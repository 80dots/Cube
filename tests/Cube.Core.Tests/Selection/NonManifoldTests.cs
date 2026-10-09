using System.Numerics;
using Cube.Core.Mesh;
using Cube.Core.Selection;

namespace Cube.Core.Tests.Selection;

/// <summary>Select → Non-Manifold: 닫힌 메시는 없음, 열린 판은 경계, 꼭짓점으로만 맞닿은 상자는 나비넥타이 정점, 고립 정점.</summary>
public class NonManifoldTests
{
    private static readonly NonManifoldOptions All = new();

    [Fact]
    public void ClosedCube_HasNone()
    {
        var (v, e) = NonManifold.Find(MeshBuilder.Cube(), All);
        Assert.Empty(v); Assert.Empty(e);
    }

    [Fact]
    public void OpenPlane_BoundaryEdgesAndVerts()
    {
        var m = MeshBuilder.Plane(1, 1, 2, 2);
        var (v, e) = NonManifold.Find(m, All);
        Assert.Equal(8, e.Count);
        Assert.Equal(8, v.Count);
        // 경계를 끄면 아무것도 없다(평면 안쪽 정점은 매니폴드)
        var (v2, e2) = NonManifold.Find(m, new NonManifoldOptions { Boundaries = false });
        Assert.Empty(v2); Assert.Empty(e2);
    }

    [Fact]
    public void CubesTouchingAtCorner_BowtieVertex()
    {
        var m = MeshBuilder.Cube();
        MeshOps.Append(m, MeshBuilder.Cube(), Matrix4x4.CreateTranslation(1, 1, 1));
        // (0.5,0.5,0.5) 두 꼭짓점을 하나로 합치면 두 상자가 정점 하나로만 이어진다
        int n = MeshOps.MergeVertices(m, Enumerable.Range(0, m.VertexCount), 0.001f);
        Assert.Equal(1, n);
        var (v, e) = NonManifold.Find(m, All);
        Assert.Single(v);
        Assert.Equal(new Vector3(0.5f, 0.5f, 0.5f), m.Verts[v.First()].Position);
        Assert.Equal(6, e.Count); // 그 정점에 닿은 엣지 3 + 3
        Assert.Empty(NonManifold.Find(m, new NonManifoldOptions { Bowtie = false }).verts);
    }

    [Fact]
    public void IsolatedVertex_Found()
    {
        var m = MeshBuilder.Cube();
        int iso = m.AddVertex(new Vector3(5, 0, 0));
        var (v, e) = NonManifold.Find(m, All);
        Assert.Equal(new[] { iso }, v.ToArray());
        Assert.Empty(e);
    }

    /// <summary>경계 정점(열린 판의 모서리 등) 자체는 나비넥타이가 아니다.</summary>
    [Fact]
    public void BoundaryCornerOfSingleFan_IsNotBowtie()
    {
        var m = MeshBuilder.Plane(1, 1, 3, 3);
        Assert.Empty(NonManifold.Find(m, new NonManifoldOptions { Boundaries = false, Isolated = false }).verts);
    }
}
