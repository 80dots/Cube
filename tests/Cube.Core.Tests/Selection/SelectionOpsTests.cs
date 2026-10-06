using Cube.Core.Mesh;
using Cube.Core.Selection;

namespace Cube.Core.Tests.Selection;

public class SelectionOpsTests
{
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
