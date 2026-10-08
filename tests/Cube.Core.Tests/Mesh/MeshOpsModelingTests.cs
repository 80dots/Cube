using System.Numerics;
using Cube.Core.Mesh;

namespace Cube.Core.Tests.Mesh;

/// <summary>Maya 모델링 메뉴 추가 연산(Split/Add Divisions/Poke/Collapse/Connect/Spin/Chamfer/Fill Hole/Triangulate/Mirror/Crease/...)의 위상 검증.</summary>
public class MeshOpsModelingTests
{
    /// <summary>오일러 특성 V - E + F(닫힌 구 위상 = 2).</summary>
    private static int Euler(PolyMesh m) => m.AliveVertexCount - m.AliveEdgeCount + m.AliveFaceCount;
    /// <summary>메시 검증기 오류가 없음을 단언하고, 실패 시 오류 목록을 메시지로 보여 준다.</summary>
    private static void Valid(PolyMesh m) { var errs = MeshValidator.Check(m); Assert.True(errs.Count == 0, string.Join("; ", errs)); }
    /// <summary>법선 Y가 0.9보다 큰 첫 번째 살아 있는 면(큐브 윗면). 없으면 -1.</summary>
    private static int TopFace(PolyMesh m) { for (int f = 0; f < m.FaceCount; f++) if (m.Faces[f].Alive && m.Faces[f].Normal.Y > 0.9f) return f; return -1; }

    /// <summary>
    /// SplitEdge(t=0.25): 엣지 위 1/4 지점에 정점이 생겨 정점 9·엣지 13이 되고, 분할된 자식 엣지가 부모의 Hard 플래그를 상속해야 한다.
    /// 이어서 5각형이 된 면을 새 정점과 반대 코너 사이로 SplitFace하면 면 7·엣지 14, 오일러 2가 유지되어야 한다.
    /// </summary>
    [Fact]
    public void SplitEdge_And_SplitFace_KeepManifold()
    {
        var m = MeshBuilder.Cube();
        int e = 0; var (a, b) = m.EdgeVertices(e);
        int nv = MeshOps.SplitEdge(m, e, 0.25f);
        Assert.True(nv >= 0);
        Valid(m);
        Assert.Equal(9, m.AliveVertexCount); Assert.Equal(13, m.AliveEdgeCount); Assert.Equal(6, m.AliveFaceCount);
        Assert.True(Vector3.Distance(m.Verts[nv].Position, Vector3.Lerp(m.Verts[a].Position, m.Verts[b].Position, 0.25f)) < 1e-6f);
        Assert.True(m.Edges[m.FindEdge(a, nv)].Hard); // 큐브 엣지는 하드 → 자식 엣지 상속
        // 5각형이 된 면을 nv와 반대 코너 사이로 나눈다
        var faces = new List<int>(); m.GetVertexFaces(nv, faces);
        int f = faces[0]; var loop = new List<int>(); m.GetFaceVertices(f, loop);
        int opp = loop[(loop.IndexOf(nv) + 2) % loop.Count];
        int ne = MeshOps.SplitFace(m, f, nv, opp);
        Assert.True(ne >= 0);
        Valid(m);
        Assert.Equal(7, m.AliveFaceCount); Assert.Equal(14, m.AliveEdgeCount);
        Assert.Equal(2, Euler(m));
    }

    /// <summary>
    /// Add Divisions: Exponential Quads 1단계는 윗면을 쿼드 4개로, Triangles 1단계는 삼각형 8개로, Linear(3×2)는 쿼드 6개로 나눠야 한다.
    /// DivideEdges(엣지 하나를 2개 점으로)는 정점 2개를 추가해야 한다. 모든 경우 메시가 유효하고 닫혀 있어야 한다.
    /// </summary>
    [Fact]
    public void AddDivisions_Quads_And_Triangles_And_Linear()
    {
        var m = MeshBuilder.Cube();
        var res = MeshOps.AddDivisions(m, new[] { TopFace(m) }, 1, MeshOps.DivisionMode.Quads);
        MeshNormals.Recompute(m);
        Assert.Equal(4, res.Count); Valid(m); Assert.Equal(2, Euler(m)); Assert.Equal(9, m.AliveFaceCount);
        var m2 = MeshBuilder.Cube();
        var tris = MeshOps.AddDivisions(m2, new[] { TopFace(m2) }, 1, MeshOps.DivisionMode.Triangles);
        MeshNormals.Recompute(m2);
        Assert.Equal(8, tris.Count); Valid(m2); Assert.Equal(2, Euler(m2));
        var m3 = MeshBuilder.Cube();
        var lin = MeshOps.AddDivisionsLinear(m3, new[] { TopFace(m3) }, 3, 2);
        MeshNormals.Recompute(m3);
        Assert.Equal(6, lin.Count); Valid(m3); Assert.Equal(2, Euler(m3));
        var m4 = MeshBuilder.Cube();
        var nv = MeshOps.DivideEdges(m4, new[] { 0 }, 2);
        Assert.Equal(2, nv.Count); Valid(m4); Assert.Equal(10, m4.AliveVertexCount);
    }

    /// <summary>
    /// Poke: 윗면 중심에 정점을 만들고 법선 방향으로 0.3 밀어(Y=0.8) 삼각형 4개로 나눈다(면 9).
    /// Collapse Edge: 엣지 하나를 한 점으로 접어 정점 7. Merge To Center: 윗면 정점 4개를 하나로 합쳐 윗면이 사라지고 옆면이 삼각형이 된다.
    /// </summary>
    [Fact]
    public void Poke_Collapse_MergeToCenter()
    {
        var m = MeshBuilder.Cube();
        var centers = MeshOps.Poke(m, new[] { TopFace(m) }, 0.3f);
        MeshNormals.Recompute(m);
        Assert.Single(centers); Valid(m); Assert.Equal(9, m.AliveFaceCount); Assert.Equal(2, Euler(m));
        Assert.True(MathF.Abs(m.Verts[centers[0]].Position.Y - 0.8f) < 1e-5f);
        // 포크로 생긴 삼각형 면 하나를 접으면 정점이 합쳐진다
        var m2 = MeshBuilder.Cube();
        var reps = MeshOps.CollapseEdges(m2, new[] { 0 });
        MeshNormals.Recompute(m2);
        Assert.Single(reps); Valid(m2); Assert.Equal(7, m2.AliveVertexCount);
        var m3 = MeshBuilder.Cube();
        var loop = new List<int>(); m3.GetFaceVertices(TopFace(m3), loop);
        int rep = MeshOps.MergeToCenter(m3, loop);
        MeshNormals.Recompute(m3);
        Assert.True(rep >= 0); Valid(m3); Assert.Equal(5, m3.AliveVertexCount); Assert.Equal(5, m3.AliveFaceCount); // 윗면이 사라지고 옆면은 삼각형
    }

    /// <summary>
    /// Connect(정점): 윗면 대각 정점 두 개를 이으면 면이 둘로 나뉘어 7면. Connect(엣지): 윗면의 마주보는 엣지 두 개 중점을 이으면
    /// 정점 10개가 되고 닫힌 위상이 유지되어야 한다.
    /// </summary>
    [Fact]
    public void Connect_Vertices_And_Edges()
    {
        var m = MeshBuilder.Cube();
        var loop = new List<int>(); m.GetFaceVertices(TopFace(m), loop);
        var edges = MeshOps.ConnectVertices(m, new[] { loop[0], loop[2] });
        Assert.Single(edges); Valid(m); Assert.Equal(7, m.AliveFaceCount);
        var m2 = MeshBuilder.Cube();
        var hes = new List<int>(); m2.GetFaceHalfEdges(TopFace(m2), hes);
        int e0 = m2.Hes[hes[0]].Edge, e2 = m2.Hes[hes[2]].Edge;
        var ne = MeshOps.ConnectEdges(m2, new[] { e0, e2 });
        MeshNormals.Recompute(m2);
        Assert.Single(ne); Valid(m2); Assert.Equal(10, m2.AliveVertexCount); Assert.Equal(2, Euler(m2));
    }

    /// <summary>
    /// 윗면을 대각선(loop[0]-loop[2])으로 나눈 뒤 Flip Triangle Edge하면 대각선이 반대(loop[1]-loop[3])로 바뀌고,
    /// 그 엣지를 다시 Spin하면 원래 대각선으로 돌아와야 한다.
    /// </summary>
    [Fact]
    public void FlipTriangleEdge_And_SpinEdge()
    {
        var m = MeshBuilder.Cube();
        int top = TopFace(m);
        var loop = new List<int>(); m.GetFaceVertices(top, loop);
        int diag = MeshOps.SplitFace(m, top, loop[0], loop[2]);
        Assert.True(diag >= 0);
        var flipped = MeshOps.FlipTriangleEdges(m, new[] { diag });
        Assert.Single(flipped); Valid(m);
        var (x, y) = m.EdgeVertices(flipped[0]);
        Assert.True((x == loop[1] && y == loop[3]) || (x == loop[3] && y == loop[1]));
        // 다시 스핀하면 원래 대각선
        int back = MeshOps.SpinEdge(m, flipped[0], forward: true);
        Assert.True(back >= 0); Valid(m);
        var (p, q) = m.EdgeVertices(back);
        Assert.True((p == loop[0] && q == loop[2]) || (p == loop[2] && q == loop[0]));
    }

    /// <summary>
    /// 정점 Chamfer: 큐브 코너를 깎아 삼각형 캡 면이 생기고(면 7, 오일러 2), removeFace 옵션이면 캡 면 없이 구멍으로 남아 면 수가 6이어야 한다.
    /// </summary>
    [Fact]
    public void ChamferVertex_MakesCapFace()
    {
        var m = MeshBuilder.Cube();
        var caps = MeshOps.ChamferVertices(m, new[] { 0 }, 0.2f, removeFace: false);
        MeshNormals.Recompute(m);
        Assert.Single(caps); Valid(m); Assert.Equal(3, m.FaceDegree(caps[0])); Assert.Equal(7, m.AliveFaceCount); Assert.Equal(2, Euler(m));
        var m2 = MeshBuilder.Cube();
        MeshOps.ChamferVertices(m2, new[] { 0 }, 0.2f, removeFace: true);
        Assert.Equal(6, m2.AliveFaceCount); Valid(m2);
    }

    /// <summary>
    /// 윗면을 지운 구멍을 Fill Hole로 메우면 +Y 법선의 면 하나가 생기고, 전체 Triangulate하면 삼각형 12개,
    /// 다시 Quadrangulate(30°)하면 원래 쿼드 6개로 돌아와야 한다.
    /// </summary>
    [Fact]
    public void FillHole_Triangulate_Quadrangulate_RoundTrip()
    {
        var m = MeshBuilder.Cube();
        int top = TopFace(m);
        var hes = new List<int>(); m.GetFaceHalfEdges(top, hes);
        int edge = m.Hes[hes[0]].Edge;
        MeshOps.DeleteFaces(m, new[] { top });
        Assert.Equal(5, m.AliveFaceCount);
        var filled = MeshOps.FillHoles(m, new[] { edge });
        MeshNormals.Recompute(m);
        Assert.Single(filled); Valid(m); Assert.Equal(6, m.AliveFaceCount); Assert.True(m.Faces[filled[0]].Normal.Y > 0.9f);
        var tris = MeshOps.Triangulate(m, Enumerable.Range(0, m.FaceCount));
        MeshNormals.Recompute(m);
        Assert.Equal(12, tris.Count); Valid(m); Assert.Equal(12, m.AliveFaceCount);
        var quads = MeshOps.Quadrangulate(m, Enumerable.Range(0, m.FaceCount), 30f);
        MeshNormals.Recompute(m);
        Assert.Equal(6, quads.Count); Valid(m); Assert.Equal(6, m.AliveFaceCount);
    }

    /// <summary>
    /// Mirror Geometry(X축, 원점 평면, +X 유지, 자르기, 병합): -X 쪽을 잘라내고 +X 쪽을 반사 복사·병합하면
    /// 닫힌 위상(오일러 2)이 유지되고 결과가 X에 대해 대칭(minX = -maxX)이어야 한다.
    /// </summary>
    [Fact]
    public void Mirror_WithCutAndMerge_IsSymmetricCube()
    {
        var m = MeshBuilder.Cube();
        // 왼쪽(-X) 절반을 잘라내고 +X 쪽을 거울 복사 → 다시 큐브
        var added = MeshOps.MirrorGeometry(m, axis: 0, planeOffset: 0f, keepPositive: true, cut: true, mergeThreshold: 1e-4f);
        MeshNormals.Recompute(m);
        Valid(m);
        // 큐브는 평면 위에 정점이 없으므로 잘리는 면은 -X 면 하나, 미러는 +X 면 → 두 면 모두 x=±0.5 → 결과 8정점 큐브가 아니라 "+X면 2장"의 상자
        Assert.Equal(2, Euler(m));
        Assert.True(added.Count > 0);
        float minX = m.Verts.Where(v => v.Alive).Min(v => v.Position.X), maxX = m.Verts.Where(v => v.Alive).Max(v => v.Position.X);
        Assert.True(MathF.Abs(minX + maxX) < 1e-5f);
    }

    /// <summary>평면의 경계 엣지 4개를 Extrude하면 엣지마다 쿼드가 생겨 면 5·정점 8이 되어야 한다.</summary>
    [Fact]
    public void ExtrudeEdges_OnBoundary_AddsQuads()
    {
        var m = MeshBuilder.Plane();
        var border = Enumerable.Range(0, m.EdgeCount).Where(e => m.IsBoundaryEdge(e)).ToList();
        var faces = MeshOps.ExtrudeEdges(m, border);
        MeshNormals.Recompute(m);
        Assert.Equal(4, faces.Count); Valid(m); Assert.Equal(8, m.AliveVertexCount); Assert.Equal(5, m.AliveFaceCount);
    }

    /// <summary>
    /// Wedge: 평면을 한 엣지(힌지)를 축으로 90°, 3단계 돌출하면 캡 1개와 단계마다 측면 3개가 생기고(면 10),
    /// 반대편 정점이 회전해 Y가 0.9보다 높아져야 한다.
    /// </summary>
    [Fact]
    public void Wedge_RotatesCapAroundHinge()
    {
        var m = MeshBuilder.Plane();
        int f = 0;
        var hes = new List<int>(); m.GetFaceHalfEdges(f, hes);
        int hinge = m.Hes[hes[0]].Edge;
        var caps = MeshOps.Wedge(m, new[] { f }, hinge, 90f, 3);
        MeshNormals.Recompute(m);
        Assert.Single(caps); Valid(m);
        Assert.Equal(10, m.AliveFaceCount); // 3단계 × (캡 + 측면 3) 중 겹친 캡 제외: 원본1 + 단계마다 측면3 = 10
        // 힌지 정점 두 개는 그대로, 반대편 정점은 90° 돌아 y가 양수
        float maxY = m.Verts.Where(v => v.Alive).Max(v => v.Position.Y);
        Assert.True(maxY > 0.9f, $"maxY={maxY}");
    }

    /// <summary>
    /// 윗면 엣지 4개에 크리즈 2를 주고 Catmull-Clark하면 크리즈 엣지 점·정점이 날카롭게 유지되어 Y=0.5 평면에 9개 정점이 남아야 한다
    /// (크리즈 없는 큐브는 면 점 1개뿐). 자식 엣지 크리즈는 한 단계 줄어 1이 되어야 한다.
    /// </summary>
    [Fact]
    public void Crease_KeepsEdgeSharpInCatmullClark()
    {
        var m = MeshBuilder.Cube();
        var hes = new List<int>(); m.GetFaceHalfEdges(TopFace(m), hes);
        var topEdges = hes.Select(h => m.Hes[h].Edge).ToList();
        MeshOps.SetCrease(m, topEdges, 2f);
        var sub = MeshOps.CatmullClark(m);
        // 크리즈 엣지 점(4)과 크리즈 정점(4)은 y = 0.5에 남는다(면 점 1 포함 9개). 크리즈 없는 큐브는 면 점 하나만 0.5
        // 윗면 평면(Y=0.5)에 정확히 놓인 정점 수를 센다.
        int OnTop(PolyMesh x) => x.Verts.Count(v => v.Alive && MathF.Abs(v.Position.Y - 0.5f) < 1e-5f);
        Assert.Equal(9, OnTop(sub));
        var plain = MeshOps.CatmullClark(MeshBuilder.Cube());
        Assert.Equal(1, OnTop(plain));
        // 자식 크리즈는 1 감소
        Assert.True(sub.Edges.Any(e => e.Alive && MathF.Abs(e.Crease - 1f) < 1e-6f));
    }

    /// <summary>
    /// Set Vertex Normal로 잠근 정점 노멀은 노멀 재계산 후에도 유지되고 Clone에도 복사되어야 한다.
    /// Unlock 후 재계산하면 다시 주변 면에서 계산된 값(+Y와 다름)으로 돌아와야 한다.
    /// </summary>
    [Fact]
    public void LockedNormals_SurviveRecompute_And_Clone()
    {
        var m = MeshBuilder.Cube();
        MeshOps.SetVertexNormal(m, new[] { 0 }, Vector3.UnitY);
        MeshNormals.Recompute(m);
        foreach (int he in m.VertexOutgoing(0)) Assert.True(Vector3.Distance(m.Hes[he].Normal, Vector3.UnitY) < 1e-6f);
        var c = m.Clone();
        Assert.True(c.LockedNormals.ContainsKey(0));
        MeshOps.UnlockNormals(m, new[] { 0 });
        MeshNormals.Recompute(m);
        Assert.True(m.VertexOutgoing(0).ToArray().Any(he => Vector3.Distance(m.Hes[he].Normal, Vector3.UnitY) > 0.5f));
    }

    /// <summary>
    /// Multi-Cut 슬라이스: Y=0 평면으로 큐브를 자르면 옆면 4개를 가로지르는 새 엣지 4개가 생기고 정점 12·면 10, 오일러 2가 되어야 한다.
    /// </summary>
    [Fact]
    public void SliceWithPlane_CutsCubeInHalf()
    {
        var m = MeshBuilder.Cube();
        var edges = MeshOps.SliceWithPlane(m, Vector3.Zero, Vector3.UnitY);
        MeshNormals.Recompute(m);
        Assert.Equal(4, edges.Count); Valid(m); Assert.Equal(12, m.AliveVertexCount); Assert.Equal(10, m.AliveFaceCount); Assert.Equal(2, Euler(m));
    }

    /// <summary>
    /// Append to Polygon: 평면의 경계 엣지에 바깥쪽 두 점을 이어 새 면을 붙이면 면 2개가 되고, 새 면 법선이 기존 면과 같은 +Y여야 한다.
    /// </summary>
    [Fact]
    public void AppendPolygon_OnPlaneBorder()
    {
        var m = MeshBuilder.Plane();
        int border = Enumerable.Range(0, m.EdgeCount).First(e => m.IsBoundaryEdge(e));
        var (a, b) = m.EdgeVertices(border);
        var pa = m.Verts[a].Position; var pb = m.Verts[b].Position;
        var outward = (pa + pb) * 0.5f; outward = Vector3.Normalize(outward) * 0.5f;
        int nf = MeshOps.AppendPolygon(m, border, new[] { pb + outward, pa + outward });
        MeshNormals.Recompute(m);
        Assert.True(nf >= 0); Valid(m); Assert.Equal(2, m.AliveFaceCount); Assert.True(m.Faces[nf].Normal.Y > 0.9f);
    }

    /// <summary>
    /// Duplicate Face: 윗면을 정점까지 복제해 떨어진 면을 추가(정점 12, 면 7)하고, 정점이 다르므로 Cleanup이 라미나로 지우지 않아야 한다.
    /// Detach Vertex: 코너 정점을 면마다 분리해 정점 +2. Detach Face(Extract): 윗면을 떼어 정점 12개가 되되 면 수는 6 그대로여야 한다.
    /// </summary>
    [Fact]
    public void DetachAndDuplicate_And_Cleanup()
    {
        var m = MeshBuilder.Cube();
        var dup = MeshOps.DuplicateFaces(m, new[] { TopFace(m) });
        MeshNormals.Recompute(m);
        Assert.Single(dup); Valid(m); Assert.Equal(12, m.AliveVertexCount); Assert.Equal(7, m.AliveFaceCount);
        int removed = MeshOps.Cleanup(m); // 라미나(같은 정점 집합) 아님: 정점이 다르므로 유지
        Assert.Equal(0, removed);
        var m2 = MeshBuilder.Cube();
        var nv = MeshOps.DetachVertices(m2, new[] { 0 });
        Assert.Equal(2, nv.Count); Valid(m2); Assert.Equal(10, m2.AliveVertexCount);
        var m3 = MeshBuilder.Cube();
        MeshOps.DetachFaces(m3, new[] { TopFace(m3) });
        Valid(m3); Assert.Equal(12, m3.AliveVertexCount); Assert.Equal(6, m3.AliveFaceCount);
    }

    /// <summary>
    /// Offset Edge Loop: 엣지 양옆에 0.2 거리의 루프를 넣어 2개 이상 새 루프가 생기고 위상이 유효해야 한다.
    /// Slide Edge: 삽입한 루프를 0.5만큼 밀면 루프 정점이 실제로 이동해야 한다.
    /// </summary>
    [Fact]
    public void OffsetEdgeLoop_And_SlideEdge()
    {
        var m = MeshBuilder.Cube();
        var hes = new List<int>(); m.GetFaceHalfEdges(TopFace(m), hes);
        int e = m.Hes[hes[0]].Edge;
        var loops = MeshOps.OffsetEdgeLoop(m, new[] { e }, 0.2f);
        MeshNormals.Recompute(m);
        Assert.True(loops.Count >= 2); Valid(m); Assert.Equal(2, Euler(m));
        var m2 = MeshBuilder.Cube();
        var ring = MeshOps.InsertEdgeLoop(m2, 0, 0.5f);
        var before = ring.Select(x => { var (p, q) = m2.EdgeVertices(x); return m2.Verts[p].Position; }).ToList();
        MeshOps.SlideEdges(m2, ring, 0.5f);
        Valid(m2);
        var after = ring.Select(x => { var (p, q) = m2.EdgeVertices(x); return m2.Verts[p].Position; }).ToList();
        Assert.True(before.Zip(after).Any(z => Vector3.Distance(z.First, z.Second) > 0.1f));
    }
}
