using PaceLaps.Agent.ConsoleHost.Spikes;

namespace PaceLaps.Agent.Tests.Spikes;

public sealed class StatsTests
{
    [Fact]
    public void Median_of_odd_and_even_lists()
    {
        Assert.Equal(3, Stats.Median([5, 1, 3]));
        Assert.Equal(2.5, Stats.Median([4, 1, 3, 2]));
        Assert.True(double.IsNaN(Stats.Median([])));
    }

    [Fact]
    public void Percentile_uses_nearest_rank()
    {
        var values = Enumerable.Range(1, 50).Select(i => (double)i).ToArray();
        Assert.Equal(48, Stats.Percentile(values, 95));
        Assert.Equal(50, Stats.Percentile(values, 100));
        Assert.Equal(1, Stats.Percentile(values, 0));
    }

    [Theory]
    [InlineData("", new int[0])]
    [InlineData("  ", new int[0])]
    [InlineData("3", new[] { 3 })]
    [InlineData("3, 7-9", new[] { 3, 7, 8, 9 })]
    [InlineData("9 1;1", new[] { 1, 9 })]
    public void Parses_number_lists(string text, int[] expected)
    {
        Assert.True(Stats.TryParseNumberList(text, 50, out var numbers, out var error));
        Assert.Null(error);
        Assert.Equal(expected, numbers);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("51")]
    [InlineData("5-3")]
    [InlineData("abc")]
    [InlineData("1-2-3")]
    [InlineData("-4")]
    public void Rejects_bad_number_lists(string text)
    {
        Assert.False(Stats.TryParseNumberList(text, 50, out var numbers, out var error));
        Assert.NotNull(error);
        Assert.Empty(numbers);
    }
}
