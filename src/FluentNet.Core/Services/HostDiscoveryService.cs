using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using FluentNet.Core.Models;
using Microsoft.Win32;

namespace FluentNet.Core.Services;

public sealed class HostDiscoveryService
{
    private const int RelationProcessorCore = 0;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLogicalProcessorInformationEx(
        int relationshipType,
        IntPtr buffer,
        ref uint returnedLength);

    public Task<OsEnvironmentInfo> ProbeOsAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            using RegistryKey? cvKey = RegistryKey
                .OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");

            string productName = cvKey?.GetValue("ProductName")?.ToString() ?? "Windows";
            string displayVersion = cvKey?.GetValue("DisplayVersion")?.ToString() ?? "Unknown";
            string buildStr = cvKey?.GetValue("CurrentBuildNumber")?.ToString() ?? "0";
            _ = int.TryParse(buildStr, out int buildNumber);

            bool isWin11 = buildNumber >= 22000;
            bool is24H2OrNewer = buildNumber >= 26100;
            bool supportsBbr2 = buildNumber >= 22621;
            if (isWin11 && ContainsIgnoreCase(productName, "Windows 10"))
            {
                productName = productName.Replace("Windows 10", "Windows 11");
            }

            int logicalCores = Environment.ProcessorCount;
            int physicalCores = ProbePhysicalCoreCount(logicalCores);

            return new OsEnvironmentInfo(
                ProductName: productName,
                DisplayVersion: displayVersion,
                BuildNumber: buildNumber,
                PhysicalCoreCount: physicalCores,
                LogicalProcessorCount: logicalCores,
                IsWindows11: isWin11,
                IsWin11_24H2OrNewer: is24H2OrNewer,
                SupportsBbr2: supportsBbr2,
                RequiresBbr2LoopbackGuard: is24H2OrNewer);
        }, cancellationToken);
    }

    /// <summary>
    /// Queries exact physical CPU core count via Win32 GetLogicalProcessorInformationEx
    /// so hybrid Intel/AMD CPUs (P-cores + E-cores) report true physical core counts.
    /// </summary>
    public static int ProbePhysicalCoreCount(int logicalFallback)
    {
        try
        {
            uint len = 0;
            GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, ref len);
            if (len == 0)
            {
                return Math.Max(1, logicalFallback / 2);
            }

            IntPtr ptr = Marshal.AllocHGlobal((int)len);
            try
            {
                if (!GetLogicalProcessorInformationEx(RelationProcessorCore, ptr, ref len))
                {
                    return Math.Max(1, logicalFallback / 2);
                }

                int cores = 0;
                int offset = 0;
                while (offset + 8 <= (int)len)
                {
                    int relationship = Marshal.ReadInt32(ptr, offset);
                    int size = Marshal.ReadInt32(ptr, offset + 4);
                    if (size <= 0)
                    {
                        break;
                    }

                    if (relationship == RelationProcessorCore)
                    {
                        cores++;
                    }

                    offset += size;
                }

                return cores > 0 ? cores : Math.Max(1, logicalFallback / 2);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }
        catch
        {
            return Math.Max(1, logicalFallback / 2);
        }
    }

    public Task<IReadOnlyList<NetworkAdapterInfo>> DiscoverAdaptersAsync(
        IReadOnlyDictionary<string, IReadOnlyList<AdapterAdvancedPropertyInfo>>? advancedPropsByAlias = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<NetworkAdapterInfo>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            NetworkInterface[] nics = NetworkInterface.GetAllNetworkInterfaces();

            var results = new List<NetworkAdapterInfo>(nics.Length);
            foreach (NetworkInterface nic in nics)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsNoiseInterface(nic))
                {
                    continue;
                }

                IPInterfaceProperties ipProps = nic.GetIPProperties();
                IPv4InterfaceProperties? ipv4Props = null;
                try
                {
                    if (nic.Supports(NetworkInterfaceComponent.IPv4))
                    {
                        ipv4Props = ipProps.GetIPv4Properties();
                    }
                }
                catch
                {
                    // Adapter may not have IPv4 bound
                }

                string? ipv4 = null;
                string? ipv6 = null;
                foreach (UnicastIPAddressInformation ua in ipProps.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork && ipv4 is null)
                    {
                        ipv4 = ua.Address.ToString();
                    }
                    else if (ua.Address.AddressFamily == AddressFamily.InterNetworkV6 && !ua.Address.IsIPv6LinkLocal && ipv6 is null)
                    {
                        ipv6 = ua.Address.ToString();
                    }
                }

                string? gateway = ipProps.GatewayAddresses
                    .Select(g => g.Address)
                    .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(System.Net.IPAddress.Any))
                    ?.ToString();

                List<string> dnsServers = ipProps.DnsAddresses
                    .Where(d => d.AddressFamily == AddressFamily.InterNetwork || (!d.IsIPv6SiteLocal && !d.IsIPv6LinkLocal))
                    .Select(d => d.ToString())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(4)
                    .ToList();

                string? mac = FormatMacAddress(nic.GetPhysicalAddress());

                AdapterMediaKind media = ClassifyMedia(nic);
                long speedMbps = nic.Speed > 0 ? nic.Speed / 1_000_000L : 0;
                int mtu = ipv4Props?.Mtu ?? 1500;
                bool isPppoe = nic.NetworkInterfaceType == NetworkInterfaceType.Ppp || mtu == 1492;

                IReadOnlyList<AdapterAdvancedPropertyInfo> adv = Array.Empty<AdapterAdvancedPropertyInfo>();
                if (advancedPropsByAlias is not null && advancedPropsByAlias.TryGetValue(nic.Name, out IReadOnlyList<AdapterAdvancedPropertyInfo>? found))
                {
                    adv = found;
                }

                results.Add(new NetworkAdapterInfo(
                    Alias: nic.Name,
                    Description: nic.Description,
                    InterfaceGuid: nic.Id,
                    InterfaceIndex: ipv4Props?.Index ?? 0,
                    MediaKind: media,
                    LinkSpeedMbps: speedMbps,
                    Ipv4Mtu: mtu,
                    IsUp: nic.OperationalStatus == OperationalStatus.Up,
                    IsPppoe: isPppoe,
                    Ipv4Address: ipv4,
                    Ipv6Address: ipv6,
                    AdvancedProperties: adv,
                    MacAddress: mac,
                    GatewayAddress: gateway,
                    DnsServers: dnsServers));
            }

            return results
                .OrderByDescending(a => a.IsUp && a.LinkSpeedMbps > 0 && a.Ipv4Address is { Length: > 0 } ip && !ip.StartsWith("169.254.", StringComparison.Ordinal))
                .ThenByDescending(a => a.IsUp)
                .ThenByDescending(a => a.LinkSpeedMbps)
                .ThenBy(a => a.MediaKind)
                .ThenBy(a => a.Alias, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }, cancellationToken);
    }

    /// <summary>
    /// Filters out disconnected duplicate/ghost adapters (e.g. Wi-Fi 2, Wi-Fi 3 with 0 Mbps and no IP)
    /// while preserving all active adapters and primary physical Ethernet/Wi-Fi ports.
    /// </summary>
    public static bool IsGhostSecondaryAdapter(NetworkAdapterInfo adapter)
    {
        if (adapter.IsUp || adapter.LinkSpeedMbps > 0)
        {
            return false;
        }

        if (adapter.MediaKind is not (AdapterMediaKind.Ethernet or AdapterMediaKind.WiFi))
        {
            return true;
        }

        // Secondary numbered clones like "Wi-Fi 2", "Wi-Fi 3", "Ethernet 2" with no IP and DOWN
        bool hasNoValidIp = string.IsNullOrWhiteSpace(adapter.Ipv4Address) ||
                            (adapter.Ipv4Address is not null && adapter.Ipv4Address.StartsWith("169.254.", StringComparison.Ordinal));

        if (hasNoValidIp && adapter.Description.IndexOf('#') >= 0)
        {
            return true;
        }

        return false;
    }

    private static string? FormatMacAddress(PhysicalAddress addr)
    {
        byte[] bytes = addr.GetAddressBytes();
        if (bytes.Length == 0)
        {
            return null;
        }

        return string.Join(":", bytes.Select(b => b.ToString("X2")));
    }

    private static bool IsNoiseInterface(NetworkInterface nic)
    {
        if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
        {
            return true;
        }

        return IsNoiseName(nic.Name, nic.Description);
    }

    public static bool IsNoiseName(string name, string desc)
    {
        return ContainsIgnoreCase(name, "QoS Packet Scheduler") ||
               ContainsIgnoreCase(name, "WFP Native MAC") ||
               ContainsIgnoreCase(name, "WFP 802.3") ||
               ContainsIgnoreCase(name, "Filter Driver") ||
               ContainsIgnoreCase(name, "Filter-") ||
               ContainsIgnoreCase(desc, "QoS Packet Scheduler") ||
               ContainsIgnoreCase(desc, "LightWeight Filter") ||
               ContainsIgnoreCase(desc, "Filter Driver") ||
               ContainsIgnoreCase(desc, "WAN Miniport") ||
               ContainsIgnoreCase(desc, "Kernel Debug") ||
               ContainsIgnoreCase(desc, "Teredo") ||
               ContainsIgnoreCase(desc, "6to4") ||
               ContainsIgnoreCase(desc, "IP-HTTPS") ||
               ContainsIgnoreCase(desc, "Wi-Fi Direct Virtual");
    }

    private static AdapterMediaKind ClassifyMedia(NetworkInterface nic)
    {
        if (nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
        {
            return AdapterMediaKind.WiFi;
        }

        string desc = nic.Description;
        if (ContainsIgnoreCase(desc, "Virtual") ||
            ContainsIgnoreCase(desc, "VPN") ||
            ContainsIgnoreCase(desc, "WireGuard") ||
            ContainsIgnoreCase(desc, "TAP-") ||
            ContainsIgnoreCase(desc, "Hyper-V") ||
            ContainsIgnoreCase(desc, "Bluetooth") ||
            nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
        {
            return AdapterMediaKind.VirtualOrTunnel;
        }

        if (nic.NetworkInterfaceType == NetworkInterfaceType.Ethernet ||
            nic.NetworkInterfaceType == NetworkInterfaceType.GigabitEthernet)
        {
            return AdapterMediaKind.Ethernet;
        }

        return AdapterMediaKind.Other;
    }

    private static bool ContainsIgnoreCase(string source, string value)
        => source.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
}
