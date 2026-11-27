using Xunit;

namespace DittoHopper.Core.Tests;

public class RigidFitTests
{
    private const double Tol = 0.01;

    /// <summary>Eight corners of a box that is neither a cube nor at the origin, so no symmetry hides a motion.</summary>
    private static readonly Vec3[] Box =
    [
        new(10, 20, 30), new(410, 20, 30), new(410, 220, 30), new(10, 220, 30),
        new(10, 20, 330), new(410, 20, 330), new(410, 220, 330), new(10, 220, 330),
    ];

    private static Xform Rotation(double degrees, Vec3 through)
    {
        double r = degrees * Math.PI / 180, c = Math.Cos(r), s = Math.Sin(r);
        // About a Z-parallel line through 'through': x' = R(x - p) + p.
        double tx = through.X - (c * through.X - s * through.Y), ty = through.Y - (s * through.X + c * through.Y);
        return new Xform([c, -s, 0, tx, s, c, 0, ty, 0, 0, 1, 0, 0, 0, 0, 1]);
    }

    private static Vec3[] Apply(Xform x, IEnumerable<Vec3> points) => [.. points.Select(x.Apply)];

    private static string Describe(Xform x) => RigidFit.Describe(x, UnitSystem.Millimeters, Tol);

    [Fact]
    public void PureTranslationIsExactAndReadsAsAMoveAlongOneAxis()
    {
        var fit = RigidFit.Fit(Box, Apply(Xform.Translation(120, 0, 0), Box), Tol);
        Assert.NotNull(fit);
        Assert.False(fit.Mirrored);
        Assert.True(fit.Transform.AlmostEquals(Xform.Translation(120, 0, 0), 1e-9));
        Assert.Equal("moved 120 mm in X", Describe(fit.Transform));
        Assert.Equal("moved 50 mm (X 30, Y -40)", Describe(Xform.Translation(30, -40, 0)));
        Assert.Equal("moved 0.8 mm in -Y", Describe(Xform.Translation(0, -0.8, 0)));
    }

    [Fact]
    public void RotationIsFoundAndDescribedAboutItsAxisLine()
    {
        var motion = Rotation(90, new Vec3(100, 0, 0));
        var fit = RigidFit.Fit(Box, Apply(motion, Box), Tol);
        Assert.NotNull(fit);
        Assert.True(fit.MaxResidual < 1e-9);
        Assert.True(fit.Transform.AlmostEquals(motion, 1e-9));
        Assert.Equal("rotated 90° about Z through (100, 0, 0)", Describe(fit.Transform));
        Assert.Equal("rotated -30° about Z", Describe(Rotation(-30, new Vec3(0, 0, 0))));
    }

    [Fact]
    public void ScrewMotionNamesTheSlideAlongTheAxis()
    {
        var screw = Xform.Translation(0, 0, 5).After(Rotation(45, new Vec3(0, 0, 0)));
        Assert.Equal("rotated 45° about Z, moved 5 mm along the axis", Describe(screw));
    }

    [Fact]
    public void MirrorHasDeterminantMinusOne()
    {
        var mirror = new Xform([-1, 0, 0, 100, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1]);   // across X = 50
        var fit = RigidFit.Fit(Box, Apply(mirror, Box), Tol);
        Assert.NotNull(fit);
        Assert.True(fit.Mirrored);
        Assert.True(fit.Transform.AlmostEquals(mirror, 1e-9));
        Assert.Equal("mirrored across the plane X = 50 mm", Describe(fit.Transform));
    }

    [Fact]
    public void PlanarSetThatAMirrorOrARotationExplainsReportsTheRotation()
    {
        Vec3[] flat = [new(0, 0, 0), new(300, 0, 0), new(300, 100, 0), new(0, 250, 0)];
        var mirror = new Xform([-1, 0, 0, 100, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1]);
        var fit = RigidFit.Fit(flat, Apply(mirror, flat), Tol);
        Assert.NotNull(fit);
        Assert.False(fit.Mirrored);
        Assert.True(fit.MaxResidual < 1e-9);
    }

    [Fact]
    public void CollinearPointsStillFitATranslation()
    {
        Vec3[] line = [new(0, 0, 0), new(1000, 0, 0)];
        var fit = RigidFit.Fit(line, Apply(Xform.Translation(0, 0, 3000), line), Tol);
        Assert.Equal("moved 3000 mm in Z", Describe(fit!.Transform));
    }

    [Fact]
    public void FitAcceptsResidualsWithinToleranceOnly()
    {
        var moved = Apply(Xform.Translation(120, 0, 0), Box);
        var noisy = moved.Select((p, i) => p + new Vec3(i % 2 == 0 ? 0.004 : -0.004, 0, 0)).ToArray();
        Assert.NotNull(RigidFit.Fit(Box, noisy, Tol));
        var bent = moved.Select((p, i) => i == 0 ? p + new Vec3(0, 0, 0.5) : p).ToArray();
        Assert.Null(RigidFit.Fit(Box, bent, Tol));
        var scaled = new Xform([2, 0, 0, 0, 0, 2, 0, 0, 0, 0, 2, 0, 0, 0, 0, 1]);
        Assert.Null(RigidFit.Fit(Box, Apply(scaled, Box), Tol));
        Assert.Null(RigidFit.Fit(Box, Box.Take(3).ToArray(), Tol));
    }

    [Fact]
    public void NonRigidTransformsListTheirRoundedMatrix()
    {
        var scaled = new Xform([2, 0, 0, 0.123456, 0, 2, 0, 0, 0, 0, 2, 0, 0, 0, 0, 1]);
        Assert.Equal("transformed by [2, 0, 0, 0.12, 0, 2, 0, 0, 0, 0, 2, 0, 0, 0, 0, 1]", Describe(scaled));
    }

    [Fact]
    public void InverseAndAfterRoundTrip()
    {
        var x = Xform.Translation(5, -7, 2).After(Rotation(33, new Vec3(10, 20, 0)));
        Assert.True(x.After(x.Inverse()!).AlmostEquals(Xform.Identity, 1e-9));
        Assert.Null(new Xform([0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1]).Inverse());
    }
}
