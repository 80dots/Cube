using System.Numerics;
using Cube.Core.Mesh;
using Cube.Core.Rig;
using Cube.Core.Scene;
using Cube.Core.Uv;
using Cube.Core.Commands;

namespace Cube.Core.Tests.Uv;

public class AutoSeamsTests
{
    [Fact]
    public void Cube_AllEdgesAreSeams()
    {
        var m = MeshBuilder.Cube();
        var seams = AutoSeams.Select(m);
        Assert.Equal(12, seams.Count);
        Assert.Equal(6, AutoSeams.Regions(m, seams).Count);
    }

    [Fact]
    public void Cylinder_RimsPlusOneCut_GivesThreeDiskRegions()
    {
        var m = MeshBuilder.Cylinder(segments: 12, caps: true);
        var seams = AutoSeams.Select(m);
        var regions = AutoSeams.Regions(m, seams);
        Assert.Equal(3, regions.Count);
        // 옆면 영역은 세로 엣지 하나가 더 잘려 원반이 된다: 심 = 림 24 + 세로 1
        Assert.Equal(25, seams.Count);
    }

    [Fact]
    public void Sphere_SplitsIntoHalves()
    {
        var m = MeshBuilder.Sphere(0.5f, 12, 6);
        var seams = AutoSeams.Select(m);
        var regions = AutoSeams.Regions(m, seams);
        Assert.True(regions.Count >= 2 && regions.Count <= 4, $"regions {regions.Count}");
    }

    [Fact]
    public void AutoWrap_ProducesLayoutInUnitSquare()
    {
        var m = MeshBuilder.Cylinder(segments: 12, caps: true);
        int seams = UvOps.AutoWrap(m);
        Assert.True(seams > 0);
        var t = UvTopology.Build(m);
        Assert.True(t.ShellCount >= 3, $"shells {t.ShellCount}");
        foreach (var p in t.Points) Assert.True(p.Uv.X >= -1e-3f && p.Uv.X <= 1.001f && p.Uv.Y >= -1e-3f && p.Uv.Y <= 1.001f, $"uv {p.Uv}");
    }

    [Fact]
    public void OrientJoints_PrimaryAxisAimsAtChild_AndChildWorldKept()
    {
        var doc = new Document();
        var a = new SceneNode { Name = "a", Shape = new JointShape() };
        var b = new SceneNode { Name = "b", Shape = new JointShape(), Local = new Transform3(new Vector3(0, 2, 1), Vector3.Zero, Vector3.One) };
        var c = new SceneNode { Name = "c", Shape = new JointShape(), Local = new Transform3(new Vector3(1, 0, 0), Vector3.Zero, Vector3.One) };
        doc.AddNode(a); doc.AddNode(b, a); doc.AddNode(c, b);
        var cWorldBefore = c.WorldMatrix.Translation; var bWorldBefore = b.WorldMatrix.Translation;
        JointOps.Orient(new[] { a }, new OrientOptions { Primary = Axis.X, Secondary = Axis.Y, SecondaryWorld = Axis.Y });
        // a의 로컬 X축이 b를 향한다
        var ax = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitX, a.WorldMatrix));
        var aim = Vector3.Normalize(bWorldBefore);
        Assert.True(Vector3.Dot(ax, aim) > 0.999f, $"ax {ax} aim {aim}");
        Assert.True(Vector3.Distance(b.WorldMatrix.Translation, bWorldBefore) < 1e-4f);
        Assert.True(Vector3.Distance(c.WorldMatrix.Translation, cWorldBefore) < 1e-4f);
        // b의 X축은 c를 향함
        var bx = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitX, b.WorldMatrix));
        Assert.True(Vector3.Dot(bx, Vector3.Normalize(cWorldBefore - bWorldBefore)) > 0.999f);
    }

    [Fact]
    public void MirrorJoints_ReflectsPositions_AndRenames()
    {
        var doc = new Document();
        var root = new SceneNode { Name = "L_arm", Shape = new JointShape(), Local = new Transform3(new Vector3(1, 2, 0), Vector3.Zero, Vector3.One) };
        var hand = new SceneNode { Name = "L_hand", Shape = new JointShape(), Local = new Transform3(new Vector3(1, 0, 0.5f), Vector3.Zero, Vector3.One) };
        doc.AddNode(root); doc.AddNode(hand, root);
        var mirrored = JointOps.Mirror(root, Axis.X, Vector3.Zero, "L_", "R_", n => n);
        Assert.Equal("R_arm", mirrored.Name);
        Assert.Single(mirrored.Children);
        Assert.Equal("R_hand", mirrored.Children[0].Name);
        Assert.True(Vector3.Distance(mirrored.WorldMatrix.Translation, new Vector3(-1, 2, 0)) < 1e-4f);
        Assert.True(Vector3.Distance(mirrored.Children[0].WorldMatrix.Translation, new Vector3(-2, 2, 0.5f)) < 1e-4f, $"{mirrored.Children[0].WorldMatrix.Translation}");
    }

    [Fact]
    public void InsertJoint_KeepsChildWorld_AndUndoes()
    {
        var doc = new Document();
        var a = new SceneNode { Name = "a", Shape = new JointShape() };
        var b = new SceneNode { Name = "b", Shape = new JointShape(), Local = new Transform3(new Vector3(0, 2, 0), Vector3.Zero, Vector3.One) };
        doc.AddNode(a); doc.AddNode(b, a);
        var cmd = new InsertJointCommand(a.Id, b.Id, 0.25f);
        doc.Undo.Push(cmd);
        Assert.NotNull(cmd.Joint);
        Assert.Same(cmd.Joint, b.Parent);
        Assert.True(Vector3.Distance(cmd.Joint!.WorldMatrix.Translation, new Vector3(0, 0.5f, 0)) < 1e-4f);
        Assert.True(Vector3.Distance(b.WorldMatrix.Translation, new Vector3(0, 2, 0)) < 1e-4f);
        doc.Undo.Undo();
        Assert.Same(a, b.Parent);
        Assert.Null(doc.Find(cmd.Joint.Id));
        Assert.True(Vector3.Distance(b.WorldMatrix.Translation, new Vector3(0, 2, 0)) < 1e-4f);
    }

    [Fact]
    public void Polygon_FromPoints_IsOneFace_WithNormalHint()
    {
        var pts = new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 0, -1), new Vector3(0, 0, -1) };
        var m = MeshBuilder.Polygon(pts, Vector3.UnitY);
        Assert.Equal(1, m.AliveFaceCount);
        Assert.Equal(4, m.AliveVertexCount);
        Assert.True(Vector3.Dot(Vector3.Normalize(MeshNormals.FaceNormalUnnormalized(m, 0)), Vector3.UnitY) > 0.99f);
    }

    [Fact]
    public void CubeFile_RoundTrips_LightsAndMaterials()
    {
        var doc = new Document();
        var mat = new MaterialDef { Name = "red", Type = MaterialType.Pbr, Color = new Vector3(1, 0, 0), Metallic = 0.3f, Roughness = 0.2f };
        doc.Undo.Push(new AddMaterialCommand(mat));
        var cube = CreatePrimitiveCommand.Cube(doc); doc.Undo.Push(cube);
        doc.Undo.Push(new AssignMaterialCommand(new[] { cube.Node.Id }, mat.Id));
        var light = new SceneNode { Name = "spot1", Shape = new LightShape { Type = LightType.Spot, Color = new Vector3(1, 0.5f, 0.2f), Intensity = 3, Range = 7, SpotAngle = 30 } };
        doc.AddNode(light);
        var json = Core.IO.CubeFileFormat.Serialize(doc);
        var doc2 = new Document();
        Core.IO.CubeFileFormat.Deserialize(doc2, json);
        Assert.Single(doc2.Materials);
        Assert.Equal(MaterialType.Pbr, doc2.Materials[0].Type);
        Assert.Equal(doc2.Materials[0].Id, doc2.MeshNodes().Single().MaterialId);
        var l2 = doc2.LightNodes().Single().Light!;
        Assert.Equal(LightType.Spot, l2.Type);
        Assert.Equal(30f, l2.SpotAngle);
        Assert.Equal(3f, l2.Intensity);
    }
}
