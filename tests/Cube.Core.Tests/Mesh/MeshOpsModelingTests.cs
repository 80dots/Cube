using System.Numerics;
using Cube.Core.Mesh;

namespace Cube.Core.Tests.Mesh;

/// <summary>Maya 모델링 메뉴 추가 연산(Split/Add Divisions/Poke/Collapse/Connect/Spin/Chamfer/Fill Hole/Triangulate/Mirror/Crease/...)의 위상 검증.</summary>
public class MeshOpsModelingTests
{
    private static int Euler(PolyMesh m) => m.AliveVertexCount - m.AliveEdgeCount + m.AliveFaceCount;
    private static void Valid(PolyMesh m) { var errs = MeshValidator.Check(m); Assert.True(errs.Count == 0, string.Join("; ", errs)); }
    private static int TopFace(PolyMesh m) { for (int f = 0; f < m.FaceCount; f++) if (m.Faces[f].Alive && m.Faces[f].Normal.Y > 0.9f) return f; return -1; }

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

    [Fact]
    public void ExtrudeEdges_OnBoundary_AddsQuads()
    {
        var m = MeshBuilder.Plane();
        var border = Enumerable.Range(0, m.EdgeCount).Where(e => m.IsBoundaryEdge(e)).ToList();
        var faces = MeshOps.ExtrudeEdges(m, border);
        MeshNormals.Recompute(m);
        Assert.Equal(4, faces.Count); Valid(m); Assert.Equal(8, m.AliveVertexCount); Assert.Equal(5, m.AliveFaceCount);
    }

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

    [Fact]
    public void Crease_KeepsEdgeSharpInCatmullClark()
    {
        var m = MeshBuilder.Cube();
        var hes = new List<int>(); m.GetFaceHalfEdges(TopFace(m), hes);
        var topEdges = hes.Select(h => m.Hes[h].Edge).ToList();
        MeshOps.SetCrease(m, topEdges, 2f);
        var sub = MeshOps.CatmullClark(m);
        // 크리즈 엣지 점(4)과 크리즈 정점(4)은 y = 0.5에 남는다(면 점 1 포함 9개). 크리즈 없는 큐브는 면 점 하나만 0.5
        int OnTop(PolyMesh x) => x.Verts.Count(v => v.Alive && MathF.Abs(v.Position.Y - 0.5f) < 1e-5f);
        Assert.Equal(9, OnTop(sub));
        var plain = MeshOps.CatmullClark(MeshBuilder.Cube());
        Assert.Equal(1, OnTop(plain));
        // 자식 크리즈는 1 감소
        Assert.True(sub.Edges.Any(e => e.Alive && MathF.Abs(e.Crease - 1f) < 1e-6f));
    }

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

    [Fact]
    public void SliceWithPlane_CutsCubeInHalf()
    {
        var m = MeshBuilder.Cube();
        var edges = MeshOps.SliceWithPlane(m, Vector3.Zero, Vector3.UnitY);
        MeshNormals.Recompute(m);
        Assert.Equal(4, edges.Count); Valid(m); Assert.Equal(12, m.AliveVertexCount); Assert.Equal(10, m.AliveFaceCount); Assert.Equal(2, Euler(m));
    }

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
