using System.Numerics;
using Cube.Core.Commands;
using Cube.Core.IO.Fbx;
using Cube.Core.Scene;

namespace Cube.Core.Tests.IO;

public class FbxWriterTests
{
    [Fact]
    public void Writer_RoundTrips_AllPropertyTypes_ThroughReader()
    {
        var root = new FbxNode("Root", (short)7, true, 42, 1.5f, 2.25, 123456789012L, "hello", new byte[] { 1, 2, 3 });
        var arrays = root.Add("Arrays", new float[] { 1, 2, 3 }, new double[200].Select((_, i) => i * 0.5).ToArray(), new long[] { -1, 2 }, Enumerable.Range(0, 100).ToArray(), new[] { true, false, true });
        root.Add("Empty");
        var leaf = root.Add("Leaf", FbxNode.Id("Model", "cube"));
        leaf.Add("Deep", 1);

        var bytes = FbxBinaryWriter.Write(new[] { root, new FbxNode("Second", 9) });
        Assert.True(FbxBinaryReader.IsBinaryFbx(bytes));
        Assert.Equal(7400u, BitConverter.ToUInt32(bytes, 23));
        var (version, nodes) = FbxBinaryReader.Read(bytes);
        Assert.Equal(7400, version);
        Assert.Equal(2, nodes.Count);
        var r = nodes[0];
        Assert.Equal("Root", r.Name);
        Assert.Equal((short)7, r.Prop<short>(0));
        Assert.True(r.Prop<bool>(1));
        Assert.Equal(42, r.Prop<int>(2));
        Assert.Equal(1.5f, r.Prop<float>(3));
        Assert.Equal(2.25, r.Prop<double>(4));
        Assert.Equal(123456789012L, r.Prop<long>(5));
        Assert.Equal("hello", r.Prop<string>(6));
        Assert.Equal(new byte[] { 1, 2, 3 }, r.Prop<byte[]>(7));
        var a = r.Child("Arrays")!;
        Assert.Equal(new float[] { 1, 2, 3 }, a.Prop<float[]>(0));
        Assert.Equal(200, a.Prop<double[]>(1).Length);
        Assert.Equal(99.5, a.Prop<double[]>(1)[199]);
        Assert.Equal(new long[] { -1, 2 }, a.Prop<long[]>(2));
        Assert.Equal(Enumerable.Range(0, 100).ToArray(), a.Prop<int[]>(3));
        Assert.Equal(new[] { true, false, true }, a.Prop<bool[]>(4));
        Assert.NotNull(r.Child("Empty"));
        Assert.Equal("Model::cube", FbxNode.ReadableId(r.Child("Leaf")!.Prop<string>(0)));
        Assert.Equal(1, r.Child("Leaf")!.Child("Deep")!.Prop<int>(0));
        Assert.Equal(9, nodes[1].Prop<int>(0));
        // 푸터: 16바이트 ID + 4 zero + 정렬 패딩 + 버전 + 120 zero + 16바이트 ID2
        Assert.Equal(0x0b, bytes[^1]);
        Assert.Equal(0xf8, bytes[^16]);
    }

    [Fact]
    public void Writer_CompressedArrays_DecodeToSameValues()
    {
        var data = Enumerable.Range(0, 5000).Select(i => Math.Sin(i)).ToArray();
        var n = new FbxNode("A", data);
        var compressed = FbxBinaryWriter.Write(new[] { n }, compress: true);
        var raw = FbxBinaryWriter.Write(new[] { n }, compress: false);
        Assert.True(compressed.Length < raw.Length);
        Assert.Equal(data, FbxBinaryReader.Read(compressed).nodes[0].Prop<double[]>(0));
        Assert.Equal(data, FbxBinaryReader.Read(raw).nodes[0].Prop<double[]>(0));
    }

    [Fact]
    public void SceneBuilder_Cube_WritesGeometryModelMaterialAndConnections()
    {
        var doc = new Document();
        var cube = CreatePrimitiveCommand.Cube(doc); doc.Undo.Push(cube);
        cube.Node.Local = new Transform3(new Vector3(1, 2, 3), new Vector3(0, 90, 0), new Vector3(2, 2, 2), new Vector3(0.5f, 0, 0));
        var mat = new MaterialDef { Name = "wood", Type = MaterialType.BlinnPhong, Color = new Vector3(0.8f, 0.6f, 0.4f), TexturePath = "C:/tex/wood.png" };
        doc.Undo.Push(new AddMaterialCommand(mat));
        doc.Undo.Push(new AssignMaterialCommand(new[] { cube.Node.Id }, mat.Id));

        var builder = new FbxSceneBuilder(doc);
        var top = builder.Build(new[] { cube.Node });
        Assert.Equal(1, builder.NodeCount);
        Assert.Equal(12, builder.TriangleCount);
        var bytes = FbxBinaryWriter.Write(top);
        var (_, nodes) = FbxBinaryReader.Read(bytes);
        var objects = nodes.First(n => n.Name == "Objects");
        var geom = objects.All("Geometry").Single();
        Assert.Equal("Geometry::pCube1", FbxNode.ReadableId(geom.Prop<string>(1)));
        var verts = geom.Child("Vertices")!.Prop<double[]>(0);
        Assert.Equal(24, verts.Length);
        Assert.Equal(50.0, verts.Max(), 3); // 0.5 m → 50 cm
        var poly = geom.Child("PolygonVertexIndex")!.Prop<int[]>(0);
        Assert.Equal(24, poly.Length);
        Assert.Equal(6, poly.Count(i => i < 0)); // 면마다 마지막 인덱스는 ~i
        Assert.All(poly, i => Assert.InRange(i < 0 ? ~i : i, 0, 7));
        Assert.Equal(72, geom.Child("LayerElementNormal")!.Child("Normals")!.Prop<double[]>(0).Length);
        Assert.Equal(24, geom.Child("LayerElementUV")!.Child("UVIndex")!.Prop<int[]>(0).Length);

        var model = objects.All("Model").Single();
        Assert.Equal("Mesh", model.Prop<string>(2));
        var props = model.Child("Properties70")!.All("P").ToDictionary(p => p.Prop<string>(0), p => p);
        Assert.Equal(100.0, props["Lcl Translation"].Prop<double>(4), 3);
        Assert.Equal(90.0, props["Lcl Rotation"].Prop<double>(5), 3);
        Assert.Equal(2.0, props["Lcl Scaling"].Prop<double>(4), 3);
        Assert.Equal(50.0, props["RotationPivot"].Prop<double>(4), 3);
        Assert.Equal(50.0, props["ScalingPivot"].Prop<double>(4), 3);

        var material = objects.All("Material").Single();
        Assert.Equal("Material::wood", FbxNode.ReadableId(material.Prop<string>(1)));
        Assert.Equal("Phong", material.Child("ShadingModel")!.Prop<string>(0));
        var tex = objects.All("Texture").Single();
        Assert.Equal("C:/tex/wood.png", tex.Child("FileName")!.Prop<string>(0));
        Assert.Single(objects.All("Video"));

        long modelId = model.Prop<long>(0), geomId = geom.Prop<long>(0), matId = material.Prop<long>(0), texId = tex.Prop<long>(0);
        var conns = nodes.First(n => n.Name == "Connections").All("C").ToList();
        Assert.Contains(conns, c => c.Prop<string>(0) == "OO" && c.Prop<long>(1) == modelId && c.Prop<long>(2) == 0L);
        Assert.Contains(conns, c => c.Prop<string>(0) == "OO" && c.Prop<long>(1) == geomId && c.Prop<long>(2) == modelId);
        Assert.Contains(conns, c => c.Prop<string>(0) == "OO" && c.Prop<long>(1) == matId && c.Prop<long>(2) == modelId);
        Assert.Contains(conns, c => c.Prop<string>(0) == "OP" && c.Prop<long>(1) == texId && c.Prop<long>(2) == matId && c.Prop<string>(3) == "DiffuseColor");

        var defs = nodes.First(n => n.Name == "Definitions");
        var counts = defs.All("ObjectType").ToDictionary(o => o.Prop<string>(0), o => o.Child("Count")!.Prop<int>(0));
        Assert.Equal(1, counts["Model"]); Assert.Equal(1, counts["Geometry"]); Assert.Equal(1, counts["Material"]); Assert.Equal(1, counts["Texture"]); Assert.Equal(1, counts["Video"]);
        Assert.Equal(counts.Values.Sum(), defs.Child("Count")!.Prop<int>(0)); // GlobalSettings 포함
        var gs = nodes.First(n => n.Name == "GlobalSettings").Child("Properties70")!.All("P").ToDictionary(p => p.Prop<string>(0), p => p);
        Assert.Equal(1, gs["UpAxis"].Prop<int>(4));
        Assert.Equal(1.0, gs["UnitScaleFactor"].Prop<double>(4));
    }

    [Fact]
    public void SceneBuilder_SkinnedMesh_WritesSkinClustersAndBindPose()
    {
        var doc = new Document();
        var cyl = CreatePrimitiveCommand.Cylinder(doc); doc.Undo.Push(cyl);
        var j0 = new SceneNode { Name = "joint1", Shape = new JointShape(), Local = new Transform3(new Vector3(0, -1, 0), Vector3.Zero, Vector3.One) };
        var j1 = new SceneNode { Name = "joint2", Shape = new JointShape(), Local = new Transform3(new Vector3(0, 2, 0), Vector3.Zero, Vector3.One) };
        doc.AddNode(j0, doc.Root); doc.AddNode(j1, j0);
        var mesh = cyl.Node.Mesh!;
        var skin = new SkinCluster { MeshBindWorld = cyl.Node.WorldMatrix };
        foreach (var j in new[] { j0, j1 }) { skin.Joints.Add(j.Id); Matrix4x4.Invert(j.WorldMatrix, out var inv); skin.BindInverse.Add(inv); }
        skin.EnsureSize(mesh.VertexCount);
        for (int v = 0; v < mesh.VertexCount; v++) skin.Weights[v] = mesh.Verts[v].Position.Y > 0 ? new List<(int, float)> { (1, 1f) } : new List<(int, float)> { (0, 0.75f), (1, 0.25f) };
        cyl.Node.MeshShape!.Skin = skin;

        // 메시만 선택해도 스킨이 참조하는 조인트 체인이 함께 나간다
        var builder = new FbxSceneBuilder(doc);
        var top = builder.Build(new[] { cyl.Node });
        var (_, nodes) = FbxBinaryReader.Read(FbxBinaryWriter.Write(top));
        var objects = nodes.First(n => n.Name == "Objects");
        var models = objects.All("Model").ToList();
        Assert.Equal(3, models.Count);
        Assert.Equal(2, models.Count(m => m.Prop<string>(2) == "LimbNode"));
        var deformers = objects.All("Deformer").ToList();
        Assert.Single(deformers.Where(d => d.Prop<string>(2) == "Skin"));
        var clusters = deformers.Where(d => d.Prop<string>(2) == "Cluster").ToList();
        Assert.Equal(2, clusters.Count);
        foreach (var c in clusters)
        {
            Assert.Equal(c.Child("Indexes")!.Prop<int[]>(0).Length, c.Child("Weights")!.Prop<double[]>(0).Length);
            Assert.Equal(16, c.Child("TransformLink")!.Prop<double[]>(0).Length);
        }
        // joint2 월드 y = 1 m → TransformLink 이동 성분 100 cm
        var c2 = clusters.First(c => FbxNode.ReadableId(c.Prop<string>(1)).EndsWith("joint2"));
        Assert.Equal(100.0, c2.Child("TransformLink")!.Prop<double[]>(0)[13], 3);
        var pose = objects.All("Pose").Single();
        Assert.Equal(3, pose.Child("NbPoseNodes")!.Prop<int>(0));
        Assert.Equal(2, objects.All("NodeAttribute").Count(a => a.Child("TypeFlags")!.Prop<string>(0) == "Skeleton"));
    }
}
