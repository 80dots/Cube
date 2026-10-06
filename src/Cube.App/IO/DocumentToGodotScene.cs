using Cube.App.Bridge;
using Cube.Core.Mesh;
using Cube.Core.Scene;
using Godot;

namespace Cube.App.IO;

/// <summary>Document 노드들을 내보내기용 임시 Godot Node3D 트리로 만든다. 호출자가 사용 후 Free() 해야 한다.</summary>
public static class DocumentToGodotScene
{
    private static readonly StandardMaterial3D DefaultMaterial = new()
    {
        ResourceName = "lambert1",
        AlbedoColor = new Color(0.5f, 0.5f, 0.5f),
        Roughness = 1f,
        Metallic = 0f,
    };

    public static (Node3D root, int nodeCount, int triCount) Build(IReadOnlyList<SceneNode> nodes, string rootName)
    {
        var root = new Node3D { Name = rootName };
        int count = 0, tris = 0;
        // 선택한 노드의 조상이 함께 선택되었으면 조상만 처리(하위는 재귀로 따라감)
        var set = new HashSet<SceneNode>(nodes);
        foreach (var n in nodes)
        {
            bool ancestorIn = false;
            for (var p = n.Parent; p != null && !p.IsRoot; p = p.Parent) if (set.Contains(p)) { ancestorIn = true; break; }
            if (ancestorIn) continue;
            BuildNode(n, root, ref count, ref tris, n.Parent != null && !n.Parent.IsRoot);
        }
        return (root, count, tris);
    }

    private static void BuildNode(SceneNode n, Node3D parent, ref int count, ref int tris, bool bakeWorld)
    {
        Node3D g;
        if (n.Mesh != null)
        {
            var mesh = n.Mesh.Clone();
            mesh.Compact();
            MeshNormals.Recompute(mesh);
            var render = MeshTessellator.Build(mesh);
            var arr = new ArrayMesh { ResourceName = n.Name + "Shape" };
            new GodotMeshBridge().UploadSurface(arr, render);
            if (arr.GetSurfaceCount() > 0) arr.SurfaceSetMaterial(0, DefaultMaterial);
            tris += render.TriangleCount;
            g = new MeshInstance3D { Mesh = arr };
        }
        else g = new Node3D();
        g.Name = n.Name;
        // 부모가 내보내기 대상이 아니면 월드 트랜스폼을 베이크
        g.Transform = bakeWorld ? n.WorldMatrix.ToGodot() : n.Local.ToGodot();
        parent.AddChild(g);
        count++;
        foreach (var c in n.Children) BuildNode(c, g, ref count, ref tris, false);
    }
}
