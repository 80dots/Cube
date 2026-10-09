using Cube.Core.Mesh;
using Cube.Core.Selection;

namespace Cube.Core.Tests.Selection;

/// <summary>
/// <c>SelectionOps</c>의 선택 확장/축소(Grow/Shrink)와 컴포넌트 모드 간 변환(Convert)을 검증한다.
/// </summary>
public class SelectionOpsTests
{
    /// <summary>
    /// 큐브 정점 하나에서 Grow를 두 번 하면 4개(자신+이웃 3) → 7개(반대 꼭짓점 제외)로 늘고,
    /// Shrink를 두 번 하면 정확히 역순으로 원래 정점 하나로 돌아오는지 확인한다.
    /// </summary>
    [Fact]
    public void Grow_Vertex_OnCube_AddsNeighbors()
    {
        var m = MeshBuilder.Cube();
        var set = new HashSet<int> { 0 };
        SelectionOps.Grow(m, SelectMode.Vertex, set);
        Assert.Equal(4, set.Count); // 자신 + 인접 3
        SelectionOps.Grow(m, SelectMode.Vertex, set);
        Assert.Equal(7, set.Count); // 반대 꼭짓점만 남음
        SelectionOps.Shrink(m, SelectMode.Vertex, set);
        Assert.Equal(4, set.Count); // 반대 꼭짓점과 이웃한 3개가 빠짐
        SelectionOps.Shrink(m, SelectMode.Vertex, set);
        Assert.Equal(new HashSet<int> { 0 }, set);
    }

    /// <summary>
    /// 5x5 평면의 가운데 면(12)에서 Grow하면 정점을 공유하는 3x3 블록이 되고,
    /// Shrink하면 다시 가운데 면 하나, 한 번 더 Shrink하면 빈 선택이 되는지 확인한다.
    /// </summary>
    [Fact]
    public void Grow_Face_OnPlane_ThenShrink()
    {
        var m = MeshBuilder.Plane(5, 5, 5, 5); // 25면, 행 우선 인덱스
        var set = new HashSet<int> { 12 };    // 중앙
        SelectionOps.Grow(m, SelectMode.Face, set);
        Assert.Equal(new HashSet<int> { 6, 7, 8, 11, 12, 13, 16, 17, 18 }, set);
        SelectionOps.Shrink(m, SelectMode.Face, set);
        Assert.Equal(new HashSet<int> { 12 }, set);
        SelectionOps.Shrink(m, SelectMode.Face, set);
        Assert.Empty(set);
    }

    /// <summary>
    /// 면 → 엣지(4) → 정점(4) → 면 변환을 차례로 한다. 정점→면은 정점을 하나라도 포함하는 면(5개)을,
    /// 정점→엣지는 양 끝이 모두 선택된 엣지(4개)만 고르는 Maya 규칙을 따르는지 확인한다.
    /// </summary>
    [Fact]
    public void Convert_FaceToEdgesToVerticesToFaces()
    {
        var m = MeshBuilder.Cube();
        var comps = new ComponentSet();
        comps.Faces.Add(0);
        var edges = SelectionOps.Convert(m, comps, SelectMode.Face, SelectMode.Edge);
        Assert.Equal(4, edges.Count);
        comps.Edges.UnionWith(edges);
        var verts = SelectionOps.Convert(m, comps, SelectMode.Edge, SelectMode.Vertex);
        Assert.Equal(4, verts.Count);
        comps.Verts.UnionWith(verts);
        var faces = SelectionOps.Convert(m, comps, SelectMode.Vertex, SelectMode.Face);
        Assert.Equal(5, faces.Count); // 정점 4개를 하나라도 포함하는 면: 앞면 + 옆면 4
        var edgesFromVerts = SelectionOps.Convert(m, comps, SelectMode.Vertex, SelectMode.Edge);
        Assert.Equal(4, edgesFromVerts.Count); // 양 끝이 모두 선택된 엣지만
    }
    /// <summary>
    /// 엣지 → 면 변환은 엣지 양쪽에 붙은 면만(Maya). 격자 내부 엣지 하나는 면 2개, 경계 엣지는 1개.
    /// 예전에는 양끝 정점에 닿는 모든 면(6개)이었다.
    /// </summary>
    [Fact]
    public void Convert_EdgeToFace_AdjacentFacesOnly()
    {
        var m = MeshBuilder.Plane(3, 3, 3, 3);
        int inner = -1, border = -1;
        for (int e = 0; e < m.EdgeCount; e++)
        {
            var (f0, f1) = m.EdgeFaces(e);
            if (f0 >= 0 && f1 >= 0 && inner < 0) inner = e;
            if ((f0 < 0 || f1 < 0) && border < 0) border = e;
        }
        var comps = new ComponentSet();
        comps.Edges.Add(inner);
        Assert.Equal(2, SelectionOps.Convert(m, comps, SelectMode.Edge, SelectMode.Face).Count);
        comps.Edges.Clear(); comps.Edges.Add(border);
        Assert.Single(SelectionOps.Convert(m, comps, SelectMode.Edge, SelectMode.Face));
    }

    /// <summary>
    /// UV 점 Grow/Shrink: 평면(UV 연속) 가운데 UV 점에서 Grow하면 이웃(같은 면의 앞뒤 코너)이 더해지고 Shrink하면 원래대로.
    /// 심으로 끊긴 큐브에서는 Grow가 심 건너편 UV 점을 더하지 않는다(같은 정점이라도 다른 UV 점).
    /// </summary>
    [Fact]
    public void GrowShrinkUv_FollowsUvNeighbors()
    {
        var m = MeshBuilder.Plane(2, 2, 2, 2); // 정점 3x3, 가운데 정점 4
        var topo = Cube.Core.Uv.UvTopology.Build(m);
        int center = topo.Points.FindIndex(p => p.Vertex == 4);
        Assert.True(center >= 0);
        var set = new HashSet<int> { center };
        SelectionOps.GrowUv(m, topo, set);
        Assert.Equal(5, set.Count); // 자신 + 쿼드 엣지 이웃 4
        SelectionOps.ShrinkUv(m, topo, set);
        Assert.Equal(new HashSet<int> { center }, set);
    }
}
