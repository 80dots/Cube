using System.Numerics;
using Cube.Core.Mesh;

namespace Cube.Core.Tests.Mesh;

/// <summary>
/// <c>MeshOps.FaceLoop(A, B)</c>(면 모드에서 이웃 면 더블클릭 시 면 루프 선택)를 검증한다.
/// A에서 B 방향으로, 공유 엣지의 반대편 엣지를 건너 쿼드를 따라가고 n각형/경계에서 멈춰야 한다.
/// </summary>
public class FaceLoopTests
{
    /// <summary>면 정점 위치의 평균(면 중심)을 구하는 도우미.</summary>
    private static Vector3 Center(PolyMesh m, int f)
    {
        var l = new List<int>(); m.GetFaceVertices(f, l);
        var s = Vector3.Zero; foreach (int v in l) s += m.Verts[v].Position; return s / l.Count;
    }

    /// <summary>
    /// 면 <paramref name="f"/>의 하프에지를 한 바퀴 돌며 트윈 쪽 이웃 면 중 조건 <paramref name="pred"/>를 만족하는 첫 면을 돌려준다. 없으면 -1.
    /// </summary>
    private static int Neighbor(PolyMesh m, int f, Func<int, bool> pred)
    {
        int start = m.Faces[f].HalfEdge, he = start;
        do { int tw = m.Hes[he].Twin; if (tw >= 0 && pred(m.Hes[tw].Face)) return m.Hes[tw].Face; he = m.Hes[he].Next; } while (he != start);
        return -1;
    }

    /// <summary>
    /// 12분할 원기둥에서 옆면과 그 옆 옆면으로 루프를 만들면 옆면 12개 전체가 한 바퀴 닫힌 루프로 선택되어야 한다(중복 없음, 모두 쿼드).
    /// </summary>
    [Fact]
    public void Cylinder_SideNeighbor_SelectsWholeRing()
    {
        var m = MeshBuilder.Cylinder(0.5f, 1f, 12, caps: true);
        int a = Enumerable.Range(0, m.FaceCount).First(f => m.FaceDegree(f) == 4);
        int b = Neighbor(m, a, g => m.FaceDegree(g) == 4);
        var loop = MeshOps.FaceLoop(m, a, b);
        Assert.Equal(12, loop.Count);
        Assert.Equal(12, loop.Distinct().Count());
        Assert.All(loop, f => Assert.Equal(4, m.FaceDegree(f)));
    }

    /// <summary>
    /// 옆면과 캡(n각형) 방향으로 루프를 만들면 세로로 진행해 위 캡 → 옆면 → 아래 캡 3개만 선택되어야 한다.
    /// n각형은 루프에 포함하되 그 너머로는 진행하지 않는 규칙을 확인한다.
    /// </summary>
    [Fact]
    public void Cylinder_CapNeighbor_GoesVerticallyAndStopsAtCaps()
    {
        var m = MeshBuilder.Cylinder(0.5f, 1f, 12, caps: true);
        int a = Enumerable.Range(0, m.FaceCount).First(f => m.FaceDegree(f) == 4);
        int cap = Neighbor(m, a, g => m.FaceDegree(g) != 4);
        var loop = MeshOps.FaceLoop(m, a, cap);
        // 위 캡 → 옆면 → 아래 캡
        Assert.Equal(3, loop.Count);
        Assert.Contains(a, loop);
        Assert.Equal(2, loop.Count(f => m.FaceDegree(f) != 4));
    }

    /// <summary>
    /// 4x4 평면에서 중앙 근처 면과 그 오른쪽 면을 고르면 같은 행(Z 동일)의 4면이 선택되어 두 번째 면이 방향을 결정함을 확인한다.
    /// 같은 면을 두 번 주면(A == B) 방향이 없으므로 빈 결과여야 한다.
    /// </summary>
    [Fact]
    public void Grid_Direction_FollowsSecondFace()
    {
        var m = MeshBuilder.Plane(1f, 1f, 4, 4);
        int a = Enumerable.Range(0, m.FaceCount).OrderBy(f => Center(m, f).LengthSquared()).First();
        int right = Neighbor(m, a, g => Center(m, g).X > Center(m, a).X + 1e-4f);
        var row = MeshOps.FaceLoop(m, a, right);
        Assert.Equal(4, row.Count);
        float z = Center(m, a).Z;
        Assert.All(row, f => Assert.True(MathF.Abs(Center(m, f).Z - z) < 1e-4f));
        Assert.Empty(MeshOps.FaceLoop(m, a, a));
    }
}
