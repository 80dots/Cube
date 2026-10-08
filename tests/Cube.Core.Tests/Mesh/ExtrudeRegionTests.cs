using Cube.Core.Mesh;

namespace Cube.Core.Tests.Mesh;

/// <summary>선택 면들이 한 정점에서만 맞닿거나 팬 중심을 공유할 때 Extrude가 구멍/뒤집힌 면 없이 닫힌 메시를 유지하는지.</summary>
public class ExtrudeRegionTests
{
    /// <summary>살아 있는 요소로 계산한 오일러 특성 V - E + F. 구멍 없는 닫힌 구 위상이면 2다.</summary>
    private static int Euler(PolyMesh m) => m.AliveVertexCount - m.AliveEdgeCount + m.AliveFaceCount;

    /// <summary>
    /// 가장 많은 면이 모이는 정점(원뿔의 꼭짓점)을 찾고, 그 정점에 붙은 삼각형 면들(팬)을 중복 없이 돌려준다.
    /// </summary>
    private static List<int> ApexFan(PolyMesh m)
    {
        int apex = -1, best = 0; var tmp = new List<int>();
        for (int v = 0; v < m.VertexCount; v++)
        {
            if (!m.Verts[v].Alive) continue;
            m.GetVertexFaces(v, tmp);
            if (tmp.Count > best) { best = tmp.Count; apex = v; }
        }
        m.GetVertexFaces(apex, tmp);
        return tmp.Where(f => m.FaceDegree(f) == 3).Distinct().ToList();
    }

    /// <summary>두 면 <paramref name="a"/>, <paramref name="b"/>가 엣지를 공유하는지(하프에지 트윈으로 이웃인지) 판정한다.</summary>
    private static bool SharesEdge(PolyMesh m, int a, int b)
    {
        int start = m.Faces[a].HalfEdge, he = start;
        do { int tw = m.Hes[he].Twin; if (tw >= 0 && m.Hes[tw].Face == b) return true; he = m.Hes[he].Next; } while (he != start);
        return false;
    }

    /// <summary>메시 검증기 오류가 없고, 살아 있는 엣지 중 경계(열린) 엣지가 하나도 없는지, 즉 구멍 없이 닫혀 있는지 단언한다.</summary>
    private static void AssertClosed(PolyMesh m)
    {
        Assert.Empty(MeshValidator.Check(m));
        for (int e = 0; e < m.EdgeCount; e++) if (m.Edges[e].Alive) Assert.False(m.IsBoundaryEdge(e), $"edge {e} is open (hole)");
    }

    /// <summary>
    /// 원뿔 꼭짓점 팬에서 엣지를 공유하지 않고 꼭짓점에서만 닿는 삼각형 두 개를 함께 Extrude한다.
    /// 두 영역은 서로 독립이므로 꼭짓점 복제본도 영역마다 따로 만들어져야 하며(정점 +6, 옆면 +6),
    /// 결과가 구멍 없이 닫혀 있어야 한다(v0.0.25에서 고친 "정점에서만 맞닿은 면" 구멍 회귀 방지).
    /// </summary>
    [Fact]
    public void Extrude_FanTrianglesTouchingOnlyAtApex_NoHoles()
    {
        var m = MeshBuilder.Cone();
        var fan = ApexFan(m);
        Assert.True(fan.Count >= 4);
        int f0 = fan[0];
        int f1 = fan.First(f => f != f0 && !SharesEdge(m, f0, f));
        int facesBefore = m.AliveFaceCount, vertsBefore = m.AliveVertexCount;
        var caps = MeshOps.ExtrudeFaces(m, new[] { f0, f1 });
        Assert.Equal(2, caps.Count);
        AssertClosed(m);
        // 두 영역이 꼭짓점에서만 닿으므로 꼭짓점 복제본도 영역마다 따로: 정점 +6, 옆면 3+3
        Assert.Equal(vertsBefore + 6, m.AliveVertexCount);
        Assert.Equal(facesBefore + 6, m.AliveFaceCount);
    }

    /// <summary>
    /// 엣지를 공유하는 이웃 팬 삼각형 두 개를 Extrude하면 하나의 영역이므로 꼭짓점 복제본을 공유해야 한다.
    /// 경계 정점 4개·경계 엣지 4개만큼 정점/옆면이 늘고, 오일러 특성 2(닫힌 구 위상)가 유지되는지 확인한다.
    /// </summary>
    [Fact]
    public void Extrude_AdjacentFanTriangles_SharesApexDuplicate()
    {
        var m = MeshBuilder.Cone();
        var fan = ApexFan(m);
        int f0 = fan[0];
        int f1 = fan.First(f => f != f0 && SharesEdge(m, f0, f));
        int vertsBefore = m.AliveVertexCount, facesBefore = m.AliveFaceCount;
        MeshOps.ExtrudeFaces(m, new[] { f0, f1 });
        AssertClosed(m);
        Assert.Equal(2, Euler(m));
        Assert.Equal(vertsBefore + 4, m.AliveVertexCount); // 경계 정점 4개(꼭짓점 포함) 복제
        Assert.Equal(facesBefore + 4, m.AliveFaceCount);   // 경계 엣지 4개 -> 옆면 4개
    }
}
