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
/// <remarks>
/// 좌표·규약: 트랜스폼은 <c>MathConvert.ToGodot</c>로 행벡터(System.Numerics) 행렬을 Godot Transform3D로 바꾼다.
/// 메시는 Compact 후 노멀을 다시 계산해 <see cref="MeshTessellator"/>로 삼각형화하고 <see cref="GodotMeshBridge"/>가
/// 와인딩(CCW→CW)과 UV(v → 1−v)를 Godot 규약으로 바꿔 올린다. glTF 쓰기는 호출자(<see cref="GltfExporter"/>)가 한다.
/// </remarks>
public static class DocumentToGodotScene
{
    /// <summary>머티리얼이 없는 노드에 쓰는 기본 회색 무광 머티리얼(Maya lambert1에 해당). 모든 내보내기가 공유한다.</summary>
    private static readonly StandardMaterial3D DefaultMaterial = new()
    {
        ResourceName = "lambert1",
        AlbedoColor = new Color(0.5f, 0.5f, 0.5f),
        Roughness = 1f,
        Metallic = 0f,
    };

    /// <summary>한 번의 Build 호출 동안 공유되는 상태(만든 스켈레톤·노드·머티리얼, 통계).</summary>
    private sealed class Ctx
    {
        /// <summary>임시 씬 루트(파일 이름이 이름).</summary>
        public readonly Node3D Root;
        /// <summary>조인트 ID → (속한 Skeleton3D, 본 인덱스, 스켈레톤 부모 월드 행렬). 스킨·애니메이션이 본을 찾을 때 쓴다.</summary>
        public readonly Dictionary<NodeId, (Skeleton3D skel, int bone, NMat skelWorld)> Bones = new();
        /// <summary>내보내기 집합(최상위 노드와 모든 자손)의 ID. 루트 조인트 판정에 쓴다.</summary>
        public readonly HashSet<NodeId> InSet = new();
        /// <summary>일반 노드 → 만든 Godot 노드와(월드 베이크 시) 부모 월드 행렬. 애니메이션 트랙 경로/값 변환용.</summary>
        public readonly Dictionary<NodeId, (Node3D node, NMat? parentWorld)> Nodes = new();
        /// <summary>문서 머티리얼 ID → 이번 내보내기의 StandardMaterial3D(같은 머티리얼은 glTF 머티리얼 하나).</summary>
        public readonly Dictionary<int, StandardMaterial3D> Materials = new();
        /// <summary>이번 내보내기에서 실제로 쓰인 문서 머티리얼(만든 순서). <see cref="GltfMaterialExtension"/>에 넘긴다.</summary>
        public readonly List<MaterialDef> UsedMaterials = new();
        /// <summary>만든 Godot 노드 수(본 포함)와 삼각형 수. 결과 메시지용.</summary>
        public int Count, Tris;
        /// <summary>루트를 받아 빈 상태로 시작한다.</summary>
        public Ctx(Node3D root) { Root = root; }
    }

    /// <summary>
    /// 내보낼 노드 목록으로 임시 Godot 씬을 만든다.
    /// 단계: ① 조상이 함께 들어 있는 노드는 빼고 최상위만 남김 → ② 루트 조인트마다 Skeleton3D →
    /// ③ 일반 노드(라이트/스킨 메시/메시/빈 노드)를 재귀로 → ④ 클립이 있으면 AnimationPlayer.
    /// 최상위 노드의 부모가 문서 루트가 아니면 부모가 내보내기에 없으므로 월드 트랜스폼을 베이크한다.
    /// </summary>
    /// <param name="nodes">내보낼 노드(하위 트리 포함).</param>
    /// <param name="rootName">임시 루트 Node3D 이름(glTF 씬 루트 노드 이름이 된다).</param>
    /// <param name="clips">함께 내보낼 애니메이션 클립(없으면 null).</param>
    /// <returns>(호출자가 Free 해야 하는 루트, 만든 노드 수, 삼각형 수).</returns>
    /// <param name="usedMaterials">채워지면 내보낸 메시가 쓰는 문서 머티리얼(<see cref="GltfMaterialExtension"/>이 텍스처·확장을 쓰는 데 쓴다).</param>
    public static (Node3D root, int nodeCount, int triCount) Build(IReadOnlyList<SceneNode> nodes, string rootName, IReadOnlyList<AnimationClip>? clips = null, List<MaterialDef>? usedMaterials = null)
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
        // 내보내기 집합 = 최상위 노드와 그 모든 자손
        foreach (var t in tops) { ctx.InSet.Add(t.Id); foreach (var d in t.Descendants()) ctx.InSet.Add(d.Id); }

        // 1) 스켈레톤: 내보내기 집합 안의 루트 조인트(부모가 조인트가 아님)마다 Skeleton3D
        foreach (var t in tops)
            foreach (var j in new[] { t }.Concat(t.Descendants()))
                if (j.IsJoint && (j.Parent == null || j.Parent.IsRoot || !j.Parent.IsJoint || !ctx.InSet.Contains(j.Parent.Id)))
                    BuildSkeleton(ctx, j);

        // 2) 일반 노드(조인트는 스켈레톤에 들어갔으므로 건너뜀)
        foreach (var t in tops)
        {
            // 최상위가 조인트면 스켈레톤으로 이미 처리되었으므로 그 아래 일반 자식만 월드 베이크로 루트에 붙인다
            if (t.IsJoint) { foreach (var c in t.Children) if (!c.IsJoint) BuildNode(ctx, c, root, bakeWorld: true); continue; }
            BuildNode(ctx, t, root, t.Parent != null && !t.Parent.IsRoot);
        }
        if (clips is { Count: > 0 }) AddAnimations(ctx, clips);
        usedMaterials?.AddRange(ctx.UsedMaterials);
        return (root, ctx.Count, ctx.Tris);
    }

    /// <summary>
    /// <paramref name="rootJoint"/>에서 시작하는 조인트 체인을 Skeleton3D 하나로 만든다.
    /// 스켈레톤 노드의 트랜스폼 = 루트 조인트 부모의 월드(부모가 없으면 단위). 각 본의 rest와 현재 포즈 = 조인트 Local.
    /// 조인트 아래 일반 노드(메시 등)는 스켈레톤 밖 루트에 월드를 베이크해 붙인다(스킨 메시는 TryBuildSkinned가 다시 스켈레톤 아래로).
    /// </summary>
    private static void BuildSkeleton(Ctx ctx, SceneNode rootJoint)
    {
        var skel = new Skeleton3D { Name = rootJoint.Name + "_skeleton" };
        var parentWorld = rootJoint.Parent != null && !rootJoint.Parent.IsRoot ? rootJoint.Parent.WorldMatrix : NMat.Identity;
        skel.Transform = parentWorld.ToGodot();
        ctx.Root.AddChild(skel);
        ctx.Count++;
        // 재귀: 조인트 j를 본으로 추가하고 자식 조인트를 그 아래 본으로 이어 붙인다
        void AddBone(SceneNode j, int parentBone)
        {
            int b = skel.AddBone(j.Name);
            if (parentBone >= 0) skel.SetBoneParent(b, parentBone);
            // 본 rest와 포즈를 모두 조인트 로컬 트랜스폼으로 둔다(바인드 포즈 = 내보내기 시점 포즈)
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

    /// <summary>
    /// MaterialDef → 내보내기용 StandardMaterial3D. 값(색·메탈릭·러프니스·알파 모드·양면·Unlit)만 넣고 텍스처는 넣지 않는다:
    /// 텍스처 패킹과 PBR 확장은 <see cref="GltfMaterialExtension"/>이 glTF JSON의 이 머티리얼 항목을 다시 써서 기록한다(메타 cube_material_id로 짝).
    /// </summary>
    private static Material MaterialFor(Ctx ctx, SceneNode n)
    {
        // 문서 머티리얼을 찾고, 이미 이번 내보내기에서 만들었으면 재사용한다(glTF 머티리얼 하나로 공유)
        var def = CubeApp.Instance.Document.FindMaterial(n.MaterialId);
        if (def == null) return DefaultMaterial;
        if (ctx.Materials.TryGetValue(def.Id, out var cached)) return cached;
        var c = def.Color;
        var m = new StandardMaterial3D { ResourceName = def.Name, AlbedoColor = new Color(c.X, c.Y, c.Z, def.GetF("alpha")) };
        // 머티리얼 종류별 기본 PBR 값: BlinnPhong은 Shininess를 러프니스로 환산, Unlit/Matcap은 unshaded
        switch (def.Type)
        {
            case MaterialType.Pbr: m.Metallic = def.Metallic; m.Roughness = def.Roughness; break;
            case MaterialType.BlinnPhong: m.Metallic = 0; m.Roughness = GltfMaterialExtension.ShininessToRoughness(def.Shininess); break;
            case MaterialType.Unlit: case MaterialType.Matcap: m.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded; m.Roughness = 1; m.Metallic = 0; break;
            default: m.Roughness = 1; m.Metallic = 0; break;
        }
        // 알파 모드: 0 = Opaque, 1 = Mask(알파 컷오프), 2 = Blend
        switch ((int)def.GetF("alphaMode"))
        {
            case 1: m.Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor; m.AlphaScissorThreshold = def.GetF("alphaCutoff"); break;
            case 2: m.Transparency = BaseMaterial3D.TransparencyEnum.Alpha; break;
        }
        if (def.GetF("doubleSided") > 0.5f) m.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
        // 확장이 glTF JSON에서 이 머티리얼을 찾아 다시 쓸 수 있도록 문서 머티리얼 ID를 메타로 단다
        m.SetMeta(GltfMaterialExtension.MetaKey, def.Id);
        ctx.Materials[def.Id] = m;
        ctx.UsedMaterials.Add(def);
        return m;
    }

    /// <summary>
    /// 일반 노드 하나와 그 하위 트리를 Godot 노드로 만든다. 조인트는 건너뛴다(스켈레톤에서 처리).
    /// 라이트 → Light3D, 스킨 메시 → 스켈레톤 아래 MeshInstance3D(실패하면 일반 메시), 메시 → MeshInstance3D, 그 밖 → Node3D.
    /// </summary>
    /// <param name="parent">붙일 Godot 부모.</param>
    /// <param name="bakeWorld">true면 Local 대신 월드 행렬을 트랜스폼으로 쓴다(부모가 내보내기에 없거나 조인트일 때).</param>
    private static void BuildNode(Ctx ctx, SceneNode n, Node3D parent, bool bakeWorld)
    {
        if (n.IsJoint) return;
        Node3D g;
        // 라이트: 종류별 Light3D를 만들고 색·세기·범위를 옮긴다(Spot 각도는 Godot이 반각이라 0.5배)
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
        // 스킨 메시는 스켈레톤 자식으로 붙고 스켈레톤 공간에 베이크되므로, 자식 노드는 월드를 베이크해 루트에 붙인다
        if (n.Mesh != null && n.Skin != null && TryBuildSkinned(ctx, n, out var skinned)) { g = skinned; ctx.Count++; foreach (var c in n.Children) BuildNode(ctx, c, ctx.Root, bakeWorld: true); return; }
        if (n.Mesh != null)
        {
            // 일반 메시: 복제본을 Compact(빈 슬롯 제거) → 노멀 재계산 → 삼각형화 → ArrayMesh에 업로드
            var mesh = n.Mesh.Clone();
            mesh.Compact();
            MeshNormals.Recompute(mesh);
            var render = MeshTessellator.Build(mesh);
            var arr = new ArrayMesh { ResourceName = n.Name + "Shape" };
            new GodotMeshBridge().UploadSurface(arr, render);
            if (arr.GetSurfaceCount() > 0) arr.SurfaceSetMaterial(0, MaterialFor(ctx, n));
            ctx.Tris += render.TriangleCount;
            g = new MeshInstance3D { Mesh = arr };
        }
        else g = new Node3D();
        g.Name = n.Name;
        // 부모가 내보내기 대상이 아니면 월드 트랜스폼을 베이크
        g.Transform = bakeWorld ? n.WorldMatrix.ToGodot() : n.Local.ToGodot();
        parent.AddChild(g);
        // 애니메이션 트랙 경로/값 변환을 위해 만든 노드와 (베이크했으면) 부모 월드를 기록
        ctx.Nodes[n.Id] = (g, bakeWorld ? ParentWorld(n) : null);
        ctx.Count++;
        foreach (var c in n.Children) BuildNode(ctx, c, g, false);
    }

    /// <remarks>
    /// 정점은 현재 포즈로 LBS 변형한 월드 위치를 스켈레톤 공간으로 옮겨 굽는다. 코너마다 상위 가중치 4개를 정규화해
    /// BONES/WEIGHTS로 넣고(합이 0이면 바인드 0에 1.0), Skin 바인드 j = 스킨 조인트 j로 맞춘다.
    /// </remarks>
    /// <param name="mi">성공 시 만든 MeshInstance3D(스켈레톤의 자식으로 이미 붙어 있음).</param>
    /// <summary>스킨 메시: 모든 조인트가 같은 스켈레톤에 있어야 한다. 아니면 false(일반 메시로 내보냄).</summary>
    private static bool TryBuildSkinned(Ctx ctx, SceneNode n, out MeshInstance3D mi)
    {
        mi = null!;
        var skin = n.Skin!; var src = n.Mesh!;
        Skeleton3D? skel = null; NMat skelWorld = NMat.Identity;
        // 스킨 조인트 j → 본 인덱스. 모든 조인트가 이번 내보내기의 같은 스켈레톤에 있어야 한다
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
        // 스켈레톤 공간 변환에 쓸 역행렬들
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
        // 렌더 코너(삼각형 언롤 정점)마다 원래 정점의 가중치에서 상위 4개를 골라 정규화한다
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
        if (arr.GetSurfaceCount() > 0) arr.SurfaceSetMaterial(0, MaterialFor(ctx, n));
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
        // 스켈레톤 자식으로 붙이고 Skeleton 경로를 연결한다
        mi = new MeshInstance3D { Name = n.Name, Mesh = arr, Skin = gskin };
        skel.AddChild(mi);
        mi.Skeleton = mi.GetPathTo(skel);
        return true;
    }

    /// <summary>노드의 부모 월드 행렬(부모가 없거나 문서 루트면 단위). 월드 베이크 노드의 애니메이션 키 변환에 쓴다.</summary>
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
        // 같은 이름의 클립이 여러 개면 _2, _3...을 붙여 라이브러리 키 충돌을 피한다
        var usedNames = new HashSet<string>();
        foreach (var clip in clips)
        {
            // 길이는 최소 한 프레임, Step = 1/fps, 루프 클립은 Linear 루프
            var anim = new Animation { Length = Math.Max(clip.Length, 1f / Math.Max(1f, clip.FrameRate)), Step = 1f / Math.Max(1f, clip.FrameRate), LoopMode = clip.Loop ? Animation.LoopModeEnum.Linear : Animation.LoopModeEnum.None };
            int written = 0;
            foreach (var tr in clip.Tracks)
            {
                var sn = doc.Find(tr.Node);
                if (sn == null || tr.KeyCount == 0) continue;
                // 트랙 경로: 조인트면 "스켈레톤:본", 일반 노드면 루트 기준 노드 경로. 내보내지 않는 노드면 건너뛴다
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
                    // 피벗이나 부모 월드 베이크가 있으면 채널을 따로 쓸 수 없으므로, 모든 키 시간에서 행렬을 평가해 T/R/S로 분해한다
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
            // 트랙이 하나도 안 쓰인 클립은 버린다
            if (written == 0) { anim.Dispose(); continue; }
            string name = SafeAnimName(clip.Name);
            string unique = name; for (int i = 2; usedNames.Contains(unique); i++) unique = name + "_" + i;
            usedNames.Add(unique);
            lib.AddAnimation(unique, anim);
        }
        if (usedNames.Count == 0) return;
        // 루트에 AnimationPlayer를 붙이고 RootNode를 루트로 지정해 트랙 경로가 루트 기준이 되게 한다
        var player = new AnimationPlayer { Name = "AnimationPlayer" };
        ctx.Root.AddChild(player);
        player.AddAnimationLibrary("", lib);
        player.RootNode = player.GetPathTo(ctx.Root);
    }

    /// <summary>지정 종류의 트랙을 추가하고 경로·선형 보간을 설정한 뒤 트랙 인덱스를 돌려준다.</summary>
    private static int Track(Animation anim, Animation.TrackType type, NodePath path)
    {
        int t = anim.AddTrack(type);
        anim.TrackSetPath(t, path);
        anim.TrackSetInterpolationType(t, Animation.InterpolationType.Linear);
        return t;
    }

    /// <summary>System.Numerics 쿼터니언 → Godot 쿼터니언(성분 순서 X, Y, Z, W 동일).</summary>
    private static Godot.Quaternion ToGodot(System.Numerics.Quaternion q) => new(q.X, q.Y, q.Z, q.W);

    /// <summary>Godot 애니메이션 이름에 쓸 수 없는 문자('/', ':', ',', '[')를 _로 바꾼다. 비면 "Take".</summary>
    private static string SafeAnimName(string s)
    {
        var chars = s.Select(c => c is '/' or ':' or ',' or '[' ? '_' : c).ToArray();
        var r = new string(chars).Trim();
        return r.Length == 0 ? "Take" : r;
    }
}
