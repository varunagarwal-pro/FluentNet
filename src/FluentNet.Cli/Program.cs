using System.Text.Json;
using FluentNet.Core.Models;
using FluentNet.Core.Serialization;
using FluentNet.Core.Services;

namespace FluentNet.Cli;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            Console.WriteLine("FluentNet CLI — 64-bit Windows TCP/IP & NIC Optimization Worker");
            Console.WriteLine("Commands:");
            Console.WriteLine("  inspect");
            Console.WriteLine("  diag --host <host>");
            Console.WriteLine("  export --profile <Optimal|Gaming|Default> --out <dir>");
            Console.WriteLine("  apply --plan <plan.json>");
            Console.WriteLine("  rollback --snapshot <snapshot.json>");
            return 0;
        }

        string cmd = args[0].ToLowerInvariant();
        switch (cmd)
        {
            case "version":
                Console.WriteLine("FluentNet.Cli 0.1.0 (64-bit)");
                return 0;

            case "inspect":
            {
                var scanner = new HostScanner();
                HostScanReport report = await scanner.ScanAsync().ConfigureAwait(false);
                Console.WriteLine(JsonSerializer.Serialize(report, FluentNetJsonContext.Default.HostScanReport));
                return 0;
            }

            case "diag":
            {
                string host = GetArg(args, "--host") ?? "1.1.1.1";
                var icmp = new IcmpDiagnosticsService();
                MtuProbeResult mtu = await icmp.DiscoverLargestMtuAsync(host).ConfigureAwait(false);
                IReadOnlyList<LatencySweepResult> sweep = await icmp.MeasureLatencySweepAsync(new[] { host }, pingsPerHost: 3).ConfigureAwait(false);
                IReadOnlyList<TraceHopResult> trace = await icmp.TraceRouteAsync(host, maxHops: 10).ConfigureAwait(false);

                Console.WriteLine($"MTU: {mtu.StatusMessage}");
                foreach (LatencySweepResult r in sweep)
                {
                    Console.WriteLine($"Latency: {r.Host} Loss={r.PacketLossPercent}% Min={r.MinMs}ms Avg={r.AvgMs}ms Max={r.MaxMs}ms Jitter={r.JitterMs}ms");
                }
                foreach (TraceHopResult h in trace)
                {
                    Console.WriteLine($"Hop {h.Hop,2}: {h.Address,-16} {h.RoundTripMs,4} ms ({h.Status})");
                }
                return mtu.Succeeded || sweep.Any(s => s.Received > 0) ? 0 : 1;
            }

            case "export":
            {
                string profileArg = GetArg(args, "--profile") ?? "Optimal";
                string outDir = GetArg(args, "--out") ?? Environment.CurrentDirectory;
                Directory.CreateDirectory(outDir);

                ProfileKind kind = profileArg.ToLowerInvariant() switch
                {
                    "gaming" => ProfileKind.GamingLowLatency,
                    "default" => ProfileKind.WindowsDefault,
                    _ => ProfileKind.RecommendedOptimal
                };

                var scanner = new HostScanner();
                HostScanReport report = await scanner.ScanAsync().ConfigureAwait(false);
                NetworkAdapterInfo? primary = report.Adapters.FirstOrDefault(a => a.IsUp);

                var profileEngine = new ProfileEngine();
                IReadOnlyDictionary<string, string> proposed = profileEngine.BuildTargetValues(kind, report, primary);

                var diffEngine = new DiffEngine();
                IReadOnlyList<ChangePlanItem> diffs = diffEngine.ComputeDiff(report, proposed);

                string ps1Path = Path.Combine(outDir, $"FluentNet_{kind}_Apply.ps1");
                string regPath = Path.Combine(outDir, $"FluentNet_{kind}_Apply.reg");
                await File.WriteAllTextAsync(ps1Path, ScriptExporter.ExportPowerShellApplyScript(diffs, kind.ToString())).ConfigureAwait(false);
                await File.WriteAllTextAsync(regPath, ScriptExporter.ExportRegistryFile(diffs)).ConfigureAwait(false);

                Console.WriteLine($"Exported {diffs.Count} changes to:\n  {ps1Path}\n  {regPath}");
                return 0;
            }

            case "apply":
            {
                string? planPath = GetArg(args, "--plan");
                if (string.IsNullOrWhiteSpace(planPath) || !File.Exists(planPath))
                {
                    Console.Error.WriteLine("Missing or invalid --plan <file>.");
                    return 2;
                }

                string json = await File.ReadAllTextAsync(planPath).ConfigureAwait(false);
                SnapshotManifest? plan = JsonSerializer.Deserialize(json, FluentNetJsonContext.Default.SnapshotManifest);
                if (plan is null || plan.Items.Count == 0)
                {
                    Console.WriteLine("No changes to apply.");
                    return 0;
                }

                // Mandatory pre-change snapshot before touching system state
                var snapMgr = new SnapshotManager();
                string savedSnap = await snapMgr.SaveSnapshotAsync(plan).ConfigureAwait(false);
                Console.WriteLine($"Saved pre-change snapshot: {savedSnap}");

                var applier = new ApplyEngine();
                ApplyReport result = await applier.ApplyPlanAsync(plan.Items).ConfigureAwait(false);
                foreach (ApplyItemResult r in result.Results)
                {
                    Console.WriteLine($"[{(r.Succeeded ? "OK" : "FAIL")}] {r.ParameterId}: {r.Message}");
                }

                return result.AllSucceeded ? 0 : 1;
            }

            case "rollback":
            {
                string? snapPath = GetArg(args, "--snapshot");
                if (string.IsNullOrWhiteSpace(snapPath) || !File.Exists(snapPath))
                {
                    Console.Error.WriteLine("Missing or invalid --snapshot <file>.");
                    return 2;
                }

                var snapMgr = new SnapshotManager();
                SnapshotManifest? snap = await snapMgr.LoadSnapshotAsync(snapPath).ConfigureAwait(false);
                if (snap is null || snap.Items.Count == 0)
                {
                    Console.Error.WriteLine("Snapshot is empty or invalid.");
                    return 2;
                }

                var revertItems = snap.Items.Select(i => i with { OldValue = i.NewValue, NewValue = i.OldValue }).ToList();
                var applier = new ApplyEngine();
                ApplyReport result = await applier.ApplyPlanAsync(revertItems).ConfigureAwait(false);
                foreach (ApplyItemResult r in result.Results)
                {
                    Console.WriteLine($"[{(r.Succeeded ? "REVERTED" : "FAIL")}] {r.ParameterId}: {r.Message}");
                }

                return result.AllSucceeded ? 0 : 1;
            }

            default:
                Console.Error.WriteLine($"Unknown command '{args[0]}'. Run --help.");
                return 1;
        }
    }

    private static string? GetArg(string[] args, string flag)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }
        return null;
    }
}
