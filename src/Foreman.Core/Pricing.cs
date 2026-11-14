namespace Foreman.Core;

public sealed record TokenUsage(long Input, long Output, long CacheRead, long CacheWrite)
{
    public static readonly TokenUsage Zero = new(0, 0, 0, 0);

    public TokenUsage Add(long input, long output, long cacheRead, long cacheWrite) =>
        new(Input + input, Output + output, CacheRead + cacheRead, CacheWrite + cacheWrite);
}

/// <summary>
/// List prices in USD per million tokens (input, output, cache read), as published for the Claude API on
/// 3 November 2026. Cache writes are priced at 1.25x input (5-minute cache). Check when models change.
/// </summary>
public static class Pricing
{
    private static readonly Dictionary<string, (decimal Input, decimal Output, decimal CacheRead)> PerMillion = new()
    {
        ["claude-opus-5-5"] = (4m, 20m, 0.20m),
        ["claude-sonnet-5-5"] = (2m, 10m, 0.10m),
        ["claude-haiku-5-5"] = (0.10m, 0.50m, 0.01m),
        ["claude-fable-5-2"] = (8m, 40m, 0.20m),
    };

    public static decimal? Cost(string model, TokenUsage u)
    {
        if (!PerMillion.TryGetValue(model, out var p)) return null;
        return Math.Round((u.Input * p.Input + u.Output * p.Output + u.CacheRead * p.CacheRead + u.CacheWrite * p.Input * 1.25m) / 1_000_000m, 4);
    }
}
