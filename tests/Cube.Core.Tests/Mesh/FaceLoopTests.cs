using System.Numerics;
using Cube.Core.Mesh;

namespace Cube.Core.Tests.Mesh;

public class FaceLoopTests
{
    private static Vector3 Center(PolyMesh m, int f)
    {
        var l = new List<int>(); m.GetFaceVertices(f, l);
        var s = Vector3.Zero; foreach (int v in l) s += m.Verts[v].Position; return s / l.Count;
    }

    private static int Neighbor(PolyMesh m, int f, Func<int, bool> pred)
    {
        int start = m.Faces[f].HalfEdge, he = start;
        do { int tw = m.Hes[he].Twin; if (tw >= 0 && pred(m.Hes[tw].Face)) return m.Hes[tw].Face; he = m.Hes[he].Next; } while (he != start);
        return -1;
    }

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
