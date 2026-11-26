using FluentNet.Core.Models;
using Microsoft.Win32;

namespace FluentNet.Core.Services;

public sealed class Registry64Provider
{
    public Task<IReadOnlyList<NetworkParameterState>> ReadAllAsync(
        IReadOnlyList<NetworkAdapterInfo> adapters,
        CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<NetworkParameterState>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

            var items = new List<NetworkParameterState>();

            ReadDword(items, hklm, "reg.default_ttl", "Default TTL", "General", @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", "DefaultTTL", "128", true);
            ReadDword(items, hklm, "reg.max_user_port", "MaxUserPort", "Advanced", @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", "MaxUserPort", "49152-65535 (Dynamic)", true);
            ReadDword(items, hklm, "reg.tcp_timed_wait_delay", "TcpTimedWaitDelay (s)", "Advanced", @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", "TcpTimedWaitDelay", "120", true);

            ReadDword(items, hklm, "reg.network_throttling", "NetworkThrottlingIndex", "Gaming", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "NetworkThrottlingIndex", "10", true, hex: true);
            ReadDword(items, hklm, "reg.system_responsiveness", "SystemResponsiveness", "Gaming", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "SystemResponsiveness", "20", true);

            ReadDword(items, hklm, "reg.qos_non_best_effort", "QoS: NonBestEffortLimit", "QoS", @"SOFTWARE\Policies\Microsoft\Windows\Psched", "NonBestEffortLimit", "20", true);
            ReadDword(items, hklm, "reg.qos_do_not_use_nla", "QoS: Do not use NLA", "QoS", @"SYSTEM\CurrentControlSet\Services\Tcpip\QoS", "Do not use NLA", "NotSet", true);

            ReadDword(items, hklm, "reg.sp_local", "LocalPriority", "Resolution", @"SYSTEM\CurrentControlSet\Services\Tcpip\ServiceProvider", "LocalPriority", "499", true);
            ReadDword(items, hklm, "reg.sp_hosts", "HostsPriority", "Resolution", @"SYSTEM\CurrentControlSet\Services\Tcpip\ServiceProvider", "HostsPriority", "500", true);
            ReadDword(items, hklm, "reg.sp_dns", "DnsPriority", "Resolution", @"SYSTEM\CurrentControlSet\Services\Tcpip\ServiceProvider", "DnsPriority", "2000", true);
            ReadDword(items, hklm, "reg.sp_netbt", "NetbtPriority", "Resolution", @"SYSTEM\CurrentControlSet\Services\Tcpip\ServiceProvider", "NetbtPriority", "2001", true);

            foreach (NetworkAdapterInfo nic in adapters.Where(a => a.MediaKind is AdapterMediaKind.Ethernet or AdapterMediaKind.WiFi))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string ifPath = $@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{nic.InterfaceGuid}";
                ReadDword(items, hklm, $"nic.{nic.InterfaceGuid}.tcpackfreq", $"[{nic.Alias}] TcpAckFrequency", "Gaming", ifPath, "TcpAckFrequency", "2", true);
                ReadDword(items, hklm, $"nic.{nic.InterfaceGuid}.tcpnodelay", $"[{nic.Alias}] TCPNoDelay", "Gaming", ifPath, "TCPNoDelay", "0", true);
                ReadDword(items, hklm, $"nic.{nic.InterfaceGuid}.tcpdelackticks", $"[{nic.Alias}] TcpDelAckTicks", "Gaming", ifPath, "TcpDelAckTicks", "2", true);
            }

            return items;
        }, cancellationToken);
    }

    private static void ReadDword(
        List<NetworkParameterState> list,
        RegistryKey hklm,
        string id,
        string displayName,
        string category,
        string subKeyPath,
        string valueName,
        string defaultValue,
        bool requiresReboot,
        bool hex = false)
    {
        using RegistryKey? key = hklm.OpenSubKey(subKeyPath, writable: false);
        object? raw = key?.GetValue(valueName);
        bool exists = raw is not null;

        string current = exists
            ? (hex && raw is int iv ? $"0x{unchecked((uint)iv):x8}" : raw!.ToString() ?? "NotSet")
            : "NotSet";

        list.Add(new NetworkParameterState(
            Id: id,
            DisplayName: displayName,
            Category: category,
            CurrentValue: current,
            WindowsDefaultValue: defaultValue,
            ExactPath: $@"HKEY_LOCAL_MACHINE\{subKeyPath}\{valueName}",
            SourceKind: ParameterSourceKind.Registry,
            SupportStatus: exists ? ParameterSupportStatus.Supported : ParameterSupportStatus.NotSet,
            RequiresReboot: requiresReboot));
    }
}
