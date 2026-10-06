using System.Numerics;
using Cube.Core.Commands;
using Cube.Core.Mesh;
using Cube.Core.Scene;
using Cube.Core.Selection;

namespace Cube.Core.Tests.Mesh;

public class MeshOpsTests
{
    private static int TopFace(PolyMesh m)
    {
        for (int f = 0; f < m.FaceCount; f++) if (m.Faces[f].Alive && m.Faces[f].Normal.Y > 0.9f) return f;
        return -1;
    }

    [Fact]
    public void ExtrudeFaces_SingleFace_OnCube()
    {
        var m = MeshBuilder.Cube();
        int top = TopFace(m);
        var newFaces = MeshOps.ExtrudeFaces(m, new[] { top });
        MeshNormals.Recompute(m);
        Assert.Single(newFaces);
        Assert.Equal(12, m.AliveVertexCount);
        Assert.Equal(20, m.AliveEdgeCount);
        Assert.Equal(10, m.AliveFaceCount);
        Assert.Empty(MeshValidator.Check(m));
        Assert.Equal(2, m.AliveVertexCount - m.AliveEdgeCount + m.AliveFaceCount);
        for (int e = 0; e < m.EdgeCount; e++) if (m.Edges[e].Alive) Assert.False(m.IsBoundaryEdge(e));
        // 새 캡 면의 노멀은 여전히 +Y
        Assert.True(m.Faces[newFaces[0]].Normal.Y > 0.99f);
        // 캡을 올리면 측면 쿼드의 노멀이 바깥을 향한다
        var verts = new List<int>(); m.GetFaceVertices(newFaces[0], verts);
        foreach (int v in verts) { var vt = m.Verts[v]; vt.Position += Vector3.UnitY; m.Verts[v] = vt; }
        MeshNormals.Recompute(m);
        for (int f = 0; f < m.FaceCount; f++)
            if (m.Faces[f].Alive) Assert.True(Vector3.Dot(m.Faces[f].Normal, m.FaceCentroid(f) - new Vector3(0, 0.5f, 0)) > 0, $"face {f} normal inward");
    }

    [Fact]
    public void ExtrudeFaces_TwoAdjacentFaces_KeepTogether()
    {
        var m = MeshBuilder.Cube();
        int top = TopFace(m);
        int front = -1;
        for (int f = 0; f < m.FaceCount; f++) if (m.Faces[f].Normal.Z > 0.9f) front = f;
        var newFaces = MeshOps.ExtrudeFaces(m, new[] { top, front });
        MeshNormals.Recompute(m);
        Assert.Equal(2, newFaces.Count);
        Assert.Empty(MeshValidator.Check(m));
        // 경계 정점 6개 복제, 측면 쿼드 6개
        Assert.Equal(14, m.AliveVertexCount);
        Assert.Equal(12, m.AliveFaceCount);
        Assert.Equal(2, m.AliveVertexCount - m.AliveEdgeCount + m.AliveFaceCount);
        // 두 캡은 여전히 엣지를 공유
        var e0 = new List<int>(); var e1 = new List<int>();
        m.GetFaceHalfEdges(newFaces[0], e0); m.GetFaceHalfEdges(newFaces[1], e1);
        var shared = e0.Select(h => m.Hes[h].Edge).Intersect(e1.Select(h => m.Hes[h].Edge));
        Assert.Single(shared);
    }

    [Fact]
    public void DeleteEdge_MergesFaces()
    {
        var m = MeshBuilder.Cube();
        int top = TopFace(m);
        var hes = new List<int>(); m.GetFaceHalfEdges(top, hes);
        int e = m.Hes[hes[0]].Edge;
        MeshOps.DeleteEdges(m, new[] { e });
        MeshNormals.Recompute(m);
        Assert.Empty(MeshValidator.Check(m));
        // Maya Delete Edge/Vertex: 두 면이 합쳐지고, 2가가 된 양 끝 정점도 정리된다
        Assert.Equal(5, m.AliveFaceCount);
        Assert.Equal(9, m.AliveEdgeCount);
        Assert.Equal(6, m.AliveVertexCount);
        Assert.Equal(2, m.AliveVertexCount - m.AliveEdgeCount + m.AliveFaceCount);
        // 합쳐진 면(1×2 직사각형)과 뒷면·바닥은 쿼드, 정점이 녹은 양옆 면은 삼각형
        int quads = 0, tris = 0;
        for (int f = 0; f < m.FaceCount; f++) if (m.Faces[f].Alive) { if (m.FaceDegree(f) == 4) quads++; else if (m.FaceDegree(f) == 3) tris++; }
        Assert.Equal(3, quads);
        Assert.Equal(2, tris);
    }

    [Fact]
    public void DeleteVertex_MergesSurroundingFaces()
    {
        var m = MeshBuilder.Cube();
        MeshOps.DeleteVertices(m, new[] { 0 });
        MeshNormals.Recompute(m);
        Assert.Empty(MeshValidator.Check(m));
        Assert.Equal(7, m.AliveVertexCount);
        Assert.Equal(4, m.AliveFaceCount);
        Assert.Equal(9, m.AliveEdgeCount);
        Assert.Equal(2, m.AliveVertexCount - m.AliveEdgeCount + m.AliveFaceCount);
    }

    [Fact]
    public void DeleteFaces_LeavesBoundary()
    {
        var m = MeshBuilder.Cube();
        MeshOps.DeleteFaces(m, new[] { TopFace(m) });
        Assert.Empty(MeshValidator.Check(m));
        Assert.Equal(5, m.AliveFaceCount);
        int boundary = 0;
        for (int e = 0; e < m.EdgeCount; e++) if (m.Edges[e].Alive && m.IsBoundaryEdge(e)) boundary++;
        Assert.Equal(4, boundary);
    }

    [Fact]
    public void MergeVertices_JoinsTwoQuads()
    {
        var m = new PolyMesh();
        int a0 = m.AddVertex(new(0, 0, 0)), a1 = m.AddVertex(new(1, 0, 0)), a2 = m.AddVertex(new(1, 0, -1)), a3 = m.AddVertex(new(0, 0, -1));
        int b0 = m.AddVertex(new(1, 0, 0)), b1 = m.AddVertex(new(2, 0, 0)), b2 = m.AddVertex(new(2, 0, -1)), b3 = m.AddVertex(new(1, 0, -1));
        m.AddFace(new[] { a0, a1, a2, a3 }); m.AddFace(new[] { b0, b1, b2, b3 });
        Assert.Equal(8, m.AliveEdgeCount);
        int merged = MeshOps.MergeVertices(m, new[] { a1, a2, b0, b3 }, 0.001f);
        Assert.Equal(2, merged);
        Assert.Empty(MeshValidator.Check(m));
        Assert.Equal(6, m.AliveVertexCount);
        Assert.Equal(7, m.AliveEdgeCount);
        Assert.Equal(2, m.AliveFaceCount);
        int shared = 0;
        for (int e = 0; e < m.EdgeCount; e++) if (m.Edges[e].Alive && !m.IsBoundaryEdge(e)) shared++;
        Assert.Equal(1, shared);
    }

    [Fact]
    public void Reverse_FlipsNormal()
    {
        var m = MeshBuilder.Cube();
        int top = TopFace(m);
        MeshOps.ReverseFaces(m, new[] { top });
        MeshNormals.Recompute(m);
        Assert.Empty(MeshValidator.Check(m));
        Assert.Equal(6, m.AliveFaceCount);
        // 연결 요소 전체가 뒤집혀 모든 노멀이 안쪽을 향한다
        for (int f = 0; f < m.FaceCount; f++)
            if (m.Faces[f].Alive) Assert.True(Vector3.Dot(m.Faces[f].Normal, m.FaceCentroid(f)) < 0);
        // 하드 플래그 유지
        for (int e = 0; e < m.EdgeCount; e++) if (m.Edges[e].Alive) Assert.True(m.Edges[e].Hard);
    }

    [Fact]
    public void Combine_And_Separate_RoundTrip()
    {
        var doc = new Document();
        var a = CreatePrimitiveCommand.Cube(doc); doc.Undo.Push(a);
        var b = CreatePrimitiveCommand.Cube(doc); doc.Undo.Push(b);
        b.Node.Local = new Transform3(new Vector3(3, 0, 0), Vector3.Zero, Vector3.One);
        var combine = new CombineCommand(new[] { a.Node.Id, b.Node.Id });
        doc.Undo.Push(combine);
        Assert.Single(doc.Nodes);
        var mesh = combine.Result!.Mesh!;
        Assert.Equal(16, mesh.AliveVertexCount);
        Assert.Equal(12, mesh.AliveFaceCount);
        Assert.Empty(MeshValidator.Check(mesh));
        Assert.Contains(mesh.Verts, v => v.Alive && MathF.Abs(v.Position.X - 3.5f) < 1e-4f); // 월드 위치 베이크

        var sep = new SeparateCommand(combine.Result!.Id);
        Assert.True(sep.Prepare(doc));
        doc.Undo.Push(sep);
        Assert.Equal(2, doc.Nodes.Count);
        foreach (var n in doc.Nodes.Values) { Assert.Equal(8, n.Mesh!.AliveVertexCount); Assert.Empty(MeshValidator.Check(n.Mesh)); }

        doc.Undo.Undo(); Assert.Single(doc.Nodes);
        doc.Undo.Undo(); Assert.Equal(2, doc.Nodes.Count);
        Assert.NotNull(doc.Find(a.Node.Id)); Assert.NotNull(doc.Find(b.Node.Id));
    }

    [Fact]
    public void ExtrudeCommand_UndoRedo_RestoresTopology()
    {
        var doc = new Document();
        var a = CreatePrimitiveCommand.Cube(doc); doc.Undo.Push(a);
        var mesh = a.Node.Mesh!;
        int top = TopFace(mesh);
        var cmd = new ExtrudeFacesCommand(a.Node.Id, new[] { top });
        doc.Undo.Push(cmd);
        Assert.True(cmd.DidChange);
        Assert.Equal(10, mesh.AliveFaceCount);
        Assert.Equal(SelectMode.Face, doc.Selection.Mode);
        Assert.Single(doc.Selection.GetComponents(a.Node.Id).Faces);
        doc.Undo.Undo();
        Assert.Equal(6, mesh.AliveFaceCount);
        Assert.Empty(MeshValidator.Check(mesh));
        doc.Undo.Redo();
        Assert.Equal(10, mesh.AliveFaceCount);
        Assert.Empty(MeshValidator.Check(mesh));
    }
}
