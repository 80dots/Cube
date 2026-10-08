using System.Numerics;
using Cube.Core.Mesh;

namespace Cube.Core.Tests.Mesh;

/// <summary>BevelEdges의 segments(둥근 프로파일) 검사.</summary>
public class MeshOpsBevelSegmentsTests
{
    /// <summary>오일러 특성 V - E + F(닫힌 구 위상 = 2).</summary>
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

    /// <summary>
    /// 베벨 결과 공통 검사: 메시 유효성, 오일러 2, 새 면 수·전체 면 수·정점 수가 기대값과 같고,
    /// 새 면에 넓이 0(퇴화) 면이 없으며 경계 엣지가 없어 메시가 닫혀 있는지 확인한다.
    /// </summary>
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

    /// <summary>
    /// 큐브 엣지 하나를 세그먼트 s로 둥글게 베벨하면 띠 쿼드 s개가 생기고, 양끝 D자 캡은 같은 평면의 끝 면에 흡수되어
    /// 면 6+s, 정점 8+2s가 되어야 한다. 둥근 띠 사이 엣지는 부드러운 음영을 위해 소프트여야 한다.
    /// </summary>
    /// <param name="s">프로파일 세그먼트 수.</param>
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

    /// <summary>
    /// 한 코너에 모이는 엣지 3개를 세그먼트 s로 베벨하면 띠 3s개 + 코너의 3s각 캡 1개가 생기고(먼 끝 캡 3개는 끝 면에 흡수),
    /// 정점은 7+6s개가 되어야 한다.
    /// </summary>
    /// <param name="s">프로파일 세그먼트 수.</param>
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

    /// <summary>
    /// 큐브의 모든 엣지를 세그먼트 s로 베벨하면 띠 12s개 + 코너 3s각 캡 8개, 정점 24s개가 되어야 하고,
    /// 새 면은 모두 쿼드(띠) 아니면 3s각(캡)이며 캡은 정확히 8개여야 한다.
    /// </summary>
    /// <param name="s">프로파일 세그먼트 수.</param>
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

    /// <summary>
    /// 세그먼트 2 둥근 베벨의 중간 점이 직선 챔퍼 중점이 아니라 두 면에 접하는 원호 위(중심 0.4, 반지름 0.1)에 놓이는지 확인한다.
    /// 24개 띠 중간 점이 모두 좌표 (0.4+0.1/√2, 0.4+0.1/√2, 0.4) 패턴을 가져야 한다.
    /// </summary>
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

    /// <summary>
    /// 양쪽 면이 공면인 평면 내부 엣지는 둥글게 할 각이 없으므로 선형 보간으로 처리해야 한다.
    /// 띠 3개 + 4가 중심 정점의 5각 캡 1개가 생기고, 퇴화 면이 없으며 모든 정점이 평면(Y=0)에 남아야 한다.
    /// </summary>
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

    /// <summary>
    /// segments = 1은 기존(세그먼트 인자 없는) 챔퍼와 완전히 같은 결과여야 한다: 엣지 1개/코너 3개/전체 세 경우 모두
    /// 새 면·면·정점·엣지 수가 같고, 예전 수치(7면/10정점, 10면/13정점, 26면/24정점)도 그대로인지 회귀 검사한다.
    /// </summary>
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

    /// <summary>
    /// 원기둥 옆면 가운데에 넣은 엣지 루프(12엣지)를 베벨하면 이웃 베벨 엣지가 만나는 정점마다 프로파일을 공유해야 한다.
    /// 캡(틈 면) 없이 띠 12s개만 생기고, 루프 정점 12개가 각각 (s+1)개 프로파일 정점으로 바뀌며, 얇은 조각 면이 없어야 한다.
    /// </summary>
    /// <param name="s">프로파일 세그먼트 수.</param>
    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void Bevel_EdgeLoop_Segments_SharesProfilesWithoutSliverFaces(int s)
    {
        // 원기둥 옆면 가운데에 엣지 루프를 넣고 그 루프를 Bevel: 엣지 두 개가 이어지는 정점마다 프로파일을 공유해야 한다
        var m = MeshBuilder.Cylinder(0.5f, 1f, 12, caps: true);
        int vertical = -1;
        for (int e = 0; e < m.EdgeCount; e++)
        {
            if (!m.Edges[e].Alive) continue;
            var (a, b) = m.EdgeVertices(e);
            var d = m.Verts[a].Position - m.Verts[b].Position;
            if (MathF.Abs(d.Y) > 0.9f) { vertical = e; break; }
        }
        Assert.True(vertical >= 0);
        // 세로 엣지에서 t=0.5로 루프를 넣으면 옆면을 한 바퀴 도는 가로 엣지 12개가 반환된다.
        var loop = MeshOps.InsertEdgeLoop(m, vertical, 0.5f);
        Assert.Equal(12, loop.Count);
        int facesBefore = m.AliveFaceCount, vertsBefore = m.AliveVertexCount;
        var newFaces = MeshOps.BevelEdges(m, loop, 0.1f, s);
        Assert.Empty(MeshValidator.Check(m));
        Assert.Equal(2, Euler(m));
        // 루프 엣지마다 띠 s개, 캡(틈 면) 없음
        Assert.Equal(12 * s, newFaces.Count);
        Assert.Equal(facesBefore + 12 * s, m.AliveFaceCount);
        // 정점: 루프 정점 12개가 프로파일 (s+1)개로 바뀐다
        Assert.Equal(vertsBefore - 12 + 12 * (s + 1), m.AliveVertexCount);
        foreach (int f in newFaces) Assert.True(FaceArea(m, f) > 1e-6f, $"face {f} is a sliver");
        for (int e = 0; e < m.EdgeCount; e++) if (m.Edges[e].Alive) Assert.False(m.IsBoundaryEdge(e));
    }
}
