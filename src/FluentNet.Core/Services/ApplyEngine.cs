using System.Diagnostics;
using FluentNet.Core.Models;
using Microsoft.Win32;
#if NET8_0_OR_GREATER
using Microsoft.Management.Infrastructure;
#else
using System.Management;
#endif

namespace FluentNet.Core.Services;

/// <summary>
/// 100% Native .NET ApplyEngine: writes directly to 64-bit Registry (Microsoft.Win32.RegistryKey)
/// and Windows CIM/WMI (ROOT\StandardCimv2 via Microsoft.Management.Infrastructure / System.Management).
/// Zero PowerShell spawns. Falls back to silent hidden netsh ONLY for Windows Client SKU read-only CIM properties.
/// </summary>
public sealed class ApplyEngine
{
    public async Task<ApplyReport> ApplyPlanAsync(
        IReadOnlyList<ChangePlanItem> items,
        CancellationToken cancellationToken = default)
    {
        var results = new List<ApplyItemResult>(items.Count);

        foreach (ChangePlanItem item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ApplyItemResult res = await ApplySingleAsync(item, cancellationToken).ConfigureAwait(false);
            results.Add(res);
        }

        return new ApplyReport(results.All(r => r.Succeeded), results);
    }

    private static async Task<ApplyItemResult> ApplySingleAsync(ChangePlanItem item, CancellationToken ct)
    {
        try
        {
            switch (item.SourceKind)
            {
                case ParameterSourceKind.Registry:
                    return ApplyRegistryDword(item);

                case ParameterSourceKind.Cim:
                    return await ApplyCimNativeAsync(item, ct).ConfigureAwait(false);

                case ParameterSourceKind.DriverAdvancedProperty:
                    return ApplyDriverAdvancedPropertyNative(item);

                case ParameterSourceKind.Netsh:
                    return await ApplyNetshNativeFallbackAsync(item, ct).ConfigureAwait(false);

                default:
                    return new ApplyItemResult(item.ParameterId, false, $"Unsupported source kind: {item.SourceKind}");
            }
        }
        catch (Exception ex)
        {
            return new ApplyItemResult(item.ParameterId, false, ex.Message);
        }
    }

    private static ApplyItemResult ApplyRegistryDword(ChangePlanItem item)
    {
        int lastSlash = item.ExactPath.LastIndexOf('\\');
        if (lastSlash <= 0)
        {
            return new ApplyItemResult(item.ParameterId, false, $"Invalid registry path: {item.ExactPath}");
        }

        string subKey = item.ExactPath.Substring(0, lastSlash);
        if (subKey.StartsWith(@"HKEY_LOCAL_MACHINE\", StringComparison.OrdinalIgnoreCase))
        {
            subKey = subKey.Substring(@"HKEY_LOCAL_MACHINE\".Length);
        }

        string valueName = item.ExactPath.Substring(lastSlash + 1);
        using RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

        if (string.Equals(item.NewValue, "NotSet", StringComparison.OrdinalIgnoreCase))
        {
            using RegistryKey? key = hklm.OpenSubKey(subKey, writable: true);
            key?.DeleteValue(valueName, throwOnMissingValue: false);
            return new ApplyItemResult(item.ParameterId, true, "Deleted registry override (restored Windows default).");
        }

        if (!TryParseDword(item.NewValue, out int dwordVal))
        {
            return new ApplyItemResult(item.ParameterId, false, $"Invalid DWORD value: {item.NewValue}");
        }

        using RegistryKey targetKey = hklm.CreateSubKey(subKey, writable: true);
        targetKey.SetValue(valueName, dwordVal, RegistryValueKind.DWord);
        return new ApplyItemResult(item.ParameterId, true, $"Set HKLM REG_DWORD = {item.NewValue} (native .NET)");
    }

    private static async Task<ApplyItemResult> ApplyCimNativeAsync(ChangePlanItem item, CancellationToken ct)
    {
        // 1. Adapter MTU via native CIM MSFT_NetIPInterface
        if (item.ParameterId.StartsWith("nic.", StringComparison.OrdinalIgnoreCase) &&
            item.ParameterId.EndsWith(".mtu", StringComparison.OrdinalIgnoreCase))
        {
            return ApplyAdapterMtuNative(item);
        }

        // 2. Global Offloads (RSS / RSC) via native CIM MSFT_NetOffloadGlobalSetting
        if (item.ParameterId is "offload.rss" or "offload.rsc")
        {
            string propName = item.ParameterId == "offload.rss" ? "ReceiveSideScaling" : "ReceiveSegmentCoalescing";
            byte encoded = EncodeEnabledDisabled(item.NewValue);
            (bool ok, string msg) = ModifyCimProperty("SELECT * FROM MSFT_NetOffloadGlobalSetting", propName, encoded);
            if (ok)
            {
                return new ApplyItemResult(item.ParameterId, true, $"Set {propName}={item.NewValue} (native CIM)");
            }

            // Silent netsh fallback only if CIM rejected
            string netshArg = item.ParameterId == "offload.rss"
                ? $"int tcp set global rss={item.NewValue.ToLowerInvariant()}"
                : $"int tcp set global rsc={item.NewValue.ToLowerInvariant()}";
            (int code, string output) = await RunHiddenNetshAsync(netshArg, ct).ConfigureAwait(false);
            return code == 0
                ? new ApplyItemResult(item.ParameterId, true, $"Set {propName}={item.NewValue} (netsh fallback)")
                : new ApplyItemResult(item.ParameterId, false, $"CIM failed ({msg}); netsh exit {code}: {output}".Trim());
        }

        // 3. TCP Template settings via native CIM MSFT_NetTCPSetting[SettingName='Internet']
        if (TryMapTcpCimProperty(item.ParameterId, item.NewValue, out string tcpProp, out object tcpVal))
        {
            (bool ok, string msg) = ModifyCimProperty("SELECT * FROM MSFT_NetTCPSetting WHERE SettingName='Internet'", tcpProp, tcpVal);
            if (ok)
            {
                return new ApplyItemResult(item.ParameterId, true, $"Set MSFT_NetTCPSetting.{tcpProp}={item.NewValue} (native CIM)");
            }

            // Some Windows 10/11 Client builds mark certain Internet template fields read-only in CIM
            // and require the kernel netsh ioctl path (still zero PowerShell, hidden no-window).
            string? netshFallback = BuildTcpNetshFallbackArgs(item.ParameterId, item.NewValue);
            if (!string.IsNullOrWhiteSpace(netshFallback))
            {
                (int code, string output) = await RunHiddenNetshAsync(netshFallback!, ct).ConfigureAwait(false);
                if (code == 0)
                {
                    return new ApplyItemResult(item.ParameterId, true, $"Set {tcpProp}={item.NewValue} (native netsh ioctl)");
                }
                return new ApplyItemResult(item.ParameterId, false, $"CIM: {msg}; netsh exit {code}: {output}".Trim());
            }

            return new ApplyItemResult(item.ParameterId, false, $"CIM ModifyInstance failed: {msg}");
        }

        return new ApplyItemResult(item.ParameterId, false, $"No native CIM handler for {item.ParameterId}");
    }

    private static ApplyItemResult ApplyAdapterMtuNative(ChangePlanItem item)
    {
        if (!uint.TryParse(item.NewValue, out uint mtu) || mtu < 576 || mtu > 9000)
        {
            return new ApplyItemResult(item.ParameterId, false, $"Invalid MTU value: {item.NewValue} (expected 576-9000)");
        }

        // ExactPath format: MSFT_NetIPInterface[ifIndex=12].NlMtuBytes
        string aliasOrIdx = item.ExactPath;
        int eq = aliasOrIdx.IndexOf("ifIndex=", StringComparison.OrdinalIgnoreCase);
        if (eq < 0)
        {
            return new ApplyItemResult(item.ParameterId, false, $"Missing interface index in {item.ExactPath}");
        }

        int end = aliasOrIdx.IndexOf(']', eq);
        string idxStr = end > eq ? aliasOrIdx.Substring(eq + 8, end - (eq + 8)) : string.Empty;
        if (!uint.TryParse(idxStr, out uint ifIndex) || ifIndex == 0)
        {
            return new ApplyItemResult(item.ParameterId, false, $"Invalid interface index '{idxStr}'");
        }

        (bool ok, string msg) = ModifyCimProperty($"SELECT * FROM MSFT_NetIPInterface WHERE InterfaceIndex={ifIndex} AND AddressFamily=2", "NlMtuBytes", mtu);
        return ok
            ? new ApplyItemResult(item.ParameterId, true, $"Set IPv4 MTU={mtu} on InterfaceIndex {ifIndex} (native CIM)")
            : new ApplyItemResult(item.ParameterId, false, $"Failed to set MTU via CIM: {msg}");
    }

    private static ApplyItemResult ApplyDriverAdvancedPropertyNative(ChangePlanItem item)
    {
        // ExactPath format: MSFT_NetAdapterAdvancedPropertySettingData[Name='<alias>',RegistryKeyword='<kw>']
        string path = item.ExactPath;
        string? alias = ExtractBracketField(path, "Name");
        string? keyword = ExtractBracketField(path, "RegistryKeyword");
        if (string.IsNullOrWhiteSpace(alias) || string.IsNullOrWhiteSpace(keyword))
        {
            return new ApplyItemResult(item.ParameterId, false, $"Invalid driver property path: {path}");
        }

        string safeAlias = alias!.Replace("'", "''");
        string safeKeyword = keyword!.Replace("'", "''");
        string wql = $"SELECT * FROM MSFT_NetAdapterAdvancedPropertySettingData WHERE Name='{safeAlias}' AND RegistryKeyword='{safeKeyword}'";

        (bool ok, string msg) = ModifyCimProperty(wql, "DisplayValue", item.NewValue);
        return ok
            ? new ApplyItemResult(item.ParameterId, true, $"Set [{alias}] {keyword}={item.NewValue} (native CIM)")
            : new ApplyItemResult(item.ParameterId, false, $"Driver property CIM update failed: {msg}");
    }

    private static async Task<ApplyItemResult> ApplyNetshNativeFallbackAsync(ChangePlanItem item, CancellationToken ct)
    {
        if (item.ParameterId == "tcp.loopback_large_mtu")
        {
            string val = item.NewValue.StartsWith("dis", StringComparison.OrdinalIgnoreCase) ? "disable" : "enable";
            (int c4, string o4) = await RunHiddenNetshAsync($"int ipv4 set global loopbacklargemtu={val}", ct).ConfigureAwait(false);
            (int c6, string o6) = await RunHiddenNetshAsync($"int ipv6 set global loopbacklargemtu={val}", ct).ConfigureAwait(false);
            return c4 == 0 && c6 == 0
                ? new ApplyItemResult(item.ParameterId, true, $"Set IPv4/IPv6 loopbacklargemtu={val}.")
                : new ApplyItemResult(item.ParameterId, false, $"netsh failed: {o4} {o6}".Trim());
        }

        string? fallback = BuildTcpNetshFallbackArgs(item.ParameterId, item.NewValue);
        if (string.IsNullOrWhiteSpace(fallback))
        {
            return new ApplyItemResult(item.ParameterId, false, $"No netsh handler for {item.ParameterId}");
        }

        (int code, string output) = await RunHiddenNetshAsync(fallback!, ct).ConfigureAwait(false);
        return code == 0
            ? new ApplyItemResult(item.ParameterId, true, "Applied via netsh.")
            : new ApplyItemResult(item.ParameterId, false, $"netsh exit {code}: {output}".Trim());
    }

    private static (bool Succeeded, string Message) ModifyCimProperty(string wql, string propertyName, object newValue)
    {
        try
        {
#if NET8_0_OR_GREATER
            using CimSession session = CimSession.Create(null);
            CimInstance? target = session.QueryInstances(CimTcpProvider.StandardCimNamespace, "WQL", wql).FirstOrDefault();
            if (target is null)
            {
                return (false, $"CIM instance not found for query: {wql}");
            }

            using (target)
            {
                CimProperty? prop = target.CimInstanceProperties[propertyName];
                if (prop is null)
                {
                    return (false, $"Property '{propertyName}' not found on CIM instance.");
                }

                prop.Value = ConvertForCimType(newValue, prop.CimType);
                session.ModifyInstance(CimTcpProvider.StandardCimNamespace, target);
                return (true, "OK");
            }
#else
            using var searcher = new ManagementObjectSearcher(CimTcpProvider.StandardCimNamespace, wql);
            ManagementObject? target = searcher.Get().Cast<ManagementObject>().FirstOrDefault();
            if (target is null)
            {
                return (false, $"WMI instance not found for query: {wql}");
            }

            using (target)
            {
                target[propertyName] = newValue;
                target.Put();
                return (true, "OK");
            }
#endif
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

#if NET8_0_OR_GREATER
    private static object ConvertForCimType(object value, CimType cimType)
    {
        return cimType switch
        {
            CimType.UInt8 => Convert.ToByte(value),
            CimType.UInt16 => Convert.ToUInt16(value),
            CimType.UInt32 => Convert.ToUInt32(value),
            CimType.UInt64 => Convert.ToUInt64(value),
            CimType.SInt32 => Convert.ToInt32(value),
            CimType.Boolean => Convert.ToBoolean(value),
            CimType.String => value.ToString() ?? string.Empty,
            _ => value
        };
    }
#endif

    private static bool TryMapTcpCimProperty(string parameterId, string value, out string propName, out object encodedValue)
    {
        propName = string.Empty;
        encodedValue = 0;

        switch (parameterId.ToLowerInvariant())
        {
            case "tcp.autotuning":
                propName = "AutoTuningLevelLocal";
                encodedValue = EncodeAutoTuning(value);
                return true;
            case "tcp.heuristics":
                propName = "ScalingHeuristics";
                encodedValue = EncodeEnabledDisabled(value);
                return true;
            case "tcp.congestion":
                propName = "CongestionProvider";
                encodedValue = EncodeCongestion(value);
                return true;
            case "tcp.ecn":
                propName = "EcnCapability";
                encodedValue = EncodeEnabledDisabled(value);
                return true;
            case "tcp.timestamps":
                propName = "Timestamps";
                encodedValue = EncodeEnabledDisabled(value);
                return true;
            case "tcp.fastopen":
                propName = "FastOpen";
                encodedValue = EncodeEnabledDisabled(value);
                return true;
            case "tcp.pacing":
                propName = "PacingProfile";
                encodedValue = EncodePacingProfile(value);
                return true;
            case "tcp.hystart":
                propName = "HyStart";
                encodedValue = EncodeEnabledDisabled(value);
                return true;
            case "tcp.prr":
                propName = "Prr";
                encodedValue = EncodeEnabledDisabled(value);
                return true;
            case "tcp.nonsackrtt":
                propName = "NonSackRttResiliency";
                encodedValue = EncodeEnabledDisabled(value);
                return true;
            case "tcp.maxsynretx":
                if (uint.TryParse(value, out uint syn))
                {
                    propName = "MaxSynRetransmissions";
                    encodedValue = syn;
                    return true;
                }
                return false;
            case "tcp.initialrto":
                if (uint.TryParse(value, out uint irto))
                {
                    propName = "InitialRtoMs";
                    encodedValue = irto;
                    return true;
                }
                return false;
            case "tcp.minrto":
                if (uint.TryParse(value, out uint mrto))
                {
                    propName = "MinRtoMs";
                    encodedValue = mrto;
                    return true;
                }
                return false;
            case "tcp.icw":
                if (uint.TryParse(value, out uint icw))
                {
                    propName = "InitialCongestionWindowMss";
                    encodedValue = icw;
                    return true;
                }
                return false;
            default:
                return false;
        }
    }

    public static string? BuildTcpNetshFallbackArgs(string parameterId, string value)
    {
        string lower = value.Trim().ToLowerInvariant();
        return parameterId.ToLowerInvariant() switch
        {
            "tcp.autotuning" => $"int tcp set global autotuninglevel={lower}",
            "tcp.heuristics" => $"int tcp set heuristics {lower}",
            "tcp.congestion" => $"int tcp set supplemental template=Internet congestionprovider={lower}",
            "tcp.ecn" => $"int tcp set global ecncapability={lower}",
            "tcp.timestamps" => $"int tcp set global timestamps={lower}",
            "tcp.maxsynretx" => $"int tcp set global maxsynretransmissions={lower}",
            "tcp.nonsackrtt" => $"int tcp set global nonsackrttresiliency={lower}",
            "tcp.initialrto" => $"int tcp set global initialRto={lower}",
            "tcp.minrto" => $"int tcp set supplemental template=Internet minrto={lower}",
            "tcp.icw" => $"int tcp set supplemental template=Internet icw={lower}",
            _ => null
        };
    }

    private static byte EncodeEnabledDisabled(string value) => value.Trim().ToLowerInvariant() switch
    {
        "disabled" or "disable" or "0" => 0,
        "enabled" or "enable" or "1" => 1,
        "default" or "2" => 2,
        _ => 0
    };

    private static byte EncodeAutoTuning(string value) => value.Trim().ToLowerInvariant() switch
    {
        "disabled" or "0" => 0,
        "highlyrestricted" or "1" => 1,
        "restricted" or "2" => 2,
        "normal" or "3" => 3,
        "experimental" or "4" => 4,
        _ => 3
    };

    private static byte EncodeCongestion(string value) => value.Trim().ToLowerInvariant() switch
    {
        "default" or "0" => 0,
        "none" or "1" => 1,
        "ctcp" or "2" => 2,
        "dctcp" or "4" => 4,
        "cubic" or "5" => 5,
        "bbr2" or "6" => 6,
        _ => 5
    };

    private static byte EncodePacingProfile(string value) => value.Trim().ToLowerInvariant() switch
    {
        "off" or "0" => 0,
        "slowstart" or "1" => 1,
        "initialwindow" or "2" => 2,
        "always" or "3" => 3,
        _ => 0
    };

    private static string? ExtractBracketField(string path, string key)
    {
        string token = $"{key}='";
        int start = path.IndexOf(token, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;
        start += token.Length;
        int end = path.IndexOf('\'', start);
        return end > start ? path.Substring(start, end - start) : null;
    }

    public static bool TryParseDword(string value, out int dword)
    {
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            uint.TryParse(value.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out uint hex))
        {
            dword = unchecked((int)hex);
            return true;
        }

        if (uint.TryParse(value, out uint dec))
        {
            dword = unchecked((int)dec);
            return true;
        }

        dword = 0;
        return false;
    }

    private static async Task<(int ExitCode, string Output)> RunHiddenNetshAsync(string arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "netsh.exe",
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start netsh.exe");
        Task<string> stdoutTask = proc.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = proc.StandardError.ReadToEndAsync();

        await Task.Run(() => proc.WaitForExit(), ct).ConfigureAwait(false);
        string stdout = await stdoutTask.ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);
        return (proc.ExitCode, string.IsNullOrWhiteSpace(stderr) ? stdout : $"{stdout}\n{stderr}");
    }
}
