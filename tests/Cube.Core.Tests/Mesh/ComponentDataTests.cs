using System.Numerics;
using Cube.Core.Commands;
using Cube.Core.Mesh;
using Cube.Core.Rig;
using Cube.Core.Scene;

namespace Cube.Core.Tests.Mesh;

/// <summary>Component Editor 데이터 접근(UV 세트별 읽기/쓰기, UV/코너 행)과 스냅샷 명령.</summary>
public class ComponentDataTests
{
    /// <summary>
    /// Component Editor의 UV 행 구성: 정점별로 같은 UV를 가진 코너들이 한 행으로 묶여야 한다.
    /// 모든 면 코너가 정확히 한 행에 한 번씩 들어가고, 한 행 안의 코너 UV가 모두 같으며,
    /// 코너 행(CornerRows)은 코너 수와 같은 개수인지 확인한다.
    /// </summary>
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

    /// <summary>
    /// UV 세트 인덱스를 지정해 쓰기: 현재 세트가 아닌 세트(map2)에 쓰면 현재 세트(Uv0)는 그대로이고,
    /// 그 세트로 전환했을 때 써 둔 값이 나타나야 한다. 현재 세트(0)에 쓰면 Uv0에 바로 반영된다.
    /// </summary>
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

    /// <summary>
    /// <c>ComponentEditCommand</c>(스냅샷 기반 명령)가 정점 위치·비현재 UV 세트·스킨 가중치를 한 번에 바꾸고,
    /// Undo 시 셋 모두 원래 값으로, Redo 시 다시 편집 값으로 돌아오는지 확인한다.
    /// 가중치는 정규화 쓰기라 조인트 0이 0.8이면 조인트 1은 0.2가 되어야 한다.
    /// </summary>
    [Fact]
    public void ComponentEditCommand_UndoRestoresPositionsUvSetsAndWeights()
    {
        // 준비: 큐브 노드 + 두 번째 UV 세트 + 모든 정점이 조인트 0/1에 0.5씩 묶인 스킨.
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

        // 실행: 람다 안에서 위치·UV·가중치를 함께 편집하는 명령을 푸시한다.
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

        // Undo: 세 종류 데이터가 모두 원래 값으로 복원되어야 한다.
        doc.Undo.Undo();
        Assert.Equal(p0, mesh.Verts[2].Position);
        Assert.Equal(uv0, ComponentData.GetUv(mesh, 1, 3));
        Assert.Equal(0.5f, node.MeshShape!.Skin!.GetWeight(4, 0), 4);

        // Redo: 편집 값이 다시 적용되어야 한다.
        doc.Undo.Redo();
        Assert.Equal(new Vector2(9, 9), ComponentData.GetUv(mesh, 1, 3));
        Assert.Equal(0.8f, node.MeshShape!.Skin!.GetWeight(4, 0), 4);
    }
}
