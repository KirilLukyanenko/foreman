namespace Foreman.Evals;

/// <summary>
/// The numbers an eval with repeated runs reports. pass@k: the chance that at least one of k runs succeeds (Chen et
/// al.'s unbiased estimator from n runs with c successes); pass^k: the chance that all k succeed (tau-bench), the one
/// that matters when a client gets one shot. Wilson intervals instead of normal ones, since the samples are small and
/// rates sit near 0 or 1. McNemar's exact test compares two versions case by case on the same tasks.
/// </summary>
public static class Stats
{
    public static double PassAtK(int n, int c, int k)
    {
        if (k > n) throw new ArgumentOutOfRangeException(nameof(k), "k can't exceed the number of runs");
        if (n - c < k) return 1;
        // 1 - C(n-c, k) / C(n, k), as a product to stay exact for small n.
        var fail = 1.0;
        for (var i = 0; i < k; i++) fail *= (double)(n - c - i) / (n - i);
        return 1 - fail;
    }

    public static double PassHatK(int n, int c, int k)
    {
        if (k > n) throw new ArgumentOutOfRangeException(nameof(k), "k can't exceed the number of runs");
        if (c < k) return 0;
        var all = 1.0;
        for (var i = 0; i < k; i++) all *= (double)(c - i) / (n - i);
        return all;
    }

    /// <summary>The 95% Wilson score interval for <paramref name="successes"/> out of <paramref name="n"/>.</summary>
    public static (double Low, double High) Wilson(int successes, int n, double z = 1.96)
    {
        if (n == 0) return (0, 1);
        var p = (double)successes / n;
        var denominator = 1 + z * z / n;
        var centre = (p + z * z / (2 * n)) / denominator;
        var half = z * Math.Sqrt(p * (1 - p) / n + z * z / (4.0 * n * n)) / denominator;
        return (Math.Max(0, centre - half), Math.Min(1, centre + half));
    }

    /// <summary>
    /// McNemar's exact two-sided p-value from the discordant pairs: <paramref name="onlyA"/> tasks only version A
    /// solved, <paramref name="onlyB"/> only B. Ties carry no information about which is better.
    /// </summary>
    public static double McNemar(int onlyA, int onlyB)
    {
        var n = onlyA + onlyB;
        if (n == 0) return 1;
        var low = Math.Min(onlyA, onlyB);
        double tail = 0;
        for (var i = 0; i <= low; i++) tail += Binomial(n, i) * Math.Pow(0.5, n);
        return Math.Min(1, 2 * tail);
    }

    private static double Binomial(int n, int k)
    {
        double r = 1;
        for (var i = 1; i <= k; i++) r = r * (n - k + i) / i;
        return r;
    }

    public static string Percent(double x) => $"{x * 100:0}%";
    public static string Interval((double Low, double High) w) => $"{Percent(w.Low)}–{Percent(w.High)}";
}
