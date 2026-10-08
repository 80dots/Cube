using System.Numerics;
using Cube.Core.Mesh;

namespace Cube.Core.Tests.Mesh;

/// <summary>
/// 하프에지 메시 <c>PolyMesh</c>의 기본 불변식을 검증한다: 비매니폴드 면 거부, 면 삭제 시 고립 요소 정리,
/// 삭제 후에도 ID(슬롯 인덱스)가 안정적이고 Compact만 재번호를 매긴다는 점, 복제 독립성, 정점 인접 조회.
/// </summary>
public class PolyMeshTests
{
    /// <summary>
    /// 엣지 a-b에 이미 두 면(반대 방향)이 붙어 있을 때 세 번째 면이나 같은 방향의 중복 면은 -1로 거부되어야 하고,
    /// 메시는 여전히 유효하며 엣지 수도 늘지 않아야 한다.
    /// </summary>
    [Fact]
    public void AddFace_RejectsNonManifoldThirdFace()
    {
        var m = new PolyMesh();
        int a = m.AddVertex(Vector3.Zero), b = m.AddVertex(Vector3.UnitX), c = m.AddVertex(Vector3.UnitY), d = m.AddVertex(Vector3.UnitZ), e = m.AddVertex(-Vector3.UnitZ);
        Assert.True(m.AddFace(new[] { a, b, c }) >= 0);
        Assert.True(m.AddFace(new[] { b, a, d }) >= 0);   // a-b 엣지 공유(반대 방향)
        Assert.Equal(-1, m.AddFace(new[] { a, b, e }));   // 세 번째 면 → 거부
        Assert.Equal(-1, m.AddFace(new[] { a, b, c }));   // 같은 방향 중복 → 거부
        Assert.Empty(MeshValidator.Check(m));
        Assert.Equal(5, m.AliveEdgeCount);
    }

    /// <summary>
    /// 큐브에서 면 하나를 지우면 엣지/정점은 다른 면이 쓰고 있어 모두 남고 경계 엣지 4개가 생긴다.
    /// 반면 면 하나뿐인 평면에서 면을 지우면 고립된 엣지·정점까지 모두 정리되어 아무것도 남지 않아야 한다.
    /// </summary>
    [Fact]
    public void RemoveFace_RemovesIsolatedEdgesAndVertices()
    {
        var m = MeshBuilder.Cube();
        m.RemoveFace(0);
        Assert.Empty(MeshValidator.Check(m));
        Assert.Equal(5, m.AliveFaceCount);
        Assert.Equal(12, m.AliveEdgeCount);  // 큐브는 면 하나 지워도 엣지는 모두 남음
        Assert.Equal(8, m.AliveVertexCount);
        int boundary = 0;
        for (int e = 0; e < m.EdgeCount; e++) if (m.Edges[e].Alive && m.IsBoundaryEdge(e)) boundary++;
        Assert.Equal(4, boundary);

        var p = MeshBuilder.Plane();
        p.RemoveFace(0);
        Assert.Equal(0, p.AliveFaceCount);
        Assert.Equal(0, p.AliveEdgeCount);
        Assert.Equal(0, p.AliveVertexCount);
    }

    /// <summary>
    /// 면을 지워도 다른 요소의 ID(슬롯 인덱스)와 데이터는 그대로이고 지운 슬롯은 Alive=false로 남는다(선택·Undo 안정성).
    /// <c>Compact()</c>를 해야 비로소 빈 슬롯이 제거되고 remap 표(삭제 = -1, 뒤 슬롯은 앞으로 당겨짐)가 반환되는지 확인한다.
    /// </summary>
    [Fact]
    public void Ids_AreStableAcrossRemoval_AndCompactRemaps()
    {
        var m = MeshBuilder.Cube();
        var v7 = m.Verts[7].Position;
        m.RemoveFace(2);
        Assert.Equal(v7, m.Verts[7].Position);
        Assert.False(m.Faces[2].Alive);
        Assert.True(m.Faces[3].Alive);
        var remap = m.Compact();
        Assert.Equal(-1, remap.Faces[2]);
        Assert.Equal(2, remap.Faces[3]);
        Assert.Equal(5, m.FaceCount);
        Assert.Empty(MeshValidator.Check(m));
    }

    /// <summary>복제본의 정점 이동·면 삭제가 원본 메시에 영향을 주지 않는지(깊은 복사인지) 확인한다.</summary>
    [Fact]
    public void Clone_IsIndependent()
    {
        var m = MeshBuilder.Cube();
        var c = m.Clone();
        var v = c.Verts[0]; v.Position += Vector3.UnitX; c.Verts[0] = v;
        Assert.NotEqual(m.Verts[0].Position, c.Verts[0].Position);
        c.RemoveFace(0);
        Assert.Equal(6, m.AliveFaceCount);
    }

    /// <summary>큐브 꼭짓점은 면 3개·엣지 3개·나가는 하프에지 3개와 인접해야 한다(정점 인접 조회 함수 검증).</summary>
    [Fact]
    public void VertexAdjacency_OnCubeCorner()
    {
        var m = MeshBuilder.Cube();
        var faces = new List<int>(); var edges = new List<int>();
        m.GetVertexFaces(0, faces); m.GetVertexEdges(0, edges);
        Assert.Equal(3, faces.Count);
        Assert.Equal(3, edges.Count);
        Assert.Equal(3, m.VertexOutgoing(0).Length);
    }
}
