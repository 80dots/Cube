using System.Numerics;
using Cube.Core.Mesh;

namespace Cube.Core.Tests.Mesh;

/// <summary>MeshOps.ExtrudeVertices(정점 스파이크): 위상(정점·면 수, 오일러, 닫힘), 꼭짓점 위치·ID, 단 나누기, 경계 정점, 이웃 정점 동시, 엣지 플래그.</summary>
public class ExtrudeVertexTests
{
    private static int Euler(PolyMesh m) => m.AliveVertexCount - m.AliveEdgeCount + m.AliveFaceCount;

    private static void AssertValid(PolyMesh m)
    {
        Assert.Empty(MeshValidator.Check(m));
        foreach (var v in Enumerable.Range(0, m.VertexCount)) if (m.Verts[v].Alive) Assert.True(m.VertexOutgoing(v).Length > 0, $"isolated v{v}");
    }

    [Fact]
    public void CubeCorner_MakesThreeSidedSpike()
    {
        var m = MeshBuilder.Cube();
        var p0 = m.Verts[0].Position;
        var apex = MeshOps.ExtrudeVertices(m, new[] { 0 }, new ExtrudeVertexOptions { Width = 0.25f, Length = 0.5f });
        AssertValid(m);
        Assert.Equal(new[] { 0 }, apex);
        Assert.Equal(11, m.AliveVertexCount);
        Assert.Equal(9, m.AliveFaceCount);
        Assert.Equal(2, Euler(m));
        Assert.True(MeshOps.IsClosed(m));
        // 꼭짓점은 모서리 바깥 대각선 방향으로 0.5
        var dir = Vector3.Normalize(p0);
        Assert.True(Vector3.Distance(m.Verts[0].Position, p0 + dir * 0.5f) < 1e-4f);
    }

    [Fact]
    public void Divisions_AddRingsAndQuads()
    {
        var m = MeshBuilder.Cube();
        MeshOps.ExtrudeVertices(m, new[] { 0 }, new ExtrudeVertexOptions { Divisions = 3 });
        AssertValid(m);
        Assert.Equal(8 + 9, m.AliveVertexCount);
        Assert.Equal(6 + 9, m.AliveFaceCount);
        Assert.Equal(2, Euler(m));
    }

    [Fact]
    public void BoundaryCorner_OpenPlane_StaysValid()
    {
        var m = MeshBuilder.Plane();
        MeshOps.ExtrudeVertices(m, new[] { 0 }, new ExtrudeVertexOptions());
        AssertValid(m);
        Assert.Equal(6, m.AliveVertexCount);
        Assert.Equal(2, m.AliveFaceCount);
    }

    [Fact]
    public void AdjacentVertices_BothSpike_NoOverlap()
    {
        var m = MeshBuilder.Cube();
        var (a, b) = m.EdgeVertices(0);
        var apex = MeshOps.ExtrudeVertices(m, new[] { a, b }, new ExtrudeVertexOptions { Width = 0.9f });
        AssertValid(m);
        Assert.Equal(2, apex.Count);
        Assert.Equal(8 + 6, m.AliveVertexCount);
        Assert.Equal(6 + 6, m.AliveFaceCount);
        Assert.True(MeshOps.IsClosed(m));
    }

    [Fact]
    public void AllCubeCorners_AndSphere_StayClosed()
    {
        var m = MeshBuilder.Cube();
        MeshOps.ExtrudeVertices(m, Enumerable.Range(0, 8), new ExtrudeVertexOptions { Width = 0.3f, Length = 0.3f, Divisions = 2 });
        AssertValid(m); Assert.True(MeshOps.IsClosed(m)); Assert.Equal(2, Euler(m));
        var s = MeshBuilder.Sphere(0.5f, 12, 6);
        MeshOps.ExtrudeVertices(s, Enumerable.Range(0, s.VertexCount).Where(v => v % 3 == 0), new ExtrudeVertexOptions { Length = -0.1f });
        AssertValid(s); Assert.True(MeshOps.IsClosed(s)); Assert.Equal(2, Euler(s));
    }

    [Fact]
    public void BaseEdges_InheritHardFlag()
    {
        var m = MeshBuilder.Cube();
        foreach (int e in Enumerable.Range(0, m.EdgeCount)) { var ed = m.Edges[e]; ed.Hard = true; m.Edges[e] = ed; }
        MeshOps.ExtrudeVertices(m, new[] { 0 }, new ExtrudeVertexOptions());
        // 원래 큐브 엣지 위에 남은 조각(r–u)은 하드 유지, 새로 생긴 옆면 엣지는 소프트
        int hard = Enumerable.Range(0, m.EdgeCount).Count(e => m.Edges[e].Alive && m.Edges[e].Hard);
        Assert.Equal(12, hard);
    }

    [Fact]
    public void IsolatedOrInvalidVertices_AreIgnored()
    {
        var m = MeshBuilder.Cube();
        int iso = m.AddVertex(new Vector3(3, 0, 0));
        Assert.Empty(MeshOps.ExtrudeVertices(m, new[] { iso, -1, 999 }, new ExtrudeVertexOptions()));
        Assert.Equal(6, m.AliveFaceCount);
    }
}
