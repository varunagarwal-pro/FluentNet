using System.Diagnostics;
using FluentNet.Core.Models;
using Microsoft.Win32;

namespace FluentNet.Core.Services;

public sealed record ApplyItemResult(string ParameterId, bool Succeeded, string Message);

public sealed record ApplyReport(bool AllSucceeded, IReadOnlyList<ApplyItemResult> Results);

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

                case ParameterSourceKind.Netsh:
                    if (item.ParameterId == "tcp.loopback_large_mtu")
                    {
                        (int c4, string o4) = await RunProcessAsync("netsh", $"int ipv4 set global loopbacklargemtu={item.NewValue}", ct).ConfigureAwait(false);
                        (int c6, string o6) = await RunProcessAsync("netsh", $"int ipv6 set global loopbacklargemtu={item.NewValue}", ct).ConfigureAwait(false);
                        return c4 == 0 && c6 == 0
                            ? new ApplyItemResult(item.ParameterId, true, "Applied IPv4/IPv6 loopbacklargemtu.")
                            : new ApplyItemResult(item.ParameterId, false, $"netsh failed: {o4} {o6}".Trim());
                    }
                    else
                    {
                        string args = BuildNetshArgs(item);
                        (int code, string output) = await RunProcessAsync("netsh", args, ct).ConfigureAwait(false);
                        return code == 0
                            ? new ApplyItemResult(item.ParameterId, true, "Applied via netsh.")
                            : new ApplyItemResult(item.ParameterId, false, $"netsh exit {code}: {output}".Trim());
                    }

                case ParameterSourceKind.Cim:
                    return await ApplyCimOrNetshAsync(item, ct).ConfigureAwait(false);

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
            return new ApplyItemResult(item.ParameterId, true, "Deleted registry value (restored NotSet).");
        }

        if (!TryParseDword(item.NewValue, out int dwordVal))
        {
            return new ApplyItemResult(item.ParameterId, false, $"Invalid DWORD value: {item.NewValue}");
        }

        using RegistryKey targetKey = hklm.CreateSubKey(subKey, writable: true);
        targetKey.SetValue(valueName, dwordVal, RegistryValueKind.DWord);
        return new ApplyItemResult(item.ParameterId, true, $"Set REG_DWORD = {item.NewValue}");
    }

    private static async Task<ApplyItemResult> ApplyCimOrNetshAsync(ChangePlanItem item, CancellationToken ct)
    {
        if (item.ExactPath.StartsWith("netsh ", StringComparison.OrdinalIgnoreCase))
        {
            string args = BuildNetshArgs(item);
            (int code, string output) = await RunProcessAsync("netsh", args, ct).ConfigureAwait(false);
            return code == 0
                ? new ApplyItemResult(item.ParameterId, true, "Applied via netsh.")
                : new ApplyItemResult(item.ParameterId, false, $"netsh exit {code}: {output}".Trim());
        }

        // Fallback to PowerShell NetTCPIP cmdlet only for CIM properties lacking direct netsh syntax
        string psCmd = item.ParameterId switch
        {
            "offload.rss" => $"Set-NetOffloadGlobalSetting -ReceiveSideScaling {item.NewValue}",
            "offload.rsc" => $"Set-NetOffloadGlobalSetting -ReceiveSegmentCoalescing {item.NewValue}",
            "tcp.maxsynretx" => $"Set-NetTCPSetting -SettingName Internet -MaxSynRetransmissions {item.NewValue}",
            "tcp.nonsackrtt" => $"Set-NetTCPSetting -SettingName Internet -NonSackRttResiliency {item.NewValue}",
            "tcp.initialrto" => $"Set-NetTCPSetting -SettingName Internet -InitialRto {item.NewValue}",
            "tcp.minrto" => $"Set-NetTCPSetting -SettingName Internet -MinRto {item.NewValue}",
            _ => string.Empty
        };

        if (string.IsNullOrWhiteSpace(psCmd))
        {
            return new ApplyItemResult(item.ParameterId, false, $"No apply handler for {item.ParameterId}");
        }

        (int psCode, string psOut) = await RunProcessAsync("powershell.exe", $"-NoProfile -NonInteractive -Command \"{psCmd}\"", ct).ConfigureAwait(false);
        return psCode == 0
            ? new ApplyItemResult(item.ParameterId, true, $"Applied: {psCmd}")
            : new ApplyItemResult(item.ParameterId, false, $"PowerShell exit {psCode}: {psOut}".Trim());
    }

    private static string BuildNetshArgs(ChangePlanItem item)
    {
        string raw = item.ExactPath.StartsWith("netsh ", StringComparison.OrdinalIgnoreCase)
            ? item.ExactPath.Substring(6)
            : item.ExactPath;

        if (raw.EndsWith("=", StringComparison.Ordinal))
        {
            return raw + item.NewValue;
        }

        return $"{raw} {item.NewValue}";
    }

    private static bool TryParseDword(string value, out int dword)
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

    private static async Task<(int ExitCode, string Output)> RunProcessAsync(string fileName, string arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {fileName}");
        Task<string> stdoutTask = proc.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = proc.StandardError.ReadToEndAsync();

        await Task.Run(() => proc.WaitForExit(), ct).ConfigureAwait(false);
        string stdout = await stdoutTask.ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);
        return (proc.ExitCode, string.IsNullOrWhiteSpace(stderr) ? stdout : $"{stdout}\n{stderr}");
    }
}
