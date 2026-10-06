using System.Numerics;
using Cube.Core.Mesh;

namespace Cube.Core.Tests.Mesh;

public class MeshBuilderTests
{
    [Fact]
    public void Cube_HasExpectedCountsAndIsValid()
    {
        var m = MeshBuilder.Cube();
        Assert.Equal(8, m.AliveVertexCount);
        Assert.Equal(12, m.AliveEdgeCount);
        Assert.Equal(6, m.AliveFaceCount);
        Assert.Equal(24, m.HalfEdgeCount);
        Assert.Empty(MeshValidator.Check(m));
        // 닫힌 매니폴드: 경계 엣지 없음, 오일러 V-E+F=2
        for (int e = 0; e < m.EdgeCount; e++) Assert.False(m.IsBoundaryEdge(e));
        Assert.Equal(2, m.AliveVertexCount - m.AliveEdgeCount + m.AliveFaceCount);
    }

    [Fact]
    public void Cube_FaceNormalsPointOutward()
    {
        var m = MeshBuilder.Cube();
        for (int f = 0; f < m.FaceCount; f++)
        {
            var c = m.FaceCentroid(f);
            Assert.True(Vector3.Dot(m.Faces[f].Normal, c) > 0.4f, $"face {f} normal {m.Faces[f].Normal} centroid {c}");
        }
    }

    [Fact]
    public void Cube_HardEdgesGiveFaceNormalsAtCorners()
    {
        var m = MeshBuilder.Cube();
        for (int h = 0; h < m.HalfEdgeCount; h++)
        {
            var he = m.Hes[h];
            var fn = m.Faces[he.Face].Normal;
            Assert.True(Vector3.Distance(he.Normal, fn) < 1e-5f);
        }
    }

    [Fact]
    public void Cube_SoftEdgesGiveSmoothedCornerNormals()
    {
        var m = MeshBuilder.Cube();
        MeshBuilder.SetAllEdgesHard(m, false);
        MeshNormals.Recompute(m);
        for (int h = 0; h < m.HalfEdgeCount; h++)
        {
            var he = m.Hes[h];
            var expected = Vector3.Normalize(m.Verts[he.Vertex].Position);
            Assert.True(Vector3.Distance(he.Normal, expected) < 1e-4f, $"he {h}: {he.Normal} vs {expected}");
        }
    }

    [Fact]
    public void Plane_Subdivided_SharesEdges()
    {
        var m = MeshBuilder.Plane(2, 2, 2, 2);
        Assert.Equal(9, m.AliveVertexCount);
        Assert.Equal(12, m.AliveEdgeCount);
        Assert.Equal(4, m.AliveFaceCount);
        Assert.Empty(MeshValidator.Check(m));
        int boundary = 0;
        for (int e = 0; e < m.EdgeCount; e++) if (m.IsBoundaryEdge(e)) boundary++;
        Assert.Equal(8, boundary);
        for (int f = 0; f < m.FaceCount; f++) Assert.True(Vector3.Distance(m.Faces[f].Normal, Vector3.UnitY) < 1e-5f);
    }

    [Fact]
    public void Cylinder_IsClosedManifoldWithNgonCaps()
    {
        var m = MeshBuilder.Cylinder(segments: 8);
        Assert.Equal(16, m.AliveVertexCount);
        Assert.Equal(10, m.AliveFaceCount);
        Assert.Equal(24, m.AliveEdgeCount);
        Assert.Empty(MeshValidator.Check(m));
        for (int e = 0; e < m.EdgeCount; e++) Assert.False(m.IsBoundaryEdge(e));
        int ngons = 0;
        for (int f = 0; f < m.FaceCount; f++) if (m.FaceDegree(f) == 8) ngons++;
        Assert.Equal(2, ngons);
    }

    [Theory]
    [InlineData(8, 4)]
    [InlineData(20, 10)]
    public void Sphere_IsClosedManifold(int segments, int rings)
    {
        var m = MeshBuilder.Sphere(segments: segments, rings: rings);
        Assert.Empty(MeshValidator.Check(m));
        for (int e = 0; e < m.EdgeCount; e++) Assert.False(m.IsBoundaryEdge(e));
        Assert.Equal(2, m.AliveVertexCount - m.AliveEdgeCount + m.AliveFaceCount);
        for (int f = 0; f < m.FaceCount; f++)
            Assert.True(Vector3.Dot(m.Faces[f].Normal, m.FaceCentroid(f)) > 0);
    }

    [Fact]
    public void Torus_IsClosedManifold_GenusOne()
    {
        var m = MeshBuilder.Torus(segments: 8, sections: 6);
        Assert.Empty(MeshValidator.Check(m));
        for (int e = 0; e < m.EdgeCount; e++) Assert.False(m.IsBoundaryEdge(e));
        Assert.Equal(0, m.AliveVertexCount - m.AliveEdgeCount + m.AliveFaceCount);
    }

    [Fact]
    public void Cone_IsClosedManifold()
    {
        var m = MeshBuilder.Cone(segments: 6);
        Assert.Empty(MeshValidator.Check(m));
        Assert.Equal(2, m.AliveVertexCount - m.AliveEdgeCount + m.AliveFaceCount);
    }
}
