using System.Numerics;
using Cube.Core.Mesh;
using Cube.Core.Selection;
using Cube.Core.Uv;

namespace Cube.Core.Tests.Uv;

/// <summary>UV Symmetry(v0.0.65): UV 점/엣지/면 거울 짝, 변형 규칙(주 쪽·반대쪽·축선 위), V축.</summary>
public class UvSymmetryTests
{
    [Fact]
    public void PlaneGrid_PointsMirrorAcrossU05()
    {
        var m = MeshBuilder.Plane(1, 1, 4, 2); // UV 격자 0..1, u = 0.5 열이 축선 위
        var topo = UvTopology.Build(m);
        var map = UvSymmetryMap.Build(topo, new UvSymmetryPlane(0, 0.5f));
        int onLine = 0, paired = 0;
        for (int p = 0; p < topo.Points.Count; p++)
        {
            int mp = map.MirrorPoint(p);
            Assert.True(mp >= 0);
            if (mp == p) { onLine++; Assert.Equal(0.5f, topo.Points[p].Uv.X, 5); }
            else { paired++; Assert.Equal(1f - topo.Points[p].Uv.X, topo.Points[mp].Uv.X, 5); Assert.Equal(topo.Points[p].Uv.Y, topo.Points[mp].Uv.Y, 5); }
        }
        Assert.Equal(3, onLine); Assert.Equal(12, paired);
        for (int e = 0; e < m.EdgeCount; e++) Assert.True(map.MirrorEdge(m, e) >= 0);
        for (int f = 0; f < m.FaceCount; f++)
        {
            int mf = map.MirrorFace(m, f); Assert.True(mf >= 0 && mf != f);
            // 거울 면의 UV 중심 u = 1 − 원래 중심 u
            Assert.Equal(1f - CenterU(m, f), CenterU(m, mf), 5);
        }
        // Expand: 왼쪽 열 면 2개 → 4개
        var set = new HashSet<int>(Enumerable.Range(0, m.FaceCount).Where(f => CenterU(m, f) < 0.2f));
        Assert.Equal(2, set.Count);
        Assert.Equal(0, map.Expand(m, SelectMode.Face, set));
        Assert.Equal(4, set.Count);
    }

    private static void AssertUv(Vector2 expected, Vector2 actual) { Assert.Equal(expected.X, actual.X, 5); Assert.Equal(expected.Y, actual.Y, 5); }

    private static float CenterU(PolyMesh m, int f)
    {
        float s = 0; int n = 0;
        int start = m.Faces[f].HalfEdge, he = start;
        do { s += m.Hes[he].Uv0.X; n++; he = m.Hes[he].Next; } while (he != start);
        return s / n;
    }

    [Fact]
    public void Transform_PrimarySide_FollowsDrag_OtherSideMirrored()
    {
        var plane = new UvSymmetryPlane(0, 0.5f);
        var move = Matrix3x2.CreateTranslation(0.1f, 0.05f);
        AssertUv(new Vector2(0.9f, 0.25f), UvSymmetryOps.Transform(new Vector2(0.8f, 0.2f), move, plane));
        AssertUv(new Vector2(0.1f, 0.25f), UvSymmetryOps.Transform(new Vector2(0.2f, 0.2f), move, plane));
        var onLine = UvSymmetryOps.Transform(new Vector2(0.5f, 0.2f), move, plane);
        Assert.Equal(0.5f, onLine.X, 5); Assert.Equal(0.25f, onLine.Y, 5);
        // 주 쪽이 − 쪽이면 거기가 드래그를 따르고 + 쪽이 거울
        AssertUv(new Vector2(0.3f, 0.25f), UvSymmetryOps.Transform(new Vector2(0.2f, 0.2f), move, plane, positivePrimary: false));
        AssertUv(new Vector2(0.7f, 0.25f), UvSymmetryOps.Transform(new Vector2(0.8f, 0.2f), move, plane, positivePrimary: false));
        // 축선 위 피벗 회전: 양쪽이 서로 거울
        var rot = Matrix3x2.CreateRotation(0.4f, new Vector2(0.5f, 0.5f));
        var a = UvSymmetryOps.Transform(new Vector2(0.8f, 0.7f), rot, plane);
        var b = UvSymmetryOps.Transform(new Vector2(0.2f, 0.7f), rot, plane);
        Assert.Equal(1f - a.X, b.X, 5); Assert.Equal(a.Y, b.Y, 5);
    }

    [Fact]
    public void VAxis_AndAsymmetricPointHasNoMirror()
    {
        var m = MeshBuilder.Plane(1, 1, 2, 2);
        var topo = UvTopology.Build(m);
        var plane = new UvSymmetryPlane(1, 0.5f);
        var map = UvSymmetryMap.Build(topo, plane);
        int p = Enumerable.Range(0, topo.Points.Count).First(i => topo.Points[i].Uv.Y < 0.1f && topo.Points[i].Uv.X < 0.1f);
        int mp = map.MirrorPoint(p);
        Assert.True(mp >= 0); Assert.Equal(1f, topo.Points[mp].Uv.Y, 5); Assert.Equal(0f, topo.Points[mp].Uv.X, 5);
        // 점 하나를 비대칭으로 옮기면 짝이 없다
        UvOps.SetPointUv(m, topo, p, new Vector2(0.03f, 0.0f));
        var map2 = UvSymmetryMap.Build(topo, plane);
        Assert.Equal(-1, map2.MirrorPoint(p));
        Assert.Equal(-1, map2.MirrorPoint(mp));
    }
}
