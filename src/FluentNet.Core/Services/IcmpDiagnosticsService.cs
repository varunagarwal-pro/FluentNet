using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace FluentNet.Core.Services;

public sealed record MtuProbeResult(string Host, int LargestPayloadBytes, int LargestMtu, bool Succeeded, string StatusMessage);

public sealed record LatencySweepResult(string Host, int Sent, int Received, double PacketLossPercent, double MinMs, double AvgMs, double MaxMs, double JitterMs);

public sealed record TraceHopResult(int Hop, string Address, long RoundTripMs, string Status);

public sealed class IcmpDiagnosticsService
{
    private const byte IpFlagDontFragment = 0x02;
    private const uint IpSuccess = 0;
    private const uint IpPacketTooBig = 11009;
    private const uint IpReqTimedOut = 11010;
    private const uint IpTtlExpiredTransit = 11013;

    [StructLayout(LayoutKind.Sequential)]
    private struct IpOptionInformation
    {
        public byte Ttl;
        public byte Tos;
        public byte Flags;
        public byte OptionsSize;
        public IntPtr OptionsData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IcmpEchoReply
    {
        public uint Address;
        public uint Status;
        public uint RoundTripTime;
        public ushort DataSize;
        public ushort Reserved;
        public IntPtr Data;
        public IpOptionInformation Options;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern IntPtr IcmpCreateFile();

    [DllImport("iphlpapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IcmpCloseHandle(IntPtr icmpHandle);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint IcmpSendEcho(
        IntPtr icmpHandle,
        uint destinationAddress,
        byte[] requestData,
        ushort requestSize,
        ref IpOptionInformation requestOptions,
        byte[] replyBuffer,
        uint replySize,
        uint timeout);

    public Task<MtuProbeResult> DiscoverLargestMtuAsync(
        string host,
        int minPayload = 500,
        int maxPayload = 1472,
        int timeoutMs = 1500,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            if (!TryResolveIpv4(host, out uint destAddr))
            {
                return new MtuProbeResult(host, 0, 0, false, "DNS resolution to IPv4 failed.");
            }

            IntPtr handle = IcmpCreateFile();
            if (handle == IntPtr.Zero || handle == new IntPtr(-1))
            {
                return new MtuProbeResult(host, 0, 0, false, "Failed to open ICMP handle.");
            }

            try
            {
                int low = minPayload;
                int high = maxPayload;
                int bestPayload = -1;

                while (low <= high)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int mid = low + ((high - low) / 2);
                    (uint status, _) = SendSingleEcho(handle, destAddr, mid, ttl: 128, dontFragment: true, timeoutMs);

                    if (status == IpSuccess)
                    {
                        bestPayload = mid;
                        low = mid + 1;
                    }
                    else if (status == IpPacketTooBig || status == IpReqTimedOut)
                    {
                        high = mid - 1;
                    }
                    else
                    {
                        high = mid - 1;
                    }
                }

                if (bestPayload < 0)
                {
                    return new MtuProbeResult(host, 0, 0, false, "Host did not respond to DF ICMP probes.");
                }

                int mtu = bestPayload + 28;
                return new MtuProbeResult(host, bestPayload, mtu, true, $"Largest non-fragmented payload {bestPayload} bytes (MTU = {mtu}).");
            }
            finally
            {
                IcmpCloseHandle(handle);
            }
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<LatencySweepResult>> MeasureLatencySweepAsync(
        IEnumerable<string> hosts,
        int pingsPerHost = 4,
        int payloadBytes = 32,
        int timeoutMs = 1500,
        CancellationToken cancellationToken = default)
    {
        Task<LatencySweepResult>[] tasks = hosts
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Select(h => Task.Run(() => ProbeHostLatency(h.Trim(), pingsPerHost, payloadBytes, timeoutMs, cancellationToken), cancellationToken))
            .ToArray();

        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<TraceHopResult>> TraceRouteAsync(
        string host,
        int maxHops = 30,
        int timeoutMs = 1500,
        CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<TraceHopResult>>(() =>
        {
            var hops = new List<TraceHopResult>();
            if (!TryResolveIpv4(host, out uint destAddr))
            {
                hops.Add(new TraceHopResult(1, "*", 0, "DNS resolution failed"));
                return hops;
            }

            IntPtr handle = IcmpCreateFile();
            if (handle == IntPtr.Zero || handle == new IntPtr(-1))
            {
                hops.Add(new TraceHopResult(1, "*", 0, "ICMP handle failed"));
                return hops;
            }

            try
            {
                for (int ttl = 1; ttl <= maxHops; ttl++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    (uint status, uint rtt, uint replyAddr) = SendSingleEchoWithAddress(handle, destAddr, 32, (byte)ttl, false, timeoutMs);
                    string addrStr = replyAddr == 0 ? "*" : new IPAddress(replyAddr).ToString();
                    string statusText = status switch
                    {
                        IpSuccess => "Reached",
                        IpTtlExpiredTransit => "Transit",
                        IpReqTimedOut => "TimedOut",
                        _ => $"Status {status}"
                    };

                    hops.Add(new TraceHopResult(ttl, addrStr, rtt, statusText));
                    if (status == IpSuccess && replyAddr == destAddr)
                    {
                        break;
                    }
                }

                return hops;
            }
            finally
            {
                IcmpCloseHandle(handle);
            }
        }, cancellationToken);
    }

    private static (uint Status, uint RttMs, uint ReplyAddress) SendSingleEchoWithAddress(
        IntPtr handle,
        uint destAddr,
        int payloadSize,
        byte ttl,
        bool dontFragment,
        int timeoutMs)
    {
        byte[] payload = new byte[Math.Max(0, payloadSize)];
        var opts = new IpOptionInformation
        {
            Ttl = ttl,
            Flags = dontFragment ? IpFlagDontFragment : (byte)0
        };

        int replyStructSize = Marshal.SizeOf<IcmpEchoReply>();
        byte[] replyBuffer = new byte[replyStructSize + payload.Length + 64];

        uint count = IcmpSendEcho(handle, destAddr, payload, (ushort)payload.Length, ref opts, replyBuffer, (uint)replyBuffer.Length, (uint)timeoutMs);
        if (count == 0)
        {
            return (IpReqTimedOut, 0, 0);
        }

        GCHandle pinned = GCHandle.Alloc(replyBuffer, GCHandleType.Pinned);
        try
        {
            IcmpEchoReply reply = Marshal.PtrToStructure<IcmpEchoReply>(pinned.AddrOfPinnedObject());
            return (reply.Status, reply.RoundTripTime, reply.Address);
        }
        finally
        {
            pinned.Free();
        }
    }

    private static LatencySweepResult ProbeHostLatency(
        string host,
        int pingsPerHost,
        int payloadBytes,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        if (!TryResolveIpv4(host, out uint destAddr))
        {
            return new LatencySweepResult(host, pingsPerHost, 0, 100.0, 0, 0, 0, 0);
        }

        IntPtr handle = IcmpCreateFile();
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
        {
            return new LatencySweepResult(host, pingsPerHost, 0, 100.0, 0, 0, 0, 0);
        }

        try
        {
            var rtts = new List<double>(pingsPerHost);
            for (int i = 0; i < pingsPerHost; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                (uint status, uint rtt) = SendSingleEcho(handle, destAddr, payloadBytes, ttl: 128, dontFragment: false, timeoutMs);
                if (status == IpSuccess)
                {
                    rtts.Add(rtt);
                }
            }

            if (rtts.Count == 0)
            {
                return new LatencySweepResult(host, pingsPerHost, 0, 100.0, 0, 0, 0, 0);
            }

            double min = rtts.Min();
            double max = rtts.Max();
            double avg = rtts.Average();
            double jitter = rtts.Count > 1
                ? rtts.Zip(rtts.Skip(1), (a, b) => Math.Abs(b - a)).Average()
                : 0.0;
            double loss = ((pingsPerHost - rtts.Count) * 100.0) / pingsPerHost;

            return new LatencySweepResult(host, pingsPerHost, rtts.Count, Math.Round(loss, 1), min, Math.Round(avg, 2), max, Math.Round(jitter, 2));
        }
        finally
        {
            IcmpCloseHandle(handle);
        }
    }

    private static (uint Status, uint RttMs) SendSingleEcho(
        IntPtr handle,
        uint destAddr,
        int payloadSize,
        byte ttl,
        bool dontFragment,
        int timeoutMs)
    {
        byte[] payload = new byte[Math.Max(0, payloadSize)];
        var opts = new IpOptionInformation
        {
            Ttl = ttl,
            Flags = dontFragment ? IpFlagDontFragment : (byte)0
        };

        int replyStructSize = Marshal.SizeOf<IcmpEchoReply>();
        byte[] replyBuffer = new byte[replyStructSize + payload.Length + 64];

        uint count = IcmpSendEcho(handle, destAddr, payload, (ushort)payload.Length, ref opts, replyBuffer, (uint)replyBuffer.Length, (uint)timeoutMs);
        if (count == 0)
        {
            return (IpReqTimedOut, 0);
        }

        GCHandle pinned = GCHandle.Alloc(replyBuffer, GCHandleType.Pinned);
        try
        {
            IcmpEchoReply reply = Marshal.PtrToStructure<IcmpEchoReply>(pinned.AddrOfPinnedObject());
            return (reply.Status, reply.RoundTripTime);
        }
        finally
        {
            pinned.Free();
        }
    }

    private static bool TryResolveIpv4(string host, out uint address)
    {
        address = 0;
        try
        {
            if (IPAddress.TryParse(host, out IPAddress? parsed) && parsed.AddressFamily == AddressFamily.InterNetwork)
            {
                address = BitConverter.ToUInt32(parsed.GetAddressBytes(), 0);
                return true;
            }

            IPAddress[] ips = Dns.GetHostAddresses(host);
            IPAddress? ipv4 = ips.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork);
            if (ipv4 is null)
            {
                return false;
            }

            address = BitConverter.ToUInt32(ipv4.GetAddressBytes(), 0);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
