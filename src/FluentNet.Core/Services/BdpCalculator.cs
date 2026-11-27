namespace FluentNet.Core.Services;

public enum BandwidthUnit
{
    Kbps,
    Mbps,
    Gbps
}

public sealed record BdpResult(
    long BdpBytes,
    long BdpBits,
    int Mss,
    long SuggestedRwinBytes,
    int RecommendedScaleFactor);

public static class BdpCalculator
{
    public static BdpResult Calculate(double bandwidth, BandwidthUnit unit, double latencyMs, int mtu = 1500)
    {
        if (bandwidth <= 0 || latencyMs <= 0 || mtu <= 40)
        {
            return new BdpResult(0, 0, Math.Max(0, mtu - 40), 0, 0);
        }

        double multiplier = unit switch
        {
            BandwidthUnit.Kbps => 1_000.0,
            BandwidthUnit.Mbps => 1_000_000.0,
            BandwidthUnit.Gbps => 1_000_000_000.0,
            _ => 1_000_000.0
        };

        double bitsPerSec = bandwidth * multiplier;
        double bdpBitsExact = bitsPerSec * (latencyMs / 1000.0);
        long bdpBits = (long)Math.Min(long.MaxValue, Math.Round(bdpBitsExact));
        long bdpBytes = (long)Math.Min(long.MaxValue, Math.Round(bdpBitsExact / 8.0));

        int mss = mtu - 40;
        long multiples = Math.Max(1, (long)Math.Ceiling((double)bdpBytes / mss));
        long suggestedRwin = multiples * mss;

        int scaleFactor = 0;
        long window = 65535;
        while (window < suggestedRwin && scaleFactor < 14)
        {
            scaleFactor++;
            window = 65535L << scaleFactor;
        }

        return new BdpResult(bdpBytes, bdpBits, mss, suggestedRwin, scaleFactor);
    }
}
