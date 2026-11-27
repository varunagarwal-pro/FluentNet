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

            ReadDword(items, hklm, "reg.default_ttl", "Default TTL", "General",
                @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", "DefaultTTL", "128", true,
                description: "Maximum IP packet hop limit (64 optimal, 128 Windows default)");

            ReadDword(items, hklm, "reg.max_user_port", "MaxUserPort", "Advanced",
                @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", "MaxUserPort", "NotSet", true,
                description: "Upper ephemeral port limit (65534 expands pool; NotSet = Windows dynamic 49152-65535)");

            ReadDword(items, hklm, "reg.tcp_timed_wait_delay", "TcpTimedWaitDelay (s)", "Advanced",
                @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", "TcpTimedWaitDelay", "120", true,
                description: "Seconds a closed socket stays in TIME_WAIT before reuse (30s optimal, 120s default)");

            ReadDword(items, hklm, "reg.afd_fast_send", "AFD FastSendDatagramThreshold", "Advanced",
                @"SYSTEM\CurrentControlSet\Services\AFD\Parameters", "FastSendDatagramThreshold", "1024", true,
                description: "UDP datagram size threshold for fast I/O path (1500 aligns with Ethernet MTU)");

            ReadDword(items, hklm, "reg.network_throttling", "NetworkThrottlingIndex", "Gaming",
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "NetworkThrottlingIndex", "0x0000000a", true, hex: true,
                description: "Multimedia scheduler packet cap (0xffffffff disables throttling; 0x0000000a = 10 default)");

            ReadDword(items, hklm, "reg.system_responsiveness", "SystemResponsiveness", "Gaming",
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "SystemResponsiveness", "20", true,
                description: "CPU % reserved for background tasks during multimedia/network workloads (10 optimal, 20 default)");

            ReadDword(items, hklm, "reg.qos_non_best_effort", "QoS: NonBestEffortLimit (%)", "QoS",
                @"SOFTWARE\Policies\Microsoft\Windows\Psched", "NonBestEffortLimit", "20", true,
                description: "Reservable QoS bandwidth cap % (0 removes reservation limit)");

            ReadDword(items, hklm, "reg.qos_do_not_use_nla", "QoS: Do not use NLA", "QoS",
                @"SYSTEM\CurrentControlSet\Services\Tcpip\QoS", "Do not use NLA", "NotSet", true,
                description: "Bypass Network Location Awareness check for QoS policies (1 optimal)");

            ReadDword(items, hklm, "reg.sp_local", "LocalPriority", "Resolution",
                @"SYSTEM\CurrentControlSet\Services\Tcpip\ServiceProvider", "LocalPriority", "499", true,
                description: "Name resolution priority for local host name (4 optimal)");

            ReadDword(items, hklm, "reg.sp_hosts", "HostsPriority", "Resolution",
                @"SYSTEM\CurrentControlSet\Services\Tcpip\ServiceProvider", "HostsPriority", "500", true,
                description: "Name resolution priority for hosts file (5 optimal)");

            ReadDword(items, hklm, "reg.sp_dns", "DnsPriority", "Resolution",
                @"SYSTEM\CurrentControlSet\Services\Tcpip\ServiceProvider", "DnsPriority", "2000", true,
                description: "Name resolution priority for DNS (6 optimal)");

            ReadDword(items, hklm, "reg.sp_netbt", "NetbtPriority", "Resolution",
                @"SYSTEM\CurrentControlSet\Services\Tcpip\ServiceProvider", "NetbtPriority", "2001", true,
                description: "Name resolution priority for NetBT (7 optimal)");

            foreach (NetworkAdapterInfo nic in adapters.Where(a => a.MediaKind is AdapterMediaKind.Ethernet or AdapterMediaKind.WiFi))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string ifPath = $@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{nic.InterfaceGuid}";
                ReadDword(items, hklm, $"nic.{nic.InterfaceGuid}.tcpackfreq", $"[{nic.Alias}] TcpAckFrequency", "Gaming", ifPath, "TcpAckFrequency", "2", true,
                    description: "1 = send immediate ACK for every segment (gaming); 2 = Windows delayed ACK default");
                ReadDword(items, hklm, $"nic.{nic.InterfaceGuid}.tcpnodelay", $"[{nic.Alias}] TCPNoDelay", "Gaming", ifPath, "TCPNoDelay", "0", true,
                    description: "1 = disable Nagle coalescing for real-time gaming; 0 = Windows default");
                ReadDword(items, hklm, $"nic.{nic.InterfaceGuid}.tcpdelackticks", $"[{nic.Alias}] TcpDelAckTicks", "Gaming", ifPath, "TcpDelAckTicks", "2", true,
                    description: "Delayed ACK timer in 100ms ticks (0 = instant for gaming; 2 = 200ms default)");
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
        bool hex = false,
        string? description = null)
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
            RequiresReboot: requiresReboot,
            Description: description));
    }
}
