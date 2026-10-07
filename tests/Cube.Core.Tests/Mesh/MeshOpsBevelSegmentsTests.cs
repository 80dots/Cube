using System.Numerics;
using Cube.Core.Mesh;

namespace Cube.Core.Tests.Mesh;

/// <summary>BevelEdges의 segments(둥근 프로파일) 검사.</summary>
public class MeshOpsBevelSegmentsTests
{
    private static int Euler(PolyMesh m) => m.AliveVertexCount - m.AliveEdgeCount + m.AliveFaceCount;

    /// <summary>Newell 법선 크기의 절반 = 다각형(비평면 포함) 면적.</summary>
    private static float FaceArea(PolyMesh m, int f)
    {
        var ids = new List<int>(); m.GetFaceVertices(f, ids);
        var n = Vector3.Zero;
        for (int i = 0; i < ids.Count; i++)
        {
            var a = m.Verts[ids[i]].Position; var b = m.Verts[ids[(i + 1) % ids.Count]].Position;
            n += Vector3.Cross(a, b);
        }
        return n.Length() * 0.5f;
    }

    /// <summary>정점 v에 모이는 엣지 ID(큐브 코너 = 3개).</summary>
    private static List<int> EdgesAt(PolyMesh m, int v) { var l = new List<int>(); m.GetVertexEdges(v, l); return l; }

    private static void AssertSound(PolyMesh m, List<int> newFaces, int expectedNewFaces, int expectedFaces, int expectedVerts)
    {
        Assert.Empty(MeshValidator.Check(m));
        Assert.Equal(2, Euler(m));
        Assert.Equal(expectedNewFaces, newFaces.Count);
        Assert.Equal(expectedFaces, m.AliveFaceCount);
        Assert.Equal(expectedVerts, m.AliveVertexCount);
        foreach (int f in newFaces) { Assert.True(m.Faces[f].Alive); Assert.True(FaceArea(m, f) > 1e-8f, $"face {f} degenerate"); }
        for (int e = 0; e < m.EdgeCount; e++) if (m.Edges[e].Alive) Assert.False(m.IsBoundaryEdge(e), $"edge {e} is boundary: mesh not closed");
    }

    // 면 수 공식: 6 + s·(베벨 엣지 수) + (캡 수). 캡은 정점에 모이는 프로파일 엣지가 3개 이상일 때 생긴다(s ≥ 2에서 끝 면과 같은 평면인 캡은 그 면에 흡수):
    //   베벨 엣지 1개가 닿는 큐브 코너(비베벨 면 1개 + s개 프로파일 엣지) → s ≥ 2에서 (s+1)각 캡, s = 1이면 없음
    //   베벨 엣지 3개가 모이는 코너 → 항상 3s각 캡.

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void Bevel_SingleEdge_Segments_RoundedStripAndEndCaps(int s)
    {
        var m = MeshBuilder.Cube();
        var newFaces = MeshOps.BevelEdges(m, new[] { 0 }, 0.1f, s);
        // 띠 쿼드 s; 양끝 D형 캡은 같은 평면의 끝 면에 흡수(원호 정점이 그 면 테두리가 됨); 정점 = 8 - 2 + 4 오프셋 + 2(s-1) 중간
        AssertSound(m, newFaces, s, 6 + s, 8 + 2 * s);
        // 둥근 띠 사이 엣지는 소프트(부드러운 음영)
        var tmp = new List<int>(); int soft = 0;
        foreach (int f in newFaces) { m.GetFaceHalfEdges(f, tmp); foreach (int he in tmp) if (!m.Edges[m.Hes[he].Edge].Hard) soft++; }
        Assert.True(soft >= 2 * (s - 1), "strip edges should be soft");
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void Bevel_ThreeEdgesAtCorner_Segments(int s)
    {
        var m = MeshBuilder.Cube();
        var edges = EdgesAt(m, 2); // (+x,+y,+z) 코너
        Assert.Equal(3, edges.Count);
        var newFaces = MeshOps.BevelEdges(m, edges, 0.1f, s);
        // 띠 3s + 코너 3s각 캡 1(먼 끝 캡 3개는 끝 면에 흡수); 정점 = 8 - 4 + 3 q + 6 P + 6(s-1) 중간
        AssertSound(m, newFaces, 3 * s + 1, 6 + 3 * s + 1, 7 + 6 * s);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void Bevel_AllEdges_Segments(int s)
    {
        var m = MeshBuilder.Cube();
        var all = Enumerable.Range(0, m.EdgeCount).ToList();
        var newFaces = MeshOps.BevelEdges(m, all, 0.1f, s);
        // 띠 12s + 코너 3s각 캡 8; 정점 = 24 q + 24(s-1) 중간 = 24s
        AssertSound(m, newFaces, 12 * s + 8, 14 + 12 * s, 24 * s);
        // 모든 면이 쿼드(띠) 또는 3s각(캡)
        var ids = new List<int>();
        foreach (int f in newFaces) { m.GetFaceVertices(f, ids); Assert.True(ids.Count == 4 || ids.Count == 3 * s, $"face {f} has {ids.Count} verts"); }
        Assert.Equal(8, newFaces.Count(f => { m.GetFaceVertices(f, ids); return ids.Count == 3 * s; }));
    }

    [Fact]
    public void Bevel_Segments2_MidpointLiesOnTangentArc()
    {
        // 단위 큐브, distance 0.1: 끝 오프셋 점 (0.5,0.4,0.4)/(0.4,0.5,0.4), 중심 (0.4,0.4,0.4), 반지름 0.1
        // → 중간 점은 두 좌표가 0.4 + 0.1/√2 ≈ 0.4707, 나머지 하나는 0.4 (챔퍼 중점 0.45보다 바깥으로 불룩)
        var m = MeshBuilder.Cube();
        MeshOps.BevelEdges(m, Enumerable.Range(0, m.EdgeCount).ToList(), 0.1f, 2);
        float r = 0.4f + 0.1f / MathF.Sqrt(2f);
        int mids = 0;
        for (int v = 0; v < m.Verts.Count; v++)
        {
            if (!m.Verts[v].Alive) continue;
            var p = m.Verts[v].Position;
            var a = new[] { MathF.Abs(p.X), MathF.Abs(p.Y), MathF.Abs(p.Z) }.OrderBy(x => x).ToArray();
            Assert.True(a[2] <= 0.5f + 1e-5f);
            if (MathF.Abs(a[2] - r) < 1e-4f && MathF.Abs(a[1] - r) < 1e-4f && MathF.Abs(a[0] - 0.4f) < 1e-4f) mids++;
        }
        Assert.Equal(24, mids);
    }

    [Fact]
    public void Bevel_CoplanarFaces_FallsBackToLinear()
    {
        // 2x2 평면 내부 엣지: 양쪽 면이 공면 → 선형 보간. 한 끝은 경계 정점(캡 없음, 띠가 열림),
        // 다른 끝은 4가 중심 정점(비베벨 면 2 + 프로파일 엣지 3 = 5각 캡; s=1일 때의 삼각 캡과 같은 규칙).
        var m = MeshBuilder.Plane(2, 2, 2, 2);
        int e = Enumerable.Range(0, m.EdgeCount).First(i => !m.IsBoundaryEdge(i));
        var newFaces = MeshOps.BevelEdges(m, new[] { e }, 0.1f, 3);
        Assert.Equal(3 + 1, newFaces.Count);
        Assert.Empty(MeshValidator.Check(m));
        foreach (int f in newFaces) Assert.True(FaceArea(m, f) > 1e-8f);
        // 모든 정점이 여전히 평면(y = 0) 위
        for (int v = 0; v < m.Verts.Count; v++) if (m.Verts[v].Alive) Assert.True(MathF.Abs(m.Verts[v].Position.Y) < 1e-5f);
    }

    [Fact]
    public void Bevel_Segments1_MatchesDefaultChamfer()
    {
        // segments = 1은 기존 챔퍼와 동일한 결과(면/정점/엣지/새 면 수)
        foreach (var pick in new Func<PolyMesh, List<int>>[] { m => new List<int> { 0 }, m => EdgesAt(m, 2), m => Enumerable.Range(0, m.EdgeCount).ToList() })
        {
            var a = MeshBuilder.Cube(); var b = MeshBuilder.Cube();
            var fa = MeshOps.BevelEdges(a, pick(a), 0.1f);
            var fb = MeshOps.BevelEdges(b, pick(b), 0.1f, 1);
            Assert.Equal(fa.Count, fb.Count);
            Assert.Equal(a.AliveFaceCount, b.AliveFaceCount);
            Assert.Equal(a.AliveVertexCount, b.AliveVertexCount);
            Assert.Equal(a.AliveEdgeCount, b.AliveEdgeCount);
            Assert.Empty(MeshValidator.Check(b));
            Assert.Equal(2, Euler(b));
        }
        // 기존 수치 회귀: 엣지 1개 = 7면/10정점, 코너 3엣지 = 10면/13정점, 전체 = 26면/24정점
        var c1 = MeshBuilder.Cube(); Assert.Single(MeshOps.BevelEdges(c1, new[] { 0 }, 0.1f, 1)); Assert.Equal(7, c1.AliveFaceCount); Assert.Equal(10, c1.AliveVertexCount);
        var c3 = MeshBuilder.Cube(); Assert.Equal(4, MeshOps.BevelEdges(c3, EdgesAt(c3, 2), 0.1f, 1).Count); Assert.Equal(10, c3.AliveFaceCount); Assert.Equal(13, c3.AliveVertexCount);
        var cAll = MeshBuilder.Cube(); Assert.Equal(20, MeshOps.BevelEdges(cAll, Enumerable.Range(0, cAll.EdgeCount).ToList(), 0.1f, 1).Count); Assert.Equal(26, cAll.AliveFaceCount); Assert.Equal(24, cAll.AliveVertexCount);
    }
}
