using System.Numerics;
using Cube.Core.Mesh;

namespace Cube.Core.Tests.Mesh;

/// <summary>
/// M2 모델링 연산을 검증한다: 엣지 루프/링 탐색(<c>MeshOps.Loops</c>), Insert Edge Loop, 기본 Bevel(<c>BevelEdges</c>),
/// Bridge(경계 엣지 체인 두 개를 쿼드로 연결). 결과 요소 수와 메시 유효성·오일러 특성을 확인한다.
/// </summary>
public class MeshOpsLoopBevelBridgeTests
{
    /// <summary>
    /// 한쪽 끝이 조건 <paramref name="a"/>, 다른 끝이 조건 <paramref name="b"/>를 만족하는(방향 무관) 살아 있는 엣지를 찾는다. 없으면 -1.
    /// 위치로 특정 엣지를 고르기 위한 도우미다.
    /// </summary>
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

    /// <summary>
    /// 큐브 정점은 3가라 엣지 루프를 이어갈 "맞은편 엣지"가 없으므로 루프는 시작 엣지 하나뿐이어야 한다.
    /// 4x4 평면의 내부 가로 엣지에서 시작하면 4가 정점을 지나 같은 행(Z=0)의 엣지 4개가 루프가 되어야 한다.
    /// </summary>
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

    /// <summary>경계 엣지에서 시작한 루프는 메시 테두리(보더 루프)를 따라 한 바퀴, 2x2 평면이면 엣지 8개가 되어야 한다.</summary>
    [Fact]
    public void EdgeLoop_OnBoundary_WalksBorder()
    {
        var plane = MeshBuilder.Plane(2, 2, 2, 2);
        int e = Enumerable.Range(0, plane.EdgeCount).First(i => plane.IsBoundaryEdge(i));
        var loop = MeshOps.EdgeLoop(plane, e);
        Assert.Equal(8, loop.Count);
    }

    /// <summary>
    /// 캡 없는 8분할 원기둥의 세로 엣지에서 엣지 링을 구하면 옆면 쿼드 8개를 건너며 세로 엣지 8개로 닫힌 링이 되어야 한다.
    /// </summary>
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

    /// <summary>
    /// 큐브 세로 엣지에 t=0.5로 Insert Edge Loop하면 옆면 네 개를 가로지르는 닫힌 루프가 생겨
    /// 정점 +4, 면 +4, 엣지 +8이 되고, 새 엣지는 모두 높이 Y=0(가운데)에 놓여야 한다.
    /// </summary>
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

    /// <summary>
    /// 열린 2x2 평면에서 Z 방향 엣지에 루프를 넣으면 경계에서 멈추는 열린 루프가 되어 새 엣지 2, 정점 +3, 면 +2가 되고
    /// 메시가 유효해야 한다.
    /// </summary>
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

    /// <summary>
    /// 큐브 엣지 하나를 베벨하면 베벨 쿼드 1개가 생겨 면 7, 정점 10(양끝 정점 2개가 각각 둘로 갈라짐)이 되고 오일러 2가 유지되어야 한다.
    /// </summary>
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

    /// <summary>
    /// 큐브의 모든 엣지를 베벨하면 엣지마다 쿼드 12개 + 꼭짓점마다 삼각 캡 8개가 생겨 총 26면·24정점이 되고 닫힌 위상을 유지해야 한다.
    /// </summary>
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

    /// <summary>
    /// 캡 없는 원통 두 개를 한 메시로 합친 뒤(위쪽은 Y로 2 이동) 마주보는 두 림(각 8엣지)을 브리지하면
    /// 쿼드 8개가 생기고, 브리지한 림 엣지가 더 이상 경계가 아니어서 하나의 튜브로 이어져야 한다.
    /// </summary>
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
