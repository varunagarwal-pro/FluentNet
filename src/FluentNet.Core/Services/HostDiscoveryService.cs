using System.Net.NetworkInformation;
using System.Net.Sockets;
using FluentNet.Core.Models;
using Microsoft.Win32;

namespace FluentNet.Core.Services;

public sealed class HostDiscoveryService
{
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
            int physicalCores = Math.Max(1, logicalCores / 2);

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
                    AdvancedProperties: adv));
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

    private static bool IsNoiseInterface(NetworkInterface nic)
    {
        if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
        {
            return true;
        }

        string name = nic.Name;
        string desc = nic.Description;

        return IsNoiseName(nic.Name, nic.Description);
    }

    public static bool IsNoiseName(string name, string desc)
    {
        // Filter NDIS LightWeight Filters, QoS Packet Schedulers, WAN Miniports, and Microsoft Kernel Debuggers
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
