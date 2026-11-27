using Xunit;

namespace DittoHopper.Core.Tests;

public class CanonicalHashTests
{
    private static ulong Hash(string tag, params double[] coords)
    {
        var h = new CanonicalHash(tag, grid: 0.01);
        foreach (double c in coords) h.Add(c);
        return h.Finish();
    }

    [Fact]
    public void CoordinatesSnapToTheGrid()
    {
        Assert.Equal(Hash("curve", 1.0, 2.0), Hash("curve", 1.004, 1.996));
        Assert.NotEqual(Hash("curve", 1.0, 2.0), Hash("curve", 1.02, 2.0));
        Assert.Equal(Hash("pt", 0.0), Hash("pt", -0.0));
    }

    [Fact]
    public void TagValueKindAndOrderAllCount()
    {
        Assert.NotEqual(Hash("curve", 1, 2), Hash("mesh", 1, 2));
        Assert.NotEqual(Hash("curve", 1, 2), Hash("curve", 2, 1));
        Assert.NotEqual(
            new CanonicalHash("c", 1).Add(3.0).Finish(),
            new CanonicalHash("c", 1).Add(3L).Finish());
        Assert.Equal(
            new CanonicalHash("c", 0.5).Add(new Vec3(1, 2, 3)).Finish(),
            new CanonicalHash("c", 0.5).Add(1.0).Add(2.0).Add(3.0).Finish());
    }

    [Fact]
    public void RejectsBadGridsAndCoordinates()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CanonicalHash("c", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CanonicalHash("c", 1).Add(double.NaN));
    }
}
