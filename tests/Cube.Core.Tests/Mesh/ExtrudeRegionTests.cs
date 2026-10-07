using Cube.Core.Mesh;

namespace Cube.Core.Tests.Mesh;

/// <summary>선택 면들이 한 정점에서만 맞닿거나 팬 중심을 공유할 때 Extrude가 구멍/뒤집힌 면 없이 닫힌 메시를 유지하는지.</summary>
public class ExtrudeRegionTests
{
    private static int Euler(PolyMesh m) => m.AliveVertexCount - m.AliveEdgeCount + m.AliveFaceCount;

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

    private static bool SharesEdge(PolyMesh m, int a, int b)
    {
        int start = m.Faces[a].HalfEdge, he = start;
        do { int tw = m.Hes[he].Twin; if (tw >= 0 && m.Hes[tw].Face == b) return true; he = m.Hes[he].Next; } while (he != start);
        return false;
    }

    private static void AssertClosed(PolyMesh m)
    {
        Assert.Empty(MeshValidator.Check(m));
        for (int e = 0; e < m.EdgeCount; e++) if (m.Edges[e].Alive) Assert.False(m.IsBoundaryEdge(e), $"edge {e} is open (hole)");
    }

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
