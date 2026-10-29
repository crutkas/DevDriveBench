using DevDriveBench.Engine.Model;
using DevDriveBench.Engine.Run;

namespace DevDriveBench.Engine.Tests;

public class BenchStatsTests
{
    [Fact]
    public void SingleRunIsReportedAsIs()
    {
        Assert.Equal(7.5, BenchStats.Representative(new[] { 7.5 }));
        Assert.False(BenchStats.IsDiscarded(0, 1));
    }

    [Fact]
    public void TwoRunsDiscardTheFirst()
    {
        Assert.Equal(4.0, BenchStats.Representative(new[] { 9.0, 4.0 }));
        Assert.True(BenchStats.IsDiscarded(0, 2));
        Assert.False(BenchStats.IsDiscarded(1, 2));
    }

    [Fact]
    public void ManyRunsReportMedianOfTheRest()
    {
        // First (100) discarded; median of {3, 1, 2} is 2.
        Assert.Equal(2.0, BenchStats.Representative(new[] { 100.0, 3.0, 1.0, 2.0 }));

        // Even remainder: mean of the middle two of {1, 2, 3, 4} = 2.5.
        Assert.Equal(2.5, BenchStats.Representative(new[] { 100.0, 4.0, 1.0, 3.0, 2.0 }));
    }

    [Fact]
    public void EmptyIsZero()
    {
        Assert.Equal(0, BenchStats.Representative(Array.Empty<double>()));
        Assert.Equal(0, BenchStats.Median(Array.Empty<double>()));
    }

    [Theory]
    [InlineData(10.0, 5.0, Better.Lower, 2.0)]
    [InlineData(500.0, 1000.0, Better.Higher, 2.0)]
    [InlineData(0.0, 5.0, Better.Lower, 0.0)]
    public void AdvantageFollowsDirection(double system, double dev, Better better, double expected)
    {
        Assert.Equal(expected, BenchStats.Advantage(system, dev, better), 6);
    }
}
