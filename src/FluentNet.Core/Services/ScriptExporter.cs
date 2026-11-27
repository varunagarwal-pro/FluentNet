using System.Text;
using FluentNet.Core.Models;

namespace FluentNet.Core.Services;

public static class ScriptExporter
{
    public static string ExportPowerShellApplyScript(IReadOnlyList<ChangePlanItem> items, string profileName)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# FluentNet Auditable Apply Script");
        sb.AppendLine($"# Profile: {profileName}");
        sb.AppendLine($"# Generated: {DateTimeOffset.UtcNow:O}");
        sb.AppendLine("# Run as Administrator");
        sb.AppendLine();

        foreach (ChangePlanItem item in items)
        {
            sb.AppendLine($"# {item.DisplayName}: {item.OldValue} -> {item.NewValue} (Default: {item.WindowsDefaultValue})");
            switch (item.SourceKind)
            {
                case ParameterSourceKind.Registry:
                    int lastSlash = item.ExactPath.LastIndexOf('\\');
                    if (lastSlash > 0)
                    {
                        string keyPath = item.ExactPath.Substring(0, lastSlash).Replace("HKEY_LOCAL_MACHINE\\", "HKLM:\\");
                        string valueName = item.ExactPath.Substring(lastSlash + 1);
                        if (string.Equals(item.NewValue, "NotSet", StringComparison.OrdinalIgnoreCase))
                        {
                            sb.AppendLine($"Remove-ItemProperty -Path '{keyPath}' -Name '{valueName}' -ErrorAction SilentlyContinue");
                        }
                        else
                        {
                            sb.AppendLine($"New-Item -Path '{keyPath}' -Force | Out-Null");
                            sb.AppendLine($"Set-ItemProperty -Path '{keyPath}' -Name '{valueName}' -Value {item.NewValue} -Type DWord");
                        }
                    }
                    break;

                case ParameterSourceKind.Netsh:
                    if (item.ParameterId == "tcp.loopback_large_mtu")
                    {
                        string val = item.NewValue.StartsWith("dis", StringComparison.OrdinalIgnoreCase) ? "disable" : "enable";
                        sb.AppendLine($"netsh int ipv4 set global loopbacklargemtu={val}");
                        sb.AppendLine($"netsh int ipv6 set global loopbacklargemtu={val}");
                    }
                    else
                    {
                        string? args = ApplyEngine.BuildTcpNetshFallbackArgs(item.ParameterId, item.NewValue);
                        if (!string.IsNullOrWhiteSpace(args))
                        {
                            sb.AppendLine($"netsh {args}");
                        }
                    }
                    break;

                case ParameterSourceKind.Cim:
                    EmitCimPowerShell(sb, item);
                    break;

                case ParameterSourceKind.DriverAdvancedProperty:
                    sb.AppendLine($"# NIC Driver Property: {item.ExactPath}");
                    string? alias = ExtractField(item.ExactPath, "Name");
                    string? kw = ExtractField(item.ExactPath, "RegistryKeyword");
                    if (!string.IsNullOrWhiteSpace(alias) && !string.IsNullOrWhiteSpace(kw))
                    {
                        sb.AppendLine($"Set-NetAdapterAdvancedProperty -Name '{alias}' -RegistryKeyword '{kw}' -DisplayValue '{item.NewValue}'");
                    }
                    break;
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static void EmitCimPowerShell(StringBuilder sb, ChangePlanItem item)
    {
        if (item.ParameterId.StartsWith("nic.", StringComparison.OrdinalIgnoreCase) &&
            item.ParameterId.EndsWith(".mtu", StringComparison.OrdinalIgnoreCase))
        {
            int eq = item.ExactPath.IndexOf("ifIndex=", StringComparison.OrdinalIgnoreCase);
            int end = eq >= 0 ? item.ExactPath.IndexOf(']', eq) : -1;
            string idx = eq >= 0 && end > eq ? item.ExactPath.Substring(eq + 8, end - (eq + 8)) : "0";
            sb.AppendLine($"Set-NetIPInterface -InterfaceIndex {idx} -AddressFamily IPv4 -NlMtuBytes {item.NewValue}");
            return;
        }

        string cmd = item.ParameterId.ToLowerInvariant() switch
        {
            "offload.rss" => $"Set-NetOffloadGlobalSetting -ReceiveSideScaling {item.NewValue}",
            "offload.rsc" => $"Set-NetOffloadGlobalSetting -ReceiveSegmentCoalescing {item.NewValue}",
            "tcp.autotuning" => $"Set-NetTCPSetting -SettingName Internet -AutoTuningLevelLocal {item.NewValue}",
            "tcp.heuristics" => $"Set-NetTCPSetting -SettingName Internet -ScalingHeuristics {item.NewValue}",
            "tcp.congestion" => $"netsh int tcp set supplemental template=Internet congestionprovider={item.NewValue.ToLowerInvariant()}",
            "tcp.ecn" => $"Set-NetTCPSetting -SettingName Internet -EcnCapability {item.NewValue}",
            "tcp.timestamps" => $"Set-NetTCPSetting -SettingName Internet -Timestamps {item.NewValue}",
            "tcp.fastopen" => $"Set-NetTCPSetting -SettingName Internet -FastOpen {item.NewValue}",
            "tcp.pacing" => $"Set-NetTCPSetting -SettingName Internet -PacingProfile {item.NewValue}",
            "tcp.hystart" => $"Set-NetTCPSetting -SettingName Internet -HyStart {item.NewValue}",
            "tcp.prr" => $"Set-NetTCPSetting -SettingName Internet -Prr {item.NewValue}",
            "tcp.maxsynretx" => $"netsh int tcp set global maxsynretransmissions={item.NewValue}",
            "tcp.nonsackrtt" => $"netsh int tcp set global nonsackrttresiliency={item.NewValue.ToLowerInvariant()}",
            "tcp.initialrto" => $"netsh int tcp set global initialRto={item.NewValue}",
            "tcp.minrto" => $"netsh int tcp set supplemental template=Internet minrto={item.NewValue}",
            "tcp.icw" => $"netsh int tcp set supplemental template=Internet icw={item.NewValue}",
            _ => $"# CIM Target: {item.ExactPath} => {item.NewValue}"
        };
        sb.AppendLine(cmd);
    }

    public static string ExportRegistryFile(IReadOnlyList<ChangePlanItem> items)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Windows Registry Editor Version 5.00");
        sb.AppendLine();

        var grouped = items
            .Where(i => i.SourceKind == ParameterSourceKind.Registry && i.ExactPath.Contains('\\'))
            .GroupBy(i => i.ExactPath.Substring(0, i.ExactPath.LastIndexOf('\\')), StringComparer.OrdinalIgnoreCase);

        foreach (IGrouping<string, ChangePlanItem> group in grouped)
        {
            sb.AppendLine($"[{group.Key}]");
            foreach (ChangePlanItem item in group)
            {
                string valueName = item.ExactPath.Substring(item.ExactPath.LastIndexOf('\\') + 1);
                if (string.Equals(item.NewValue, "NotSet", StringComparison.OrdinalIgnoreCase))
                {
                    sb.AppendLine($"\"{valueName}\"=-");
                }
                else
                {
                    string hexDword = FormatRegDword(item.NewValue);
                    sb.AppendLine($"\"{valueName}\"=dword:{hexDword}");
                }
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string? ExtractField(string path, string key)
    {
        string token = $"{key}='";
        int start = path.IndexOf(token, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;
        start += token.Length;
        int end = path.IndexOf('\'', start);
        return end > start ? path.Substring(start, end - start) : null;
    }

    private static string FormatRegDword(string value)
    {
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            uint.TryParse(value.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out uint hexVal))
        {
            return hexVal.ToString("x8");
        }

        if (uint.TryParse(value, out uint decVal))
        {
            return decVal.ToString("x8");
        }

        return "00000000";
    }
}
