using System.Numerics;
using Cube.Core.Commands;
using Cube.Core.IO;
using Cube.Core.Mesh;
using Cube.Core.Scene;

namespace Cube.Core.Tests.IO;

public class CubeFileFormatTests
{
    [Fact]
    public void SaveLoad_RoundTrip_PreservesHierarchyMeshAndAttributes()
    {
        var doc = new Document();
        var cube = CreatePrimitiveCommand.Cube(doc); doc.Undo.Push(cube);
        var mesh = cube.Node.Mesh!;
        int top = -1; for (int f = 0; f < mesh.FaceCount; f++) if (mesh.Faces[f].Normal.Y > 0.9f) top = f;
        doc.Undo.Push(new ExtrudeFacesCommand(cube.Node.Id, new[] { top }));
        MeshOps.SetEdgesHard(mesh, new[] { 0, 1 }, false); // 소프트 엣지 둘
        var sphere = CreatePrimitiveCommand.Sphere(doc); doc.Undo.Push(sphere);
        sphere.Node.Local = new Transform3(new Vector3(1, 2, 3), new Vector3(10, 20, 30), new Vector3(2, 2, 2));
        var child = new SceneNode { Name = "child", Local = new Transform3(new Vector3(0, 1, 0), Vector3.Zero, Vector3.One) };
        doc.AddNode(child, sphere.Node);
        child.Visible = false;

        string json = CubeFileFormat.Serialize(doc);
        var doc2 = new Document();
        CubeFileFormat.Deserialize(doc2, json);

        Assert.Equal(3, doc2.Nodes.Count);
        Assert.Equal(2, doc2.Root.Children.Count);
        var cube2 = doc2.Root.Children.First(n => n.Name == "pCube1");
        var sphere2 = doc2.Root.Children.First(n => n.Name == "pSphere1");
        Assert.Single(sphere2.Children);
        Assert.Equal("child", sphere2.Children[0].Name);
        Assert.False(sphere2.Children[0].Visible);
        Assert.Equal(sphere.Node.Local, sphere2.Local);

        var m2 = cube2.Mesh!;
        Assert.Empty(MeshValidator.Check(m2));
        Assert.Equal(mesh.AliveVertexCount, m2.AliveVertexCount);
        Assert.Equal(mesh.AliveFaceCount, m2.AliveFaceCount);
        Assert.Equal(mesh.AliveEdgeCount, m2.AliveEdgeCount);
        int hardSrc = 0, hardDst = 0;
        for (int e = 0; e < mesh.EdgeCount; e++) if (mesh.Edges[e].Alive && mesh.Edges[e].Hard) hardSrc++;
        for (int e = 0; e < m2.EdgeCount; e++) if (m2.Edges[e].Alive && m2.Edges[e].Hard) hardDst++;
        Assert.Equal(hardSrc, hardDst);
        // 코너 UV 보존: 첫 면의 UV 집합 비교
        var uvSrc = new HashSet<Vector2>(); var uvDst = new HashSet<Vector2>();
        foreach (var h in mesh.Hes) if (h.Alive) uvSrc.Add(h.Uv0);
        foreach (var h in m2.Hes) if (h.Alive) uvDst.Add(h.Uv0);
        Assert.Equal(uvSrc, uvDst);
        Assert.False(doc2.IsDirty);
        Assert.False(doc2.Undo.CanUndo);
    }

    [Fact]
    public void Deserialize_RejectsWrongFormat()
    {
        var doc = new Document();
        Assert.Throws<InvalidDataException>(() => CubeFileFormat.Deserialize(doc, "{\"format\":\"other\",\"version\":1,\"nodes\":[]}"));
        Assert.Throws<InvalidDataException>(() => CubeFileFormat.Deserialize(doc, "{\"format\":\"cube\",\"version\":99,\"nodes\":[]}"));
    }

    [Fact]
    public void SaveLoad_File()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cube_test_{Guid.NewGuid():N}.cube");
        try
        {
            var doc = new Document();
            doc.Undo.Push(CreatePrimitiveCommand.Torus(doc));
            CubeFileFormat.Save(doc, path);
            Assert.Equal(path, doc.FilePath);
            Assert.False(doc.IsDirty);
            var doc2 = new Document();
            CubeFileFormat.Load(doc2, path);
            Assert.Single(doc2.Nodes);
            Assert.Equal(doc.MeshNodes().First().Mesh!.AliveFaceCount, doc2.MeshNodes().First().Mesh!.AliveFaceCount);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
