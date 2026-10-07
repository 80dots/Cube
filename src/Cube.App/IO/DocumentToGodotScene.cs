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
        /// <summary>일반 노드 → 만든 Godot 노드와(월드 베이크 시) 부모 월드 행렬. 애니메이션 트랙 경로/값 변환용.</summary>
        public readonly Dictionary<NodeId, (Node3D node, NMat? parentWorld)> Nodes = new();
        public int Count, Tris;
        public Ctx(Node3D root) { Root = root; }
    }

    public static (Node3D root, int nodeCount, int triCount) Build(IReadOnlyList<SceneNode> nodes, string rootName, IReadOnlyList<AnimationClip>? clips = null)
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
        if (clips is { Count: > 0 }) AddAnimations(ctx, clips);
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

    private static readonly Dictionary<int, StandardMaterial3D> MaterialCacheExport = new();

    /// <summary>MaterialDef → 내보내기용 StandardMaterial3D(glTF PBR로 기록됨).</summary>
    private static Material MaterialFor(SceneNode n)
    {
        var def = CubeApp.Instance.Document.FindMaterial(n.MaterialId);
        if (def == null) return DefaultMaterial;
        var m = new StandardMaterial3D { ResourceName = def.Name, AlbedoColor = new Color(def.Color.X, def.Color.Y, def.Color.Z) };
        var tex = Viewport.MaterialCache.LoadTexture(def.TexturePath);
        if (tex != null) { m.AlbedoTexture = tex; m.AlbedoColor = Colors.White; }
        switch (def.Type)
        {
            case MaterialType.Pbr: m.Metallic = def.Metallic; m.Roughness = def.Roughness; break;
            case MaterialType.BlinnPhong: m.Metallic = 0; m.Roughness = Math.Clamp(MathF.Sqrt(2f / (def.Shininess + 2f)), 0.05f, 1f); break;
            case MaterialType.Unlit: case MaterialType.Matcap: m.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded; m.Roughness = 1; break;
            default: m.Roughness = 1; m.Metallic = 0; break;
        }
        return m;
    }

    private static void BuildNode(Ctx ctx, SceneNode n, Node3D parent, bool bakeWorld)
    {
        if (n.IsJoint) return;
        Node3D g;
        if (n.Light is { } light)
        {
            Light3D gl = light.Type switch { LightType.Directional => new DirectionalLight3D(), LightType.Spot => new SpotLight3D { SpotRange = light.Range, SpotAngle = light.SpotAngle * 0.5f }, _ => new OmniLight3D { OmniRange = light.Range } };
            gl.LightColor = new Color(light.Color.X, light.Color.Y, light.Color.Z); gl.LightEnergy = light.Intensity;
            gl.Name = n.Name;
            gl.Transform = bakeWorld ? n.WorldMatrix.ToGodot() : n.Local.ToGodot();
            parent.AddChild(gl);
            ctx.Nodes[n.Id] = (gl, bakeWorld ? ParentWorld(n) : null);
            ctx.Count++;
            foreach (var c in n.Children) BuildNode(ctx, c, gl, false);
            return;
        }
        if (n.Mesh != null && n.Skin != null && TryBuildSkinned(ctx, n, out var skinned)) { g = skinned; ctx.Count++; foreach (var c in n.Children) BuildNode(ctx, c, ctx.Root, bakeWorld: true); return; }
        if (n.Mesh != null)
        {
            var mesh = n.Mesh.Clone();
            mesh.Compact();
            MeshNormals.Recompute(mesh);
            var render = MeshTessellator.Build(mesh);
            var arr = new ArrayMesh { ResourceName = n.Name + "Shape" };
            new GodotMeshBridge().UploadSurface(arr, render);
            if (arr.GetSurfaceCount() > 0) arr.SurfaceSetMaterial(0, MaterialFor(n));
            ctx.Tris += render.TriangleCount;
            g = new MeshInstance3D { Mesh = arr };
        }
        else g = new Node3D();
        g.Name = n.Name;
        // 부모가 내보내기 대상이 아니면 월드 트랜스폼을 베이크
        g.Transform = bakeWorld ? n.WorldMatrix.ToGodot() : n.Local.ToGodot();
        parent.AddChild(g);
        ctx.Nodes[n.Id] = (g, bakeWorld ? ParentWorld(n) : null);
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
                foreach (var (j, w) in list.OrderByDescending(x => x.weight).Take(4)) { bones4[c * 4 + k] = j; weights4[c * 4 + k] = w; sum += w; k++; }
            if (sum > 1e-6f) for (int i = 0; i < 4; i++) weights4[c * 4 + i] /= sum;
            else { bones4[c * 4] = 0; weights4[c * 4] = 1f; }
        }
        var arr = new ArrayMesh { ResourceName = n.Name + "Shape" };
        new GodotMeshBridge().UploadSurface(arr, render, null, bones4, weights4);
        if (arr.GetSurfaceCount() > 0) arr.SurfaceSetMaterial(0, MaterialFor(n));
        ctx.Tris += render.TriangleCount;

        // Skin: 스킨 조인트 j = 바인드 j(메시 BONES 값은 바인드 번호), 역바인드(스켈레톤 공간) = skelWorld · inv(jointWorld)
        var gskin = new Skin();
        for (int j = 0; j < skin.Joints.Count; j++)
        {
            var jn = doc.Find(skin.Joints[j]);
            var jointInv = NMat.Identity;
            if (jn != null) NMat.Invert(jn.WorldMatrix, out jointInv);
            gskin.AddBind(boneOf[j], (skelWorld * jointInv).ToGodot()); // 바인드 번호가 j와 같도록 항상 추가
            gskin.SetBindName(j, jn?.Name ?? skel.GetBoneName(boneOf[j]));
        }
        // 가중치가 없는 본도 바인드로 넣는다: Godot glTF 내보내기는 Skin에 없는 본을 버리는데, 그 본의 트랜스폼(루트 본 회전 등)과 애니메이션이 함께 사라진다
        var bound = new HashSet<int>(boneOf);
        foreach (var (jid, info) in ctx.Bones)
        {
            if (info.skel != skel || bound.Contains(info.bone)) continue;
            var jn = doc.Find(jid); if (jn == null) continue;
            NMat.Invert(jn.WorldMatrix, out var inv2);
            int bi = gskin.GetBindCount();
            gskin.AddBind(info.bone, (skelWorld * inv2).ToGodot());
            gskin.SetBindName(bi, jn.Name);
            bound.Add(info.bone);
        }
        mi = new MeshInstance3D { Name = n.Name, Mesh = arr, Skin = gskin };
        skel.AddChild(mi);
        mi.Skeleton = mi.GetPathTo(skel);
        return true;
    }

    private static NMat ParentWorld(SceneNode n) => n.Parent != null && !n.Parent.IsRoot ? n.Parent.WorldMatrix : NMat.Identity;

    // ---------------------------------------------------------------- 애니메이션

    /// <summary>
    /// 클립마다 Godot Animation(position/rotation/scale 3D 트랙)을 만들어 루트의 AnimationPlayer에 넣는다. GltfDocument가 glTF 애니메이션으로 기록한다.
    /// 조인트는 "스켈레톤:본" 경로(본 로컬), 일반 노드는 노드 경로(월드를 베이크한 노드는 부모 rest 월드를 곱한 값).
    /// 피벗이 없고 베이크하지 않는 노드는 채널별 원래 키를 그대로, 그 밖에는 모든 키 시간에서 행렬을 분해해 세 채널을 쓴다.
    /// </summary>
    private static void AddAnimations(Ctx ctx, IReadOnlyList<AnimationClip> clips)
    {
        var doc = CubeApp.Instance.Document;
        var lib = new AnimationLibrary();
        var usedNames = new HashSet<string>();
        foreach (var clip in clips)
        {
            var anim = new Animation { Length = Math.Max(clip.Length, 1f / Math.Max(1f, clip.FrameRate)), Step = 1f / Math.Max(1f, clip.FrameRate), LoopMode = clip.Loop ? Animation.LoopModeEnum.Linear : Animation.LoopModeEnum.None };
            int written = 0;
            foreach (var tr in clip.Tracks)
            {
                var sn = doc.Find(tr.Node);
                if (sn == null || tr.KeyCount == 0) continue;
                string path; NMat? parentWorld = null;
                if (ctx.Bones.TryGetValue(tr.Node, out var b)) path = ctx.Root.GetPathTo(b.skel) + ":" + b.skel.GetBoneName(b.bone);
                else if (ctx.Nodes.TryGetValue(tr.Node, out var gn)) { path = ctx.Root.GetPathTo(gn.node); parentWorld = gn.parentWorld; }
                else continue;
                var rest = sn.Local;
                if (parentWorld == null && rest.Pivot == NVec3.Zero)
                {
                    // 원래 키 그대로
                    if (tr.Position.Count > 0) { int t = Track(anim, Animation.TrackType.Position3D, path); foreach (var k in tr.Position) anim.PositionTrackInsertKey(t, k.Time, k.Value.ToGodot()); }
                    if (tr.Rotation.Count > 0) { int t = Track(anim, Animation.TrackType.Rotation3D, path); foreach (var k in tr.Rotation) anim.RotationTrackInsertKey(t, k.Time, ToGodot(k.Value)); }
                    if (tr.Scale.Count > 0) { int t = Track(anim, Animation.TrackType.Scale3D, path); foreach (var k in tr.Scale) anim.ScaleTrackInsertKey(t, k.Time, k.Value.ToGodot()); }
                }
                else
                {
                    var times = tr.KeyTimes.Distinct().OrderBy(x => x).ToList();
                    int tp = Track(anim, Animation.TrackType.Position3D, path), trr = Track(anim, Animation.TrackType.Rotation3D, path), ts = Track(anim, Animation.TrackType.Scale3D, path);
                    foreach (var time in times)
                    {
                        var m = tr.Evaluate(time, rest).ToMatrix();
                        if (parentWorld is { } pw) m *= pw;
                        NMat.Decompose(m, out var sc, out var q, out var p);
                        anim.PositionTrackInsertKey(tp, time, p.ToGodot());
                        anim.RotationTrackInsertKey(trr, time, ToGodot(System.Numerics.Quaternion.Normalize(q)));
                        anim.ScaleTrackInsertKey(ts, time, sc.ToGodot());
                    }
                }
                written++;
            }
            if (written == 0) { anim.Dispose(); continue; }
            string name = SafeAnimName(clip.Name);
            string unique = name; for (int i = 2; usedNames.Contains(unique); i++) unique = name + "_" + i;
            usedNames.Add(unique);
            lib.AddAnimation(unique, anim);
        }
        if (usedNames.Count == 0) return;
        var player = new AnimationPlayer { Name = "AnimationPlayer" };
        ctx.Root.AddChild(player);
        player.AddAnimationLibrary("", lib);
        player.RootNode = player.GetPathTo(ctx.Root);
    }

    private static int Track(Animation anim, Animation.TrackType type, NodePath path)
    {
        int t = anim.AddTrack(type);
        anim.TrackSetPath(t, path);
        anim.TrackSetInterpolationType(t, Animation.InterpolationType.Linear);
        return t;
    }

    private static Godot.Quaternion ToGodot(System.Numerics.Quaternion q) => new(q.X, q.Y, q.Z, q.W);

    private static string SafeAnimName(string s)
    {
        var chars = s.Select(c => c is '/' or ':' or ',' or '[' ? '_' : c).ToArray();
        var r = new string(chars).Trim();
        return r.Length == 0 ? "Take" : r;
    }
}
