using System.Numerics;
using Cube.Core.Mesh;

namespace Cube.Core.Tests.Mesh;

public class MeshOpsLoopBevelBridgeTests
{
    private static int EdgeBetween(PolyMesh m, Func<Vector3, bool> a, Func<Vector3, bool> b)
    {
        for (int e = 0; e < m.EdgeCount; e++)
        {
            if (!m.Edges[e].Alive) continue;
            var (x, y) = m.EdgeVertices(e);
            var px = m.Verts[x].Position; var py = m.Verts[y].Position;
            if ((a(px) && b(py)) || (a(py) && b(px))) return e;
        }
        return -1;
    }

    [Fact]
    public void EdgeLoop_OnCube_IsSingleEdge_And_OnPlaneRunsAcross()
    {
        // 큐브 정점은 3가 → 루프는 시작 엣지 하나
        var cube = MeshBuilder.Cube();
        Assert.Single(MeshOps.EdgeLoop(cube, 0));

        // 4x4 평면: 내부 가로 엣지에서 시작하면 같은 행의 엣지 4개가 루프
        var plane = MeshBuilder.Plane(4, 4, 4, 4);
        int e = EdgeBetween(plane, p => MathF.Abs(p.Z) < 1e-4f && MathF.Abs(p.X) < 1e-4f, p => MathF.Abs(p.Z) < 1e-4f && MathF.Abs(p.X - 1f) < 1e-4f);
        Assert.True(e >= 0);
        var loop = MeshOps.EdgeLoop(plane, e);
        Assert.Equal(4, loop.Count);
        foreach (int le in loop) { var (x, y) = plane.EdgeVertices(le); Assert.True(MathF.Abs(plane.Verts[x].Position.Z) < 1e-4f && MathF.Abs(plane.Verts[y].Position.Z) < 1e-4f); }
    }

    [Fact]
    public void EdgeLoop_OnBoundary_WalksBorder()
    {
        var plane = MeshBuilder.Plane(2, 2, 2, 2);
        int e = Enumerable.Range(0, plane.EdgeCount).First(i => plane.IsBoundaryEdge(i));
        var loop = MeshOps.EdgeLoop(plane, e);
        Assert.Equal(8, loop.Count);
    }

    [Fact]
    public void EdgeRing_OnCylinderSide_IsClosed()
    {
        var m = MeshBuilder.Cylinder(segments: 8, caps: false);
        // 세로 엣지(양끝 y가 다름)
        int e = Enumerable.Range(0, m.EdgeCount).First(i => { var (x, y) = m.EdgeVertices(i); return MathF.Abs(m.Verts[x].Position.Y - m.Verts[y].Position.Y) > 0.5f; });
        var (entries, faces, closed) = MeshOps.EdgeRing(m, e);
        Assert.True(closed);
        Assert.Equal(8, entries.Count);
        Assert.Equal(8, faces.Count);
    }

    [Fact]
    public void InsertEdgeLoop_OnCube_AddsFourVertsAndFourFaces()
    {
        var m = MeshBuilder.Cube();
        int before = m.AliveVertexCount, fBefore = m.AliveFaceCount, eBefore = m.AliveEdgeCount;
        // 세로 엣지 하나: 링은 4개 세로 엣지(닫힘), 캡(윗면/아랫면)은 링에 포함되지 않는 4각이지만 세로 엣지의 면이 아니므로 영향 없음
        int e = Enumerable.Range(0, m.EdgeCount).First(i => { var (x, y) = m.EdgeVertices(i); return MathF.Abs(m.Verts[x].Position.Y - m.Verts[y].Position.Y) > 0.5f; });
        var newEdges = MeshOps.InsertEdgeLoop(m, e, 0.5f);
        Assert.Equal(4, newEdges.Count);
        Assert.Equal(before + 4, m.AliveVertexCount);
        Assert.Equal(fBefore + 4, m.AliveFaceCount);
        Assert.Equal(eBefore + 8, m.AliveEdgeCount);
        Assert.Empty(MeshValidator.Check(m));
        foreach (int ne in newEdges) { var (x, y) = m.EdgeVertices(ne); Assert.True(MathF.Abs(m.Verts[x].Position.Y) < 1e-5f && MathF.Abs(m.Verts[y].Position.Y) < 1e-5f); }
    }

    [Fact]
    public void InsertEdgeLoop_OnOpenPlane_SplitsRowAndKeepsManifold()
    {
        var m = MeshBuilder.Plane(2, 2, 2, 2);
        int e = EdgeBetween(m, p => MathF.Abs(p.Z) < 1e-4f && MathF.Abs(p.X) < 1e-4f, p => MathF.Abs(p.Z - 1f) < 1e-4f && MathF.Abs(p.X) < 1e-4f);
        Assert.True(e >= 0);
        int vBefore = m.AliveVertexCount, fBefore = m.AliveFaceCount;
        var newEdges = MeshOps.InsertEdgeLoop(m, e, 0.25f);
        Assert.Equal(2, newEdges.Count);
        Assert.Equal(vBefore + 3, m.AliveVertexCount);
        Assert.Equal(fBefore + 2, m.AliveFaceCount);
        Assert.Empty(MeshValidator.Check(m));
    }

    [Fact]
    public void Bevel_SingleCubeEdge_AddsQuad()
    {
        var m = MeshBuilder.Cube();
        var newFaces = MeshOps.BevelEdges(m, new[] { 0 }, 0.1f);
        Assert.Single(newFaces);
        Assert.Equal(7, m.AliveFaceCount);   // 6 + 베벨 쿼드
        Assert.Equal(10, m.AliveVertexCount); // 8 - 2 + 4
        Assert.Empty(MeshValidator.Check(m));
        Assert.Equal(2, m.AliveVertexCount - m.AliveEdgeCount + m.AliveFaceCount); // 오일러 특성 유지
    }

    [Fact]
    public void Bevel_AllCubeEdges_MakesCapsAtCorners()
    {
        var m = MeshBuilder.Cube();
        var all = Enumerable.Range(0, m.EdgeCount).ToList();
        var newFaces = MeshOps.BevelEdges(m, all, 0.1f);
        Assert.Equal(12 + 8, newFaces.Count); // 쿼드 12 + 삼각 캡 8
        Assert.Equal(26, m.AliveFaceCount);
        Assert.Equal(24, m.AliveVertexCount);
        Assert.Empty(MeshValidator.Check(m));
        Assert.Equal(2, m.AliveVertexCount - m.AliveEdgeCount + m.AliveFaceCount);
    }

    [Fact]
    public void Bridge_TwoCylinderRims_ClosesTube()
    {
        // 캡 없는 원통 두 개를 합쳐 서로 마주보는 림을 브리지
        var a = MeshBuilder.Cylinder(segments: 8, caps: false);
        var b = MeshBuilder.Cylinder(segments: 8, caps: false);
        MeshOps.Append(a, b, Matrix4x4.CreateTranslation(0, 2f, 0));
        // a의 윗 림(y=0.5)과 b의 아랫 림(y=1.5)
        var rims = Enumerable.Range(0, a.EdgeCount).Where(e => a.IsBoundaryEdge(e)).Where(e =>
        {
            var (x, y) = a.EdgeVertices(e); float yy = a.Verts[x].Position.Y;
            return MathF.Abs(yy - 0.5f) < 1e-4f || MathF.Abs(yy - 1.5f) < 1e-4f;
        }).ToList();
        Assert.Equal(16, rims.Count);
        int fBefore = a.AliveFaceCount;
        var faces = MeshOps.BridgeEdges(a, rims);
        Assert.Equal(8, faces.Count);
        Assert.Equal(fBefore + 8, a.AliveFaceCount);
        Assert.Empty(MeshValidator.Check(a));
        foreach (int e in rims) Assert.False(a.IsBoundaryEdge(e));
    }
}
