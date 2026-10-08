using System.Numerics;
using Cube.Core.Commands;
using Cube.Core.Mesh;
using Cube.Core.Scene;
using Cube.Core.Selection;

namespace Cube.Core.Tests.Mesh;

/// <summary>
/// M1 기본 폴리 편집 연산(<c>MeshOps</c>)과 관련 명령을 검증한다: Extrude(Keep Faces Together), Delete Edge/Vertex/Face,
/// Merge Vertices, Reverse, Combine/Separate, Extrude 명령의 Undo/Redo.
/// </summary>
public class MeshOpsTests
{
    /// <summary>법선 Y가 0.9보다 큰 첫 번째 살아 있는 면(큐브 윗면)을 찾는다. 없으면 -1.</summary>
    private static int TopFace(PolyMesh m)
    {
        for (int f = 0; f < m.FaceCount; f++) if (m.Faces[f].Alive && m.Faces[f].Normal.Y > 0.9f) return f;
        return -1;
    }

    /// <summary>
    /// 큐브 윗면 하나를 Extrude하면 정점 12·엣지 20·면 10의 닫힌 메시가 되고 새 캡 법선은 +Y여야 한다.
    /// 캡을 위로 올린 뒤 모든 면 법선이 바깥(중심 (0,0.5,0) 반대쪽)을 향하는지로 옆면 감김 방향까지 확인한다.
    /// </summary>
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

    /// <summary>
    /// 엣지를 공유하는 윗면·앞면을 함께 Extrude하면(Keep Faces Together) 경계 정점 6개만 복제되고 옆면 6개가 생기며,
    /// 두 캡은 여전히 엣지 하나를 공유해 하나의 영역으로 붙어 있어야 한다.
    /// </summary>
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

    /// <summary>
    /// Maya Delete Edge: 윗면 엣지를 지우면 두 면이 1×2 직사각형으로 합쳐지고, 2가가 된 양끝 정점도 함께 정리되어
    /// 면 5·엣지 9·정점 6(쿼드 3, 삼각형 2)이 되어야 한다.
    /// </summary>
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

    /// <summary>정점 하나를 지우면 그 정점을 둘러싼 세 면이 한 면으로 합쳐져 정점 7·면 4·엣지 9의 닫힌 메시가 되어야 한다.</summary>
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

    /// <summary>면을 지우면 엣지·정점은 남고 그 둘레 엣지 4개가 경계(구멍)가 되어야 한다.</summary>
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

    /// <summary>
    /// 같은 위치에 겹친 정점을 가진 떨어진 쿼드 두 개에서 Merge Vertices(임계 0.001)를 하면 정점 2쌍이 합쳐져
    /// 정점 6·엣지 7이 되고 두 면이 내부 엣지 하나를 공유하게 되어야 한다.
    /// </summary>
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

    /// <summary>
    /// Reverse는 지정한 면만이 아니라 연결 요소 전체를 뒤집어야(하프에지 일관성) 모든 법선이 안쪽을 향하고,
    /// 하드 엣지 플래그 같은 엣지 속성은 유지되어야 한다.
    /// </summary>
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

    /// <summary>
    /// 큐브 두 개(하나는 X+3)를 Combine하면 노드 하나에 정점 16·면 12가 월드 위치로 베이크되고,
    /// Separate하면 다시 큐브 두 개로 나뉘며, Undo 두 번이면 원래 두 노드(같은 ID)로 복원되는지 확인한다.
    /// </summary>
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

        // Separate: 연결 요소별로 다시 노드를 만든다(Prepare가 분리 가능 여부를 미리 판단).
        var sep = new SeparateCommand(combine.Result!.Id);
        Assert.True(sep.Prepare(doc));
        doc.Undo.Push(sep);
        Assert.Equal(2, doc.Nodes.Count);
        foreach (var n in doc.Nodes.Values) { Assert.Equal(8, n.Mesh!.AliveVertexCount); Assert.Empty(MeshValidator.Check(n.Mesh)); }

        // Undo 순서: Separate 취소 → 합친 노드 하나, Combine 취소 → 원래 두 노드.
        doc.Undo.Undo(); Assert.Single(doc.Nodes);
        doc.Undo.Undo(); Assert.Equal(2, doc.Nodes.Count);
        Assert.NotNull(doc.Find(a.Node.Id)); Assert.NotNull(doc.Find(b.Node.Id));
    }

    /// <summary>
    /// <c>ExtrudeFacesCommand</c>를 푸시하면 면이 10개가 되고 선택이 면 모드의 새 캡 하나로 바뀌어야 하며,
    /// Undo/Redo가 위상(6면 ↔ 10면)을 메시 유효성을 유지한 채 정확히 오가야 한다.
    /// </summary>
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
