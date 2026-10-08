using System.Numerics;
using Cube.Core.Commands;
using Cube.Core.Mesh;
using Cube.Core.Rig;
using Cube.Core.Scene;

namespace Cube.Core.Tests.Mesh;

/// <summary>Component Editor 데이터 접근(UV 세트별 읽기/쓰기, UV/코너 행)과 스냅샷 명령.</summary>
public class ComponentDataTests
{
    [Fact]
    public void UvRows_GroupCornersWithSameUvPerVertex()
    {
        var m = MeshBuilder.Cube();
        var corners = ComponentData.CornersByVertex(m);
        var verts = Enumerable.Range(0, m.VertexCount).ToList();
        var rows = ComponentData.UvRows(m, 0, verts, corners);
        // 모든 코너가 정확히 한 행에 들어간다
        Assert.Equal(m.Hes.Count(h => h.Alive && h.Face >= 0), rows.Sum(r => r.Corners.Length));
        foreach (var r in rows)
            foreach (int h in r.Corners) Assert.Equal(m.Hes[r.Corners[0]].Uv0, m.Hes[h].Uv0);
        Assert.Equal(m.Hes.Count(h => h.Alive && h.Face >= 0), ComponentData.CornerRows(m, verts, corners).Count);
    }

    [Fact]
    public void SetUv_WritesCurrentOrOtherSet()
    {
        var m = MeshBuilder.Cube();
        int second = m.AddUvSet("map2", copyCurrent: true);
        Assert.Equal(0, m.CurrentUvSet);
        ComponentData.SetUv(m, second, 0, new Vector2(0.25f, 0.75f));
        Assert.Equal(new Vector2(0.25f, 0.75f), ComponentData.GetUv(m, second, 0));
        Assert.NotEqual(new Vector2(0.25f, 0.75f), m.Hes[0].Uv0); // 현재 세트는 그대로
        ComponentData.SetUv(m, 0, 1, new Vector2(0.5f, 0.5f));
        Assert.Equal(new Vector2(0.5f, 0.5f), m.Hes[1].Uv0);
        m.SwitchUvSet(second);
        Assert.Equal(new Vector2(0.25f, 0.75f), m.Hes[0].Uv0);
    }

    [Fact]
    public void ComponentEditCommand_UndoRestoresPositionsUvSetsAndWeights()
    {
        var doc = new Document();
        var node = new SceneNode { Name = "m", Shape = new MeshShape(MeshBuilder.Cube()) };
        doc.AddNode(node);
        var mesh = node.Mesh!;
        mesh.AddUvSet("map2", copyCurrent: true);
        var skin = new SkinCluster();
        skin.EnsureSize(mesh.VertexCount);
        for (int v = 0; v < mesh.VertexCount; v++) skin.Weights[v] = new List<(int, float)> { (0, 0.5f), (1, 0.5f) };
        node.MeshShape!.Skin = skin;
        var p0 = mesh.Verts[2].Position; var uv0 = ComponentData.GetUv(mesh, 1, 3);

        doc.Undo.Push(new ComponentEditCommand("Edit", node.Id, (m, s) =>
        {
            var v = m.Verts[2]; v.Position += Vector3.One; m.Verts[2] = v;
            ComponentData.SetUv(m, 1, 3, new Vector2(9, 9));
            SkinOps.SetWeightNormalized(s!, 4, 0, 0.8f);
        }));
        Assert.Equal(p0 + Vector3.One, mesh.Verts[2].Position);
        Assert.Equal(new Vector2(9, 9), ComponentData.GetUv(mesh, 1, 3));
        Assert.Equal(0.8f, skin.GetWeight(4, 0), 4);
        Assert.Equal(0.2f, skin.GetWeight(4, 1), 4);

        doc.Undo.Undo();
        Assert.Equal(p0, mesh.Verts[2].Position);
        Assert.Equal(uv0, ComponentData.GetUv(mesh, 1, 3));
        Assert.Equal(0.5f, node.MeshShape!.Skin!.GetWeight(4, 0), 4);

        doc.Undo.Redo();
        Assert.Equal(new Vector2(9, 9), ComponentData.GetUv(mesh, 1, 3));
        Assert.Equal(0.8f, node.MeshShape!.Skin!.GetWeight(4, 0), 4);
    }
}
