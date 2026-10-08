using System.Numerics;
using Cube.Core.Mesh;

namespace Cube.Core.Tests.Mesh;

/// <summary>
/// 기본 도형 생성기 <c>MeshBuilder</c>(Cube/Plane/Cylinder/Sphere/Torus/Cone)를 검증한다.
/// 요소 수, 메시 유효성, 닫힘(경계 엣지 없음), 오일러 특성(구 위상 2, 토러스 0), 법선 방향을 확인한다.
/// </summary>
public class MeshBuilderTests
{
    /// <summary>큐브는 정점 8·엣지 12·면 6·하프에지 24이고, 경계 엣지 없는 닫힌 매니폴드(V-E+F=2)여야 한다.</summary>
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

    /// <summary>원점 중심 큐브의 모든 면 법선이 면 중심 방향(바깥)을 향하는지, 즉 CCW 감김이 올바른지 확인한다.</summary>
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

    /// <summary>기본 큐브는 모든 엣지가 하드라 각 코너 노멀이 자기 면 법선과 같아야 한다(각진 셰이딩).</summary>
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

    /// <summary>
    /// 모든 엣지를 소프트로 바꾸고 노멀을 다시 계산하면 코너 노멀이 인접 세 면 법선의 평균, 즉 원점→정점 방향이 되어야 한다.
    /// </summary>
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

    /// <summary>
    /// 2x2 분할 평면은 이웃 면이 정점/엣지를 공유해 정점 9·엣지 12·면 4가 되고, 경계 엣지 8개, 모든 면 법선이 +Y여야 한다.
    /// </summary>
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

    /// <summary>8분할 원기둥은 정점 16·면 10·엣지 24의 닫힌 매니폴드이고 캡 두 개가 8각형 n-gon이어야 한다.</summary>
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

    /// <summary>
    /// 여러 해상도의 구가 닫힌 매니폴드(경계 없음, 오일러 2)이고 모든 면 법선이 바깥을 향하는지 확인한다.
    /// 극점의 삼각형 팬도 올바르게 연결되어야 한다.
    /// </summary>
    /// <param name="segments">경도 방향 분할 수.</param>
    /// <param name="rings">위도 방향 분할 수.</param>
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

    /// <summary>토러스는 구멍이 하나(genus 1)인 닫힌 매니폴드라 오일러 특성이 0이어야 한다.</summary>
    [Fact]
    public void Torus_IsClosedManifold_GenusOne()
    {
        var m = MeshBuilder.Torus(segments: 8, sections: 6);
        Assert.Empty(MeshValidator.Check(m));
        for (int e = 0; e < m.EdgeCount; e++) Assert.False(m.IsBoundaryEdge(e));
        Assert.Equal(0, m.AliveVertexCount - m.AliveEdgeCount + m.AliveFaceCount);
    }

    /// <summary>원뿔(꼭짓점 팬 + 바닥 n각형)이 유효한 닫힌 메시(오일러 2)인지 확인한다.</summary>
    [Fact]
    public void Cone_IsClosedManifold()
    {
        var m = MeshBuilder.Cone(segments: 6);
        Assert.Empty(MeshValidator.Check(m));
        Assert.Equal(2, m.AliveVertexCount - m.AliveEdgeCount + m.AliveFaceCount);
    }
}
