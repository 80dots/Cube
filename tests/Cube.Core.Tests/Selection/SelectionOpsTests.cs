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
}
