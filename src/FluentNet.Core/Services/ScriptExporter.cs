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
                        string val = item.NewValue.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                            ? item.NewValue
                            : item.NewValue;
                        sb.AppendLine($"New-Item -Path '{keyPath}' -Force | Out-Null");
                        sb.AppendLine($"Set-ItemProperty -Path '{keyPath}' -Name '{valueName}' -Value {val} -Type DWord");
                    }
                    break;
                case ParameterSourceKind.Netsh:
                    if (item.ParameterId == "tcp.loopback_large_mtu")
                    {
                        sb.AppendLine($"netsh int ipv4 set global loopbacklargemtu={item.NewValue}");
                        sb.AppendLine($"netsh int ipv6 set global loopbacklargemtu={item.NewValue}");
                    }
                    else
                    {
                        sb.AppendLine($"{item.ExactPath}{item.NewValue}");
                    }
                    break;
                case ParameterSourceKind.Cim:
                    sb.AppendLine($"# CIM Target: {item.ExactPath} => {item.NewValue}");
                    if (item.ExactPath.StartsWith("netsh ", StringComparison.OrdinalIgnoreCase))
                    {
                        sb.AppendLine($"{item.ExactPath}{item.NewValue}");
                    }
                    else if (item.ParameterId == "offload.rss")
                    {
                        sb.AppendLine($"Set-NetOffloadGlobalSetting -ReceiveSideScaling {item.NewValue}");
                    }
                    else if (item.ParameterId == "offload.rsc")
                    {
                        sb.AppendLine($"Set-NetOffloadGlobalSetting -ReceiveSegmentCoalescing {item.NewValue}");
                    }
                    break;
            }
            sb.AppendLine();
        }

        return sb.ToString();
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
                string hexDword = FormatRegDword(item.NewValue);
                sb.AppendLine($"\"{valueName}\"=dword:{hexDword}");
            }
            sb.AppendLine();
        }

        return sb.ToString();
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
