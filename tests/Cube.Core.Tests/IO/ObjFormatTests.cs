using System.Numerics;
using Cube.Core.IO;
using Cube.Core.Mesh;
using Cube.Core.Scene;

namespace Cube.Core.Tests.IO;

public class ObjFormatTests
{
    private static string TempPath(string name) => Path.Combine(Path.GetTempPath(), $"cube-objtest-{Guid.NewGuid():N}-{name}");

    private static void AssertSamePositions(PolyMesh expected, Matrix4x4 world, PolyMesh actual, float tol = 1e-5f)
    {
        var got = new List<Vector3>();
        for (int v = 0; v < actual.VertexCount; v++) if (actual.Verts[v].Alive) got.Add(actual.Verts[v].Position);
        for (int v = 0; v < expected.VertexCount; v++)
        {
            if (!expected.Verts[v].Alive) continue;
            var p = Vector3.Transform(expected.Verts[v].Position, world);
            Assert.True(got.Any(q => (q - p).Length() <= tol), $"vertex {p} not found in read mesh");
        }
    }

    private static void AssertSameFaceUvs(PolyMesh expected, PolyMesh actual)
    {
        var le = new List<int>(); var la = new List<int>();
        int fa = 0;
        for (int fe = 0; fe < expected.FaceCount; fe++)
        {
            if (!expected.Faces[fe].Alive) continue;
            while (!actual.Faces[fa].Alive) fa++;
            int ne = expected.GetFaceHalfEdges(fe, le);
            int na = actual.GetFaceHalfEdges(fa, la);
            Assert.Equal(ne, na);
            for (int i = 0; i < ne; i++)
                Assert.True((expected.Hes[le[i]].Uv0 - actual.Hes[la[i]].Uv0).Length() < 1e-6f, $"face {fe} corner {i} uv differs");
            fa++;
        }
    }

    [Fact]
    public void Cube_RoundTrip_PreservesCountsPositionsAndUvs()
    {
        var mesh = MeshBuilder.Cube();
        var node = new SceneNode { Name = "pCube1", Shape = new MeshShape(mesh) };
        string path = TempPath("cube.obj");
        try
        {
            ObjFormat.Write(path, new[] { node });
            var objs = ObjFormat.Read(path);
            Assert.Single(objs);
            Assert.Equal("pCube1", objs[0].Name);
            var m2 = objs[0].Mesh;
            Assert.Empty(MeshValidator.Check(m2));
            Assert.Equal(0, objs[0].SkippedFaces);
            Assert.True(objs[0].HadNormals);
            Assert.Equal(mesh.AliveVertexCount, m2.AliveVertexCount);
            Assert.Equal(mesh.AliveFaceCount, m2.AliveFaceCount);
            Assert.Equal(mesh.AliveEdgeCount, m2.AliveEdgeCount);
            AssertSamePositions(mesh, Matrix4x4.Identity, m2);
            AssertSameFaceUvs(mesh, m2);
            // 면 방향(CCW) 보존: 각 면 노멀이 원본과 같은 방향
            for (int f = 0; f < 6; f++) Assert.True(Vector3.Dot(mesh.Faces[f].Normal, m2.Faces[f].Normal) > 0.99f);
            // 코너 노멀이 파일에서 그대로 들어왔는지
            Assert.True((mesh.Hes[0].Normal - m2.Hes[0].Normal).Length() < 1e-5f);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TwoTransformedNodes_RoundTrip_BakesWorldSpace()
    {
        var doc = new Document();
        var parent = new SceneNode
        {
            Name = "parent node",
            Shape = new MeshShape(MeshBuilder.Cube()),
            Local = new Transform3(new Vector3(1, 2, 3), new Vector3(10, 20, 30), new Vector3(2, 1, 0.5f), new Vector3(0.5f, 0, 0)),
        };
        var child = new SceneNode
        {
            Name = "child",
            Shape = new MeshShape(MeshBuilder.Cylinder(0.5f, 1f, 8, true)),
            Local = new Transform3(new Vector3(0, 1, 0), new Vector3(0, 45, 0), Vector3.One),
        };
        doc.AddNode(parent);
        doc.AddNode(child, parent);

        string path = TempPath("two.obj");
        try
        {
            ObjFormat.Write(path, new[] { parent, child });
            string text = File.ReadAllText(path);
            Assert.Contains("o parent_node\n", text);
            Assert.Contains("o child\n", text);

            var objs = ObjFormat.Read(path);
            Assert.Equal(2, objs.Count);
            foreach (var o in objs) Assert.Empty(MeshValidator.Check(o.Mesh));

            var pm = parent.Mesh!; var cm = child.Mesh!;
            Assert.Equal(pm.AliveVertexCount, objs[0].Mesh.AliveVertexCount);
            Assert.Equal(pm.AliveFaceCount, objs[0].Mesh.AliveFaceCount);
            Assert.Equal(cm.AliveVertexCount, objs[1].Mesh.AliveVertexCount);
            Assert.Equal(cm.AliveFaceCount, objs[1].Mesh.AliveFaceCount);
            AssertSamePositions(pm, parent.WorldMatrix, objs[0].Mesh);
            AssertSamePositions(cm, child.WorldMatrix, objs[1].Mesh);
            AssertSameFaceUvs(pm, objs[0].Mesh);
            AssertSameFaceUvs(cm, objs[1].Mesh);
            // n각형(원기둥 캡 8각형) 유지
            Assert.Contains(objs[1].Mesh.Faces.Where(f => f.Alive).Select((f, i) => i), i => objs[1].Mesh.FaceDegree(i) == 8);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void LocalSpace_Write_IgnoresTransform()
    {
        var node = new SceneNode { Name = "n", Shape = new MeshShape(MeshBuilder.Cube()), Local = new Transform3(new Vector3(5, 5, 5), Vector3.Zero, Vector3.One) };
        string text = ObjFormat.WriteToString(new[] { node }, worldSpace: false);
        var objs = ObjFormat.ReadFromString(text);
        AssertSamePositions(node.Mesh!, Matrix4x4.Identity, objs[0].Mesh);
    }

    [Fact]
    public void Read_HandlesNegativeIndices_MissingVtVn_AndReversedFaces()
    {
        // vt/vn 없는 삼각형 둘, 음수 인덱스, 두 번째 면이 뒤집혀 있음(같은 방향 엣지 → 뒤집어 재시도)
        string obj = "v 0 0 0\nv 1 0 0\nv 1 1 0\nv 0 1 0\n" +
                     "f -4 -3 -2\n" +   // (0,1,2) CCW
                     "f 1 3 4\n";        // (0,2,3) CCW — 엣지 0-2 를 반대 방향으로 공유해야 하는데 같은 방향이면 거부 → 여기서는 정상
        var objs = ObjFormat.ReadFromString(obj);
        Assert.Single(objs);
        var m = objs[0].Mesh;
        Assert.Empty(MeshValidator.Check(m));
        Assert.Equal(2, m.AliveFaceCount);
        Assert.Equal(4, m.AliveVertexCount);
        Assert.False(objs[0].HadNormals);
        Assert.True(m.Faces[0].Normal.Z > 0.99f); // Recompute로 노멀 계산

        string flipped = "v 0 0 0\nv 1 0 0\nv 1 1 0\nv 0 1 0\n" +
                         "f 1 2 3\n" +
                         "f 1 4 3\n";   // (0,3,2): 엣지 0-2 가 첫 면과 같은 방향 → -1 → 뒤집어 (2,3,0) 으로 추가
        var objs2 = ObjFormat.ReadFromString(flipped);
        var m2 = objs2[0].Mesh;
        Assert.Empty(MeshValidator.Check(m2));
        Assert.Equal(2, m2.AliveFaceCount);
        Assert.Equal(0, objs2[0].SkippedFaces);
    }

    [Fact]
    public void Read_GroupsByObjectAndSkipsNonManifold()
    {
        string obj = "o a\nv 0 0 0\nv 1 0 0\nv 1 1 0\nvt 0 0\nvt 1 0\nvt 1 1\n" +
                     "f 1/1 2/2 3/3\n" +
                     "o b\nv 0 0 1\nv 1 0 1\nv 1 1 1\nv 0 1 1\n" +
                     "f 4 5 6\nf 4 6 7\nf 4 7 6\n"; // 세 번째 면: 엣지 4-6 에 3번째 면 → 뒤집어도 비매니폴드 → skip
        var objs = ObjFormat.ReadFromString(obj);
        Assert.Equal(2, objs.Count);
        Assert.Equal("a", objs[0].Name);
        Assert.Equal("b", objs[1].Name);
        Assert.Equal(1, objs[0].Mesh.AliveFaceCount);
        Assert.Equal(new Vector2(1, 1), objs[0].Mesh.Hes[2].Uv0);
        Assert.Equal(2, objs[1].Mesh.AliveFaceCount);
        Assert.Equal(1, objs[1].SkippedFaces);
        Assert.Empty(MeshValidator.Check(objs[1].Mesh));
    }

    [Fact]
    public void Exporter_WritesFileAndReportsCounts()
    {
        var doc = new Document();
        var node = new SceneNode { Name = "pCube1", Shape = new MeshShape(MeshBuilder.Cube()) };
        doc.AddNode(node);
        var exp = new ObjExporter();
        Assert.Contains(".obj", exp.Extensions);
        string path = TempPath("exp.obj");
        try
        {
            var r = exp.Export(doc, new[] { node }, path, ExportPreset.Generic);
            Assert.True(r.Ok, r.Message);
            Assert.Equal(1, r.NodeCount);
            Assert.Equal(12, r.TriangleCount);
            Assert.True(File.Exists(path));

            var imp = new ObjImporter();
            var ir = imp.Import(path, doc, ImportOptions.Default);
            Assert.True(ir.Ok, ir.Message);
            Assert.Single(ir.Nodes);
            Assert.Equal(6, ir.Nodes[0].Mesh!.AliveFaceCount);
        }
        finally { File.Delete(path); }
    }
}
