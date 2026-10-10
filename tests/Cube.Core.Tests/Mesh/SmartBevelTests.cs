using System.Numerics;
using Cube.Core.Mesh;

namespace Cube.Core.Tests.Mesh;

/// <summary>Smart Bevel(v0.0.62): A 엣지별 Clamp, B 전처리(비매니폴드 제외·나쁜 면 삼각화), C 각도 기반 가변 폭.</summary>
public class SmartBevelTests
{
    private static void AssertValid(PolyMesh m) { Assert.Empty(MeshValidator.Check(m)); Assert.True(MeshOps.IsClosed(m)); }

    /// <summary>A: 큐브 한 면의 네 엣지 중 하나가 아주 짧게 분할돼 있을 때 — Global은 전체 폭이 줄고, PerEdge는 짧은 엣지에 닿은 것만 준다.</summary>
    [Fact]
    public void PerEdgeClamp_OnlyShortEdgeShrinks()
    {
        // 큐브 윗면 둘레 엣지 중 하나를 분할해 짧은 조각(길이 0.1)을 만든다
        // SplitEdge는 면을 다시 만들어 면 ID가 바뀌므로 윗면은 분할 뒤에 다시 찾는다
        int Top(PolyMesh m) => Enumerable.Range(0, m.FaceCount).First(f => m.Faces[f].Alive && MeshNormals.FaceNormalUnnormalized(m, f).Y > 0.5f);
        var mk = () => { var m = MeshBuilder.Cube(); var hes = new List<int>(); m.GetFaceHalfEdges(Top(m), hes); int e0 = m.Hes[hes[0]].Edge; MeshOps.SplitEdge(m, e0, 0.1f); return (m, Top(m)); };
        var (g, topG) = mk(); var (p, topP) = mk();
        // 윗면 둘레의 긴 엣지들(짧은 조각 제외)을 Width 0.3으로 베벨
        List<int> Ring(PolyMesh m, int top) { var hes = new List<int>(); m.GetFaceHalfEdges(top, hes); return hes.Select(h => m.Hes[h].Edge).Where(e => { var (a, b) = m.EdgeVertices(e); return Vector3.Distance(m.Verts[a].Position, m.Verts[b].Position) > 0.5f; }).ToList(); }
        var ringG = Ring(g, topG); var ringP = Ring(p, topP); // Bevel 뒤에는 윗면이 다시 만들어지므로 미리 구한다
        var rg = MeshOps.Bevel(g, ringG, new BevelOptions { Width = 0.3f, ClampMode = BevelClampMode.Global }, out var repG);
        var rp = MeshOps.Bevel(p, ringP, new BevelOptions { Width = 0.3f, ClampMode = BevelClampMode.PerEdge }, out var repP);
        AssertValid(g); AssertValid(p);
        Assert.True(repG.MinClamp < 0.5f, $"global clamp {repG.MinClamp}");
        Assert.True(repP.ClampedEdges >= 1 && repP.ClampedEdges < ringP.Count, $"per-edge clamped {repP.ClampedEdges} of {ringP.Count}");
        // PerEdge 결과의 띠 총 넓이가 Global보다 크다(짧은 엣지에 닿지 않은 베벨은 폭을 유지)
        float Area(PolyMesh m, List<int> faces) { float a = 0; foreach (int f in faces) if (f >= 0 && f < m.FaceCount && m.Faces[f].Alive) a += MeshNormals.FaceNormalUnnormalized(m, f).Length() * 0.5f; return a; }
        Assert.True(Area(p, rp) > Area(g, rg) * 1.2f, $"per-edge area {Area(p, rp)} vs global {Area(g, rg)}");
    }

    /// <summary>B: 꼭짓점으로만 맞닿은 두 상자(나비넥타이 정점)에서 그 정점에 닿은 엣지는 제외되고 나머지는 정상 베벨된다.</summary>
    [Fact]
    public void SkipNonManifold_ExcludesBowtieEdges()
    {
        var m = MeshBuilder.Cube();
        MeshOps.Append(m, MeshBuilder.Cube(), Matrix4x4.CreateTranslation(1, 1, 1));
        MeshOps.MergeVertices(m, Enumerable.Range(0, m.VertexCount), 0.001f);
        var all = Enumerable.Range(0, m.EdgeCount).Where(e => m.Edges[e].Alive).ToList();
        var faces = MeshOps.Bevel(m, all, new BevelOptions { Width = 0.1f, SkipNonManifold = true }, out var rep);
        Assert.Equal(6, rep.SkippedNonManifold);
        Assert.True(faces.Count > 0);
        Assert.Empty(MeshValidator.Check(m));
    }

    /// <summary>B: 오목 n각형(L자 면)에 닿은 엣지를 베벨할 때 FixBadFaces가 그 면을 먼저 삼각화하고 베벨 엣지 ID를 다시 찾는다.</summary>
    [Fact]
    public void FixBadFaces_TriangulatesConcaveNeighbor()
    {
        // L자 기둥: 큐브 두 개 Union(Boolean)으로 윗면이 L자 n각형
        var a = MeshBuilder.Cube(); var b = MeshBuilder.Cube();
        var m = MeshOps.Boolean(a, b, Matrix4x4.CreateTranslation(0.5f, 0f, 0.5f), BooleanOperation.Union, out _);
        // Union 결과의 +Y 6각형 둘 중 L자(오목)인 면(다른 하나는 일직선 정점이 있는 볼록 6각형)
        int lFace = Enumerable.Range(0, m.FaceCount).First(f => m.Faces[f].Alive && m.FaceDegree(f) == 6 && MeshNormals.FaceNormalUnnormalized(m, f).Y > 0.5f && MeshOps.IsBadFace(m, f));
        var hes = new List<int>(); m.GetFaceHalfEdges(lFace, hes);
        var ring = hes.Select(h => m.Hes[h].Edge).ToList();
        var faces = MeshOps.Bevel(m, ring, new BevelOptions { Width = 0.05f, FixBadFaces = true }, out var rep);
        Assert.True(rep.FixedFaces >= 1);
        Assert.True(faces.Count >= ring.Count);
        AssertValid(m);
    }

    /// <summary>C: AngleWidth = 1이면 30° 엣지의 띠 폭이 90° 엣지보다 좁다(sin(15°)/sin(45°) ≈ 0.37). 0이면 같다.</summary>
    [Fact]
    public void AngleWidth_NarrowsShallowEdges()
    {
        // 90° 모서리(큐브)와 30° 꺾임(접힌 평면)의 띠 폭 비교: 띠 쿼드의 짧은 변 길이로 측정
        float StripWidth(PolyMesh m, int edge, float angleW)
        {
            var (a, b) = m.EdgeVertices(edge);
            var faces = MeshOps.Bevel(m, new[] { edge }, new BevelOptions { Width = 0.2f, WidthType = BevelWidthType.Offset, AngleWidth = angleW });
            Assert.Single(faces);
            var vs = new List<int>(); m.GetFaceVertices(faces[0], vs);
            // 띠 쿼드: 베벨 엣지 방향과 수직인 변의 길이
            var dir = Vector3.Normalize(m.Verts[b].Position - m.Verts[a].Position);
            float best = float.MaxValue;
            for (int i = 0; i < vs.Count; i++) { var d = m.Verts[vs[(i + 1) % vs.Count]].Position - m.Verts[vs[i]].Position; if (MathF.Abs(Vector3.Dot(Vector3.Normalize(d), dir)) < 0.5f) best = MathF.Min(best, d.Length()); }
            return best;
        }
        PolyMesh Fold(float deg)
        {
            var m = MeshBuilder.Plane(2, 2, 2, 1);
            float r = deg * MathF.PI / 180f;
            for (int v = 0; v < m.VertexCount; v++) { var p = m.Verts[v].Position; if (p.X > 0.1f) { var vv = m.Verts[v]; vv.Position = new Vector3(p.X * MathF.Cos(r), p.X * MathF.Sin(r), p.Z); m.Verts[v] = vv; } }
            m.BumpGeometry(); return m;
        }
        int Middle(PolyMesh m) => Enumerable.Range(0, m.EdgeCount).First(e => m.Edges[e].Alive && m.Edges[e].He1 >= 0);
        float w90a = StripWidth(Fold(90), Middle(Fold(90)), 0f), w30a = StripWidth(Fold(30), Middle(Fold(30)), 0f);
        float w90b = StripWidth(Fold(90), Middle(Fold(90)), 1f), w30b = StripWidth(Fold(30), Middle(Fold(30)), 1f);
        Assert.True(MathF.Abs(w90a - w90b) < 1e-3f, "90° edge keeps its width");
        Assert.True(w30b < w30a * 0.5f, $"30° edge narrower with AngleWidth: {w30b} vs {w30a}");
    }

    /// <summary>기본 옵션(PerEdge)으로 기존 베벨 결과가 바뀌지 않는 경우: 클램프가 걸리지 않는 큐브 전체 베벨은 Global과 동일.</summary>
    [Fact]
    public void PerEdge_EqualsGlobal_WhenNothingClamps()
    {
        var g = MeshBuilder.Cube(); var p = MeshBuilder.Cube();
        var all = Enumerable.Range(0, 12).ToList();
        MeshOps.Bevel(g, all, new BevelOptions { Width = 0.1f, Segments = 2, ClampMode = BevelClampMode.Global }, out var rg);
        MeshOps.Bevel(p, all, new BevelOptions { Width = 0.1f, Segments = 2, ClampMode = BevelClampMode.PerEdge }, out var rp);
        Assert.Equal(0, rg.ClampedEdges); Assert.Equal(0, rp.ClampedEdges);
        Assert.Equal(g.AliveVertexCount, p.AliveVertexCount);
        for (int v = 0; v < g.VertexCount; v++) if (g.Verts[v].Alive) Assert.True(Vector3.Distance(g.Verts[v].Position, p.Verts[v].Position) < 1e-5f);
    }
}
