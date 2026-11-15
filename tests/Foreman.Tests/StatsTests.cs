using Foreman.Evals;

namespace Foreman.Tests;

public class StatsTests
{
    [Theory]
    [InlineData(3, 0, 1, 0.0)]
    [InlineData(3, 3, 1, 1.0)]
    [InlineData(3, 1, 1, 1.0 / 3)]
    [InlineData(3, 1, 3, 1.0)]
    [InlineData(5, 2, 2, 0.7)]
    public void Pass_at_k_is_the_chance_one_of_k_runs_succeeds(int n, int c, int k, double expected) =>
        Assert.Equal(expected, Stats.PassAtK(n, c, k), 6);

    [Theory]
    [InlineData(3, 3, 3, 1.0)]
    [InlineData(3, 2, 3, 0.0)]
    [InlineData(5, 4, 2, 0.6)]
    public void Pass_hat_k_is_the_chance_all_k_runs_succeed(int n, int c, int k, double expected) =>
        Assert.Equal(expected, Stats.PassHatK(n, c, k), 6);

    [Fact]
    public void Wilson_stays_inside_zero_and_one_and_is_wide_for_small_samples()
    {
        var (low, high) = Stats.Wilson(6, 8);
        Assert.InRange(low, 0.40, 0.42);
        Assert.InRange(high, 0.92, 0.93);
        Assert.Equal(1.0, Stats.Wilson(9, 9).High);
        Assert.InRange(Stats.Wilson(9, 9).Low, 0.70, 0.71);
        Assert.Equal((0.0, 1.0), Stats.Wilson(0, 0));
    }

    [Fact]
    public void McNemar_needs_lopsided_discordant_pairs()
    {
        Assert.Equal(1.0, Stats.McNemar(0, 0));
        Assert.Equal(0.5, Stats.McNemar(0, 2), 6);
        Assert.Equal(22.0 / 1024, Stats.McNemar(1, 9), 9);
        Assert.Equal(1.0, Stats.McNemar(3, 3));
    }
}
