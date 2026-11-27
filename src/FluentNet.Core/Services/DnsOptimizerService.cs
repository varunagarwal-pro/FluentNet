using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace FluentNet.Core.Services;

public sealed record DnsPreset(
    string Name,
    string Tagline,
    string PrimaryIpv4,
    string SecondaryIpv4);

public sealed record DnsBenchmarkResult(
    DnsPreset Preset,
    double LatencyMs,
    bool Reachable,
    string StatusText);

/// <summary>
/// Native .NET DNS resolver benchmarking, Win32 dnsapi.dll cache flushing, and iphlpapi.dll ARP cache flushing.
/// </summary>
public sealed class DnsOptimizerService
{
    [DllImport("dnsapi.dll", EntryPoint = "DnsFlushResolverCache")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DnsFlushResolverCacheNative();

    [DllImport("iphlpapi.dll", EntryPoint = "FlushIpNetTable")]
    private static extern uint FlushIpNetTableNative(uint dwIfIndex);

    public static IReadOnlyList<DnsPreset> Presets { get; } = new[]
    {
        new DnsPreset("Cloudflare", "Fastest privacy-first DNS", "1.1.1.1", "1.0.0.1"),
        new DnsPreset("Google Public DNS", "Global Anycast reliability", "8.8.8.8", "8.8.4.4"),
        new DnsPreset("Quad9 Security", "Malware & threat blocking", "9.9.9.9", "149.112.112.112"),
        new DnsPreset("AdGuard DNS", "Built-in ad & tracker filter", "94.140.14.14", "94.140.15.15"),
        new DnsPreset("OpenDNS", "Cisco enterprise filtering", "208.67.222.222", "208.67.220.220")
    };

    public static bool FlushDnsCache()
    {
        try
        {
            return DnsFlushResolverCacheNative();
        }
        catch
        {
            return false;
        }
    }

    public static bool FlushArpCache(int interfaceIndex)
    {
        if (interfaceIndex <= 0) return false;
        try
        {
            return FlushIpNetTableNative((uint)interfaceIndex) == 0;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<(bool Succeeded, string Message)> RenewDhcpLeaseAsync(CancellationToken ct = default)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ipconfig.exe",
                Arguments = "/renew",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc is null) return (false, "Could not start DHCP renewal.");
            Task<string> outTask = proc.StandardOutput.ReadToEndAsync();
            Task<string> errTask = proc.StandardError.ReadToEndAsync();
            bool exited = await Task.Run(() => proc.WaitForExit(10000), ct).ConfigureAwait(false);
            if (!exited)
            {
                try { proc.Kill(); } catch { /* ignore */ }
                return (false, "DHCP renewal timed out.");
            }
            await Task.WhenAll(outTask, errTask).ConfigureAwait(false);
            return proc.ExitCode == 0
                ? (true, "DHCP lease renewed.")
                : (false, "DHCP renewal did not complete (adapter may use a static IP).");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public async Task<IReadOnlyList<DnsBenchmarkResult>> BenchmarkResolversAsync(
        int timeoutMs = 1500,
        CancellationToken cancellationToken = default)
    {
        Task<DnsBenchmarkResult>[] tasks = Presets
            .Select(p => ProbeDnsAsync(p, timeoutMs, cancellationToken))
            .ToArray();

        DnsBenchmarkResult[] results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results
            .OrderByDescending(r => r.Reachable)
            .ThenBy(r => r.LatencyMs)
            .ToList();
    }

    private static async Task<DnsBenchmarkResult> ProbeDnsAsync(DnsPreset preset, int timeoutMs, CancellationToken ct)
    {
        var samples = new List<double>(3);
        for (int i = 0; i < 3; i++)
        {
            ct.ThrowIfCancellationRequested();
            double? ms = await MeasureTcpConnectMsAsync(preset.PrimaryIpv4, 53, timeoutMs, ct).ConfigureAwait(false);
            if (ms.HasValue) samples.Add(ms.Value);
        }

        if (samples.Count == 0)
        {
            return new DnsBenchmarkResult(preset, 9999, false, "Timed out");
        }

        double avg = Math.Round(samples.Average(), 1);
        return new DnsBenchmarkResult(preset, avg, true, $"{avg:0.0} ms");
    }

    public static async Task<double?> MeasureTcpConnectMsAsync(string host, int port, int timeoutMs, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            var sw = Stopwatch.StartNew();
            Task connectTask = client.ConnectAsync(host, port);
            Task delayTask = Task.Delay(timeoutMs, ct);
            Task winner = await Task.WhenAny(connectTask, delayTask).ConfigureAwait(false);
            sw.Stop();

            if (winner == connectTask && !connectTask.IsFaulted && client.Connected)
            {
                return sw.Elapsed.TotalMilliseconds;
            }
        }
        catch
        {
            // Ignore unreachable endpoint
        }

        return null;
    }

    public static string GradeLatencySweep(LatencySweepResult result)
    {
        if (result.Received == 0 || result.PacketLossPercent >= 10) return "F";
        if (result.PacketLossPercent > 2) return "D";
        if (result.AvgMs <= 20 && result.JitterMs <= 3 && result.PacketLossPercent == 0) return "A+";
        if (result.AvgMs <= 45 && result.JitterMs <= 8 && result.PacketLossPercent == 0) return "A";
        if (result.AvgMs <= 80 && result.JitterMs <= 15) return "B";
        return "C";
    }
}
