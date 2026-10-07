using System.Numerics;
using Cube.App.Bridge;
using Cube.Core.Mesh;
using Cube.Core.Rig;
using Cube.Core.Scene;
using Godot;
using NMat = System.Numerics.Matrix4x4;
using NVec3 = System.Numerics.Vector3;

namespace Cube.App.IO;

/// <summary>
/// Document 노드들을 내보내기용 임시 Godot Node3D 트리로 만든다. 호출자가 사용 후 Free() 해야 한다.
/// 조인트 계층은 Skeleton3D(루트 조인트마다 하나)로, skinCluster가 있는 메시는 그 스켈레톤의 자식 MeshInstance3D(스켈레톤 공간에 베이크, BONES/WEIGHTS + Skin)로 나간다.
/// 바인드 포즈 = 내보내기 시점의 현재 포즈.
/// </summary>
public static class DocumentToGodotScene
{
    private static readonly StandardMaterial3D DefaultMaterial = new()
    {
        ResourceName = "lambert1",
        AlbedoColor = new Color(0.5f, 0.5f, 0.5f),
        Roughness = 1f,
        Metallic = 0f,
    };

    private sealed class Ctx
    {
        public readonly Node3D Root;
        public readonly Dictionary<NodeId, (Skeleton3D skel, int bone, NMat skelWorld)> Bones = new();
        public readonly HashSet<NodeId> InSet = new();
        public int Count, Tris;
        public Ctx(Node3D root) { Root = root; }
    }

    public static (Node3D root, int nodeCount, int triCount) Build(IReadOnlyList<SceneNode> nodes, string rootName)
    {
        var root = new Node3D { Name = rootName };
        var ctx = new Ctx(root);
        // 선택한 노드의 조상이 함께 선택되었으면 조상만 처리(하위는 재귀로 따라감)
        var set = new HashSet<SceneNode>(nodes);
        var tops = new List<SceneNode>();
        foreach (var n in nodes)
        {
            bool ancestorIn = false;
            for (var p = n.Parent; p != null && !p.IsRoot; p = p.Parent) if (set.Contains(p)) { ancestorIn = true; break; }
            if (!ancestorIn) tops.Add(n);
        }
        foreach (var t in tops) { ctx.InSet.Add(t.Id); foreach (var d in t.Descendants()) ctx.InSet.Add(d.Id); }

        // 1) 스켈레톤: 내보내기 집합 안의 루트 조인트(부모가 조인트가 아님)마다 Skeleton3D
        foreach (var t in tops)
            foreach (var j in new[] { t }.Concat(t.Descendants()))
                if (j.IsJoint && (j.Parent == null || j.Parent.IsRoot || !j.Parent.IsJoint || !ctx.InSet.Contains(j.Parent.Id)))
                    BuildSkeleton(ctx, j);

        // 2) 일반 노드(조인트는 스켈레톤에 들어갔으므로 건너뜀)
        foreach (var t in tops)
        {
            if (t.IsJoint) { foreach (var c in t.Children) if (!c.IsJoint) BuildNode(ctx, c, root, bakeWorld: true); continue; }
            BuildNode(ctx, t, root, t.Parent != null && !t.Parent.IsRoot);
        }
        return (root, ctx.Count, ctx.Tris);
    }

    private static void BuildSkeleton(Ctx ctx, SceneNode rootJoint)
    {
        var skel = new Skeleton3D { Name = rootJoint.Name + "_skeleton" };
        var parentWorld = rootJoint.Parent != null && !rootJoint.Parent.IsRoot ? rootJoint.Parent.WorldMatrix : NMat.Identity;
        skel.Transform = parentWorld.ToGodot();
        ctx.Root.AddChild(skel);
        ctx.Count++;
        void AddBone(SceneNode j, int parentBone)
        {
            int b = skel.AddBone(j.Name);
            if (parentBone >= 0) skel.SetBoneParent(b, parentBone);
            var local = j.Local.ToGodot();
            skel.SetBoneRest(b, local);
            skel.SetBonePosePosition(b, local.Origin);
            skel.SetBonePoseRotation(b, local.Basis.GetRotationQuaternion());
            skel.SetBonePoseScale(b, local.Basis.Scale);
            ctx.Bones[j.Id] = (skel, b, parentWorld);
            ctx.Count++;
            foreach (var c in j.Children)
            {
                if (c.IsJoint) AddBone(c, b);
                else BuildNode(ctx, c, ctx.Root, bakeWorld: true); // 조인트 아래 일반 노드는 월드를 베이크해 루트에
            }
        }
        AddBone(rootJoint, -1);
    }

    private static void BuildNode(Ctx ctx, SceneNode n, Node3D parent, bool bakeWorld)
    {
        if (n.IsJoint) return;
        Node3D g;
        if (n.Mesh != null && n.Skin != null && TryBuildSkinned(ctx, n, out var skinned)) { g = skinned; ctx.Count++; foreach (var c in n.Children) BuildNode(ctx, c, ctx.Root, bakeWorld: true); return; }
        if (n.Mesh != null)
        {
            var mesh = n.Mesh.Clone();
            mesh.Compact();
            MeshNormals.Recompute(mesh);
            var render = MeshTessellator.Build(mesh);
            var arr = new ArrayMesh { ResourceName = n.Name + "Shape" };
            new GodotMeshBridge().UploadSurface(arr, render);
            if (arr.GetSurfaceCount() > 0) arr.SurfaceSetMaterial(0, DefaultMaterial);
            ctx.Tris += render.TriangleCount;
            g = new MeshInstance3D { Mesh = arr };
        }
        else g = new Node3D();
        g.Name = n.Name;
        // 부모가 내보내기 대상이 아니면 월드 트랜스폼을 베이크
        g.Transform = bakeWorld ? n.WorldMatrix.ToGodot() : n.Local.ToGodot();
        parent.AddChild(g);
        ctx.Count++;
        foreach (var c in n.Children) BuildNode(ctx, c, g, false);
    }

    /// <summary>스킨 메시: 모든 조인트가 같은 스켈레톤에 있어야 한다. 아니면 false(일반 메시로 내보냄).</summary>
    private static bool TryBuildSkinned(Ctx ctx, SceneNode n, out MeshInstance3D mi)
    {
        mi = null!;
        var skin = n.Skin!; var src = n.Mesh!;
        Skeleton3D? skel = null; NMat skelWorld = NMat.Identity;
        var boneOf = new int[skin.Joints.Count];
        for (int j = 0; j < skin.Joints.Count; j++)
        {
            if (!ctx.Bones.TryGetValue(skin.Joints[j], out var b)) return false;
            if (skel == null) { skel = b.skel; skelWorld = b.skelWorld; }
            else if (skel != b.skel) return false;
            boneOf[j] = b.bone;
        }
        if (skel == null) return false;
        NMat.Invert(skelWorld, out var skelInv);
        NMat.Invert(n.WorldMatrix, out var meshInv);
        var doc = CubeApp.Instance.Document;

        // 현재 포즈로 변형된 월드 위치 → 스켈레톤 공간
        var deformed = new NVec3[src.VertexCount];
        SkinOps.Deform(src, skin, id => doc.Find(id)?.WorldMatrix, meshInv, deformed);
        var mesh = src.Clone(); // Compact 하지 않아 정점 ID가 스킨과 일치
        var toSkel = n.WorldMatrix * skelInv;
        for (int v = 0; v < mesh.VertexCount; v++)
        {
            if (!mesh.Verts[v].Alive) continue;
            var vert = mesh.Verts[v]; vert.Position = NVec3.Transform(deformed[v], toSkel); mesh.Verts[v] = vert;
        }
        MeshNormals.Recompute(mesh);
        var render = MeshTessellator.Build(mesh);
        var bones4 = new int[render.CornerCount * 4]; var weights4 = new float[render.CornerCount * 4];
        for (int c = 0; c < render.CornerCount; c++)
        {
            int v = mesh.Hes[render.CornerToHalfEdge[c]].Vertex;
            var list = v < skin.Weights.Length ? skin.Weights[v] : null;
            float sum = 0; int k = 0;
            if (list != null)
                foreach (var (j, w) in list.OrderByDescending(x => x.weight).Take(4)) { bones4[c * 4 + k] = boneOf[j]; weights4[c * 4 + k] = w; sum += w; k++; }
            if (sum > 1e-6f) for (int i = 0; i < 4; i++) weights4[c * 4 + i] /= sum;
            else { bones4[c * 4] = boneOf.Length > 0 ? boneOf[0] : 0; weights4[c * 4] = 1f; }
        }
        var arr = new ArrayMesh { ResourceName = n.Name + "Shape" };
        new GodotMeshBridge().UploadSurface(arr, render, null, bones4, weights4);
        if (arr.GetSurfaceCount() > 0) arr.SurfaceSetMaterial(0, DefaultMaterial);
        ctx.Tris += render.TriangleCount;

        // Skin: 본마다 역바인드(스켈레톤 공간) = skelWorld · inv(jointWorld)
        var gskin = new Skin();
        for (int j = 0; j < skin.Joints.Count; j++)
        {
            var jn = doc.Find(skin.Joints[j]);
            if (jn == null) continue;
            NMat.Invert(jn.WorldMatrix, out var jointInv);
            gskin.AddBind(boneOf[j], (skelWorld * jointInv).ToGodot());
            gskin.SetBindName(j, jn.Name);
        }
        mi = new MeshInstance3D { Name = n.Name, Mesh = arr, Skin = gskin };
        skel.AddChild(mi);
        mi.Skeleton = mi.GetPathTo(skel);
        return true;
    }
}
