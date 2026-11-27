using System.Drawing;
using Rhino.DocObjects;
using Rhino.FileIO;
using Rhino.Geometry;
using Xunit;
using Xunit.Abstractions;

namespace DittoHopper.Rhino.Tests;

[Collection("Rhino")]
[Trait("Category", "RhinoIntegration")]
public sealed class SmokeTests(RhinoHost host, ITestOutputHelper output)
{
    [RhinoFact]
    public void RhinoCoreBootsAndMeasuresABox()
    {
        output.WriteLine($"RhinoCore boot: {host.BootTime.TotalSeconds:F1} s");
        host.Run(BoxVolume);
    }

    [RhinoFact]
    public void EditedPairKeepsGuidsAndUserText() => host.Run(EditedPair);

    [RhinoFact]
    public void RebuiltPairLosesGuids() => host.Run(RebuiltPair);

    private static void BoxVolume()
    {
        using var brep = Brep.CreateFromBox(new BoundingBox(Point3d.Origin, new Point3d(1, 2, 3)));
        Assert.NotNull(brep);
        Assert.True(brep.IsSolid);
        using var mass = VolumeMassProperties.Compute(brep);
        Assert.Equal(6, mass.Volume, 8);
    }

    private static void EditedPair()
    {
        var pair = FixturePair.Create("layer-rename", BuildSample,
            editB: b => b.AllLayers.Single(l => l.Name == "Walls").Name = "A-WALL");

        using var a = File3dm.Read(pair.A);
        using var b = File3dm.Read(pair.B);
        Assert.Contains(a.AllLayers, l => l.Name == "Walls");
        Assert.DoesNotContain(b.AllLayers, l => l.Name == "Walls");
        Assert.Equal(LayerId(a, "Walls"), LayerId(b, "A-WALL"));
        Assert.Equal(ObjectIds(a), ObjectIds(b));
        Assert.Equal(4, ObjectIds(b).Count);
        Assert.Equal("W-01", b.Objects.Single(o => o.Name == "wall").Attributes.GetUserString("Mark"));
        Assert.Equal("Rev B", b.Strings.GetValue("Revision"));
        Assert.Single(b.AllInstanceDefinitions);
        Assert.Single(b.AllGroups);
    }

    private static void RebuiltPair()
    {
        var pair = FixturePair.CreateFromScratch("gh-rebake", BuildSample, buildB: BuildSample);

        using var a = File3dm.Read(pair.A);
        using var b = File3dm.Read(pair.B);
        Assert.Equal(4, ObjectIds(b).Count);
        Assert.Empty(ObjectIds(a).Intersect(ObjectIds(b)));
        Assert.Equal("W-01", b.Objects.Single(o => o.Name == "wall").Attributes.GetUserString("Mark"));
    }

    /// <summary>Two layers, a box with user text, a line, a block and its instance, two objects in a group.</summary>
    private static void BuildSample(File3dm file)
    {
        file.Strings.SetString("Revision", "Rev B");
        var walls = file.AllLayers.AddLayer("Walls", Color.Firebrick);
        var furniture = file.AllLayers.AddLayer("Furniture", Color.SteelBlue);
        var group = file.AllGroups.AddGroup();

        var wall = new ObjectAttributes { LayerIndex = walls, Name = "wall" };
        wall.SetUserString("Mark", "W-01");
        wall.AddToGroup(group);
        using var box = Brep.CreateFromBox(new BoundingBox(Point3d.Origin, new Point3d(4, 0.2, 3)));
        file.Objects.AddBrep(box, wall);

        var edge = new ObjectAttributes { LayerIndex = walls, Name = "edge" };
        edge.AddToGroup(group);
        file.Objects.AddLine(new Line(Point3d.Origin, new Point3d(4, 0, 0)), edge);

        using var seat = Brep.CreateFromBox(new BoundingBox(Point3d.Origin, new Point3d(0.5, 0.5, 0.45)));
        var chair = file.AllInstanceDefinitions.Add("Chair", "", Point3d.Origin, seat,
            new ObjectAttributes { LayerIndex = furniture });
        file.Objects.AddInstanceObject(chair, Transform.Translation(1, 1, 0),
            new ObjectAttributes { LayerIndex = furniture, Name = "chair-1" });

        file.Objects.AddPoint(new Point3d(2, 2, 0), new ObjectAttributes { LayerIndex = furniture, Name = "marker" });
    }

    private static Guid LayerId(File3dm file, string name) => file.AllLayers.Single(l => l.Name == name).Id;

    /// <summary>Model objects only (block member geometry is in the table too), sorted.</summary>
    private static List<Guid> ObjectIds(File3dm file)
    {
        // File3dmObjectTable's IList indexer and CopyTo return nulls (Rhino 8.36), so LINQ's
        // Select/ToList see nulls. Plain foreach enumerates correctly.
        var ids = new List<Guid>();
        foreach (var o in file.Objects)
            if (!o.Attributes.IsInstanceDefinitionObject) ids.Add(o.Id);
        ids.Sort();
        return ids;
    }
}
