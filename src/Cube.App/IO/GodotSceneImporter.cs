using Cube.App.Bridge;
using Cube.Core.IO;
using Cube.Core.Mesh;
using Cube.Core.Scene;
using Godot;
using NMat = System.Numerics.Matrix4x4;
using NVec2 = System.Numerics.Vector2;
using NVec3 = System.Numerics.Vector3;

namespace Cube.App.IO;

/// <summary>Godot GltfDocument/FbxDocument가 만든 씬 트리를 Document 노드로 변환한다.</summary>
/// <remarks>
/// 흐름: AppendFromFile → GenerateScene(임시 Godot 씬) → 노드 재귀 변환(<see cref="Convert"/>) →
/// 스킨 조인트 ID·바인드 행렬 해석(<c>ResolveSkins</c>) → 애니메이션 추출(<see cref="AnimationImport"/>) → 임시 씬 해제.
/// 문서는 직접 바꾸지 않고 <see cref="ImportResult"/>(노드·머티리얼·클립)를 돌려주며 FileActions가 Undo 명령으로 넣는다.
/// 메시는 Godot 서피스(삼각형 수프)를 <see cref="TriangleSoupToPolyMesh"/>로 용접·쿼드화해 PolyMesh로 만든다.
/// </remarks>
public abstract class GodotSceneImporterBase : IImporter
{
    /// <summary>형식 이름(다이얼로그 필터 표시용).</summary>
    public abstract string Name { get; }
    /// <summary>처리하는 확장자 목록.</summary>
    public abstract IReadOnlyList<string> Extensions { get; }

    /// <summary>형식별 Godot 문서/상태 객체를 만든다(glTF = GltfDocument, FBX = FbxDocument(ufbx)).</summary>
    protected abstract (GltfDocument doc, GltfState state) CreateDocument();

    /// <summary>파일을 읽어 SceneNode 트리로 변환한다. 단계별 소요 시간을 [ImportPerf]로 출력한다.</summary>
    /// <param name="path">가져올 파일 경로.</param>
    /// <param name="doc">대상 문서(이름 중복 회피·ID 배정·머티리얼 생성에만 사용, 노드는 추가하지 않음).</param>
    /// <param name="options">용접·쿼드 병합 등 변환 옵션.</param>
    public ImportResult Import(string path, Document doc, ImportOptions options)
    {
        Node? scene = null;
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            // 1) 파일 파싱
            var (gltf, state) = CreateDocument();
            var err = gltf.AppendFromFile(path, state);
            long tAppend = sw.ElapsedMilliseconds;
            if (err != Error.Ok) return ImportResult.Fail($"AppendFromFile failed: {err}");
            // 2) Godot 씬 생성(메시·스켈레톤·AnimationPlayer 포함 임시 트리)
            scene = gltf.GenerateScene(state);
            if (scene == null) return ImportResult.Fail("GenerateScene returned null.");
            long tScene = sw.ElapsedMilliseconds;
            var nodes = new List<SceneNode>();
            var ictx = new ImportCtx(doc, options, path);
            // 루트 자체가 메시를 가지면 루트도 노드로, 아니면 루트의 자식들을 최상위로
            if (scene is Node3D rootN3 && HasMesh(rootN3)) nodes.Add(Convert(rootN3, ictx));
            else foreach (var child in scene.GetChildren()) if (child is Node3D c3) nodes.Add(Convert(c3, ictx));
            // 3) 스킨 해석 → 4) 애니메이션 추출
            long tConvert = sw.ElapsedMilliseconds;
            ictx.ResolveSkins(nodes);
            long tSkins = sw.ElapsedMilliseconds;
            var clips = AnimationImport.Extract(scene, ictx.NodeMap, ictx.BoneNodes);
            GD.Print($"[ImportPerf] {System.IO.Path.GetFileName(path)}: append {tAppend} ms, generateScene {tScene - tAppend} ms, convert {tConvert - tScene} ms (meshes {ictx.Meshes}), skins {tSkins - tConvert} ms, animations {sw.ElapsedMilliseconds - tSkins} ms ({clips.Count} clips, {clips.Sum(c => c.KeyCount)} keys)");
            // 결과 메시지: 메시·머티리얼·조인트·스킨·애니메이션 개수
            int mats = ictx.Materials.Created.Count;
            string msg = $"Imported {ictx.Meshes} mesh(es)" + (mats > 0 ? $", {mats} material(s)" : "") + (ictx.Joints > 0 ? $", {ictx.Joints} joint(s), {ictx.Skins} skin(s)" : "")
                + (clips.Count > 0 ? $", {clips.Count} animation(s)" : "") + $" from {System.IO.Path.GetFileName(path)}";
            return new ImportResult(true, msg, nodes) { Animations = clips, Materials = ictx.Materials.Created };
        }
        catch (Exception ex) { return ImportResult.Fail(ex.Message); }
        finally { scene?.Free(); }
    }

    /// <summary>노드가 메시를 가진 MeshInstance3D/ImporterMeshInstance3D인지.</summary>
    private static bool HasMesh(Node n) => n is MeshInstance3D { Mesh: not null } || n is ImporterMeshInstance3D { Mesh: not null };

    /// <summary>한 번의 가져오기 동안 공유되는 상태(이름 중복 회피, 노드 맵, 보류 중인 스킨, 머티리얼, 통계).</summary>
    private sealed class ImportCtx
    {
        /// <summary>대상 문서와 변환 옵션.</summary>
        public readonly Document Doc; public readonly ImportOptions Options;
        /// <summary>만든 메시·조인트·스킨 수(결과 메시지용).</summary>
        public int Meshes, Joints, Skins;
        /// <summary>이번 가져오기에서 이미 배정한 노드 이름.</summary>
        private readonly HashSet<string> _used = new();
        /// <summary>문서 안 이름과 이번 가져오기에서 이미 쓴 이름을 모두 피한다(아직 문서에 없는 노드끼리도 겹치지 않게).</summary>
        public string Unique(string baseName)
        {
            bool Taken(string n) => _used.Contains(n) || Doc.Nodes.Values.Any(x => x.Name == n);
            string name = baseName;
            // 이미 쓰인 이름이면 끝 숫자를 떼어 낸 줄기에 1, 2, 3...을 붙여 빈 이름을 찾는다
            if (Taken(name))
            {
                string stem = name.TrimEnd("0123456789".ToCharArray()); if (stem.Length == 0) stem = name;
                for (int i = 1; ; i++) { string cand = stem + i; if (!Taken(cand)) { name = cand; break; } }
            }
            _used.Add(name);
            return name;
        }
        /// <summary>Skeleton3D → 본 인덱스별 조인트 노드.</summary>
        public readonly Dictionary<Skeleton3D, SceneNode[]> BoneNodes = new();
        /// <summary>Godot 노드 → 만든 SceneNode(애니메이션 트랙 경로 해석용).</summary>
        public readonly Dictionary<Node, SceneNode> NodeMap = new();
        /// <summary>트리 조립 후 조인트 ID·바인드 행렬을 채울 스킨 메시.</summary>
        public readonly List<(SceneNode mesh, SkinCluster skin, List<SceneNode> joints)> Pending = new();
        /// <summary>Godot 머티리얼 → 문서 MaterialDef 변환·중복 제거 담당.</summary>
        public readonly ImportedMaterials Materials;
        /// <summary>문서·옵션·파일 경로(텍스처 상대 경로 기준)로 만든다.</summary>
        public ImportCtx(Document doc, ImportOptions options, string path) { Doc = doc; Options = options; Materials = new ImportedMaterials(doc, path); }

        /// <summary>최상위 노드들에 ID를 먼저 배정한 뒤(문서에 넣어도 유지됨) 스킨의 조인트 ID와 바인드 행렬을 채운다.</summary>
        public void ResolveSkins(IEnumerable<SceneNode> tops)
        {
            foreach (var t in tops) Doc.AssignIds(t);
            foreach (var (mesh, skin, joints) in Pending)
            // 바인드 = 가져온 시점의 rest 포즈: 메시 월드와 조인트 월드 역행렬을 기록
            {
                skin.Joints.Clear(); skin.BindInverse.Clear();
                skin.MeshBindWorld = mesh.WorldMatrix;
                foreach (var jn in joints)
                {
                    skin.Joints.Add(jn.Id);
                    NMat.Invert(jn.WorldMatrix, out var inv);
                    skin.BindInverse.Add(inv);
                }
            }
        }
    }

    /// <summary>
    /// Godot Node3D 하나(와 하위 트리)를 SceneNode로 재귀 변환한다.
    /// Skeleton3D면 본마다 조인트 SceneNode를 만들어 본 계층대로 붙이고(rest = 본 rest),
    /// 메시가 있으면 서피스를 머티리얼별로 묶어 메시 셰이프(필요하면 머티리얼별 자식 노드)로 만든다.
    /// 트랜스폼은 Godot 로컬 Transform을 피벗 0인 <see cref="Transform3"/>으로 분해한다.
    /// </summary>
    private static SceneNode Convert(Node3D g, ImportCtx ctx)
    {
        var doc = ctx.Doc;
        var node = new SceneNode { Name = ctx.Unique(SafeName(g.Name)) };
        node.Local = Transform3.FromMatrix(g.Transform.ToNumerics());
        ctx.NodeMap[g] = node;
        if (g is Skeleton3D skel)
        {
            // 본 → 조인트 노드(부모 본 아래, 루트 본은 스켈레톤 노드 아래)
            int n = skel.GetBoneCount();
            var bones = new SceneNode[n];
            for (int i = 0; i < n; i++)
            {
                bones[i] = new SceneNode { Name = ctx.Unique(SafeName(skel.GetBoneName(i))), Shape = new JointShape(), Local = Transform3.FromMatrix(skel.GetBoneRest(i).ToNumerics()) };
                ctx.Joints++;
            }
            for (int i = 0; i < n; i++)
            {
                int p = skel.GetBoneParent(i);
                if (p >= 0) bones[p].AttachChild(bones[i]); else node.AttachChild(bones[i]);
            }
            ctx.BoneNodes[skel] = bones;
        }
        // 메시 서피스(삼각형 수프)와 스킨 데이터, 서피스별 머티리얼을 모은다
        var surfaces = CollectSurfaces(g, out var skinData, out var surfaceMats);
        if (surfaces.Count > 0)
        {
            Skeleton3D? skelNode = null;
            // 스킨: 메시가 가리키는 스켈레톤이 이번에 변환된 것이어야 가중치를 조인트에 연결할 수 있다
            if (skinData != null && TryFindSkeleton(g, out var sk) && ctx.BoneNodes.ContainsKey(sk))
            {
                skelNode = sk;
                // 메시 BONES 값은 스켈레톤 본 번호가 아니라 Skin 바인드 번호다(Skin이 있을 때). 바인드 → 본으로 바꾼다
                if (skinData.Skin is { } gs && gs.GetBindCount() > 0)
                {
                    var map = new int[gs.GetBindCount()];
                    for (int b = 0; b < map.Length; b++)
                    {
                        int bone = gs.GetBindBone(b);
                        if (bone < 0) bone = sk.FindBone(gs.GetBindName(b));
                        map[b] = bone;
                    }
                    skinData.BindToBone = map;
                    BakeBindToRest(g, sk, skinData, surfaces);
                }
                // 스킨 영향이 다른 정점이 용접되지 않게 태그를 단다
                SetWeldTags(skinData, surfaces);
            }
            // 머티리얼은 오브젝트 단위라 서피스를 머티리얼별로 묶어 하나면 이 노드에, 여럿이면 머티리얼별 자식 메시로 나눈다
            var groups = new List<(int mat, List<int> surfaces)>();
            for (int i = 0; i < surfaces.Count; i++)
            {
                int mat = ctx.Materials.IdFor(surfaceMats[i]);
                int gi = groups.FindIndex(x => x.mat == mat);
                if (gi < 0) { groups.Add((mat, new List<int>())); gi = groups.Count - 1; }
                groups[gi].surfaces.Add(i);
            }
            if (groups.Count == 1) BuildMeshShape(node, groups[0].mat, groups[0].surfaces, surfaces, skinData, skelNode, ctx);
            else
                foreach (var (mat, idxs) in groups)
                {
                    var part = new SceneNode { Name = ctx.Unique(node.Name + "_" + SafeName(ctx.Materials.NameOf(mat))) };
                    BuildMeshShape(part, mat, idxs, surfaces, skinData, skelNode, ctx);
                    node.AttachChild(part);
                }
        }
        // 자식 노드를 재귀로 변환해 붙인다
        foreach (var child in g.GetChildren())
            if (child is Node3D c3)
            {
                var cn = Convert(c3, ctx);
                node.AttachChild(cn);
            }
        return node;
    }

    /// <summary>서피스 묶음 하나를 PolyMesh로 바꿔 target에 메시·머티리얼·스킨을 붙인다.</summary>
    /// <param name="target">메시 셰이프를 붙일 노드.</param>
    /// <param name="materialId">문서 머티리얼 ID(0 = lambert1).</param>
    /// <param name="idxs">이 묶음에 속한 서피스 인덱스(<paramref name="all"/> 기준).</param>
    /// <param name="all">노드의 전체 서피스 목록.</param>
    private static void BuildMeshShape(SceneNode target, int materialId, List<int> idxs, List<TriangleSoupToPolyMesh.Surface> all, SkinData? skinData, Skeleton3D? skelNode, ImportCtx ctx)
    {
        var subset = idxs.Select(i => all[i]).ToList();
        foreach (var su in subset) su.Material = 0; // 면 머티리얼 번호는 쓰지 않는다(머티리얼은 오브젝트 단위)
        var swm = System.Diagnostics.Stopwatch.StartNew();
        // 삼각형 수프 → 폴리 메시(용접·하드 엣지 추론·쿼드 병합). vsrc = 폴리 정점별 원본 (서피스, 수프 정점)
        var mesh = TriangleSoupToPolyMesh.Convert(subset, ctx.Options, out var st, out _, out var vsrc);
        if (swm.ElapsedMilliseconds > 200) GD.Print($"[ImportPerf]   mesh {target.Name}: {subset.Sum(x => x.Indices.Length) / 3} tris → {swm.ElapsedMilliseconds} ms (merged quads {st.MergedQuads})");
        var shape = new MeshShape(mesh);
        target.Shape = shape;
        target.MaterialId = materialId;
        ctx.Meshes++;
        // 스킨이 있으면 이 묶음의 서피스별 가중치로 SkinCluster를 만들고 조인트 ID 해석을 보류 목록에 올린다
        if (skinData != null && skelNode != null && ctx.BoneNodes.TryGetValue(skelNode, out var boneNodes))
        {
            var per = idxs.Select(i => i < skinData.PerSurface.Count ? skinData.PerSurface[i] : (Array.Empty<int>(), Array.Empty<float>(), 0)).ToList();
            var skin = BuildSkin(skinData, per, boneNodes, vsrc, mesh, out var jointNodes);
            if (skin != null) { shape.Skin = skin; ctx.Pending.Add((target, skin, jointNodes)); ctx.Skins++; }
        }
    }

    /// <summary>스킨 영향(본, 가중치)이 다른 정점은 위치가 같아도 용접하지 않도록 태그를 단다(맞닿은 다른 부품이 한 정점이 되면 한쪽 가중치를 잃어 재생 때 고정됨).</summary>
    /// <remarks>
    /// 태그 = (본, 가중치×1000 반올림) 쌍을 정렬해 FNV-1a 64비트 해시한 값(최하위 비트를 1로 해 0과 구분).
    /// TriangleSoupToPolyMesh는 위치와 태그가 모두 같은 정점만 합친다.
    /// </remarks>
    private static void SetWeldTags(SkinData sd, List<TriangleSoupToPolyMesh.Surface> surfaces)
    {
        var pairs = new List<(int bone, int w)>();
        for (int s = 0; s < surfaces.Count && s < sd.PerSurface.Count; s++)
        {
            var (bones, weights, stride) = sd.PerSurface[s];
            if (stride <= 0) continue;
            var surf = surfaces[s];
            var tags = new long[surf.Positions.Length];
            for (int v = 0; v < tags.Length && (v + 1) * stride <= bones.Length; v++)
            {
                pairs.Clear();
                for (int k = 0; k < stride; k++)
                {
                    int w = (int)MathF.Round(weights[v * stride + k] * 1000f);
                    if (w > 0) pairs.Add((bones[v * stride + k], w));
                }
                pairs.Sort();
                ulong h = 1469598103934665603UL;
                foreach (var (b, w) in pairs) { h = (h ^ (uint)b) * 1099511628211UL; h = (h ^ (uint)w) * 1099511628211UL; }
                tags[v] = (long)(h | 1); // 0은 '태그 없음'
            }
            surf.WeldTag = tags;
        }
    }

    /// <summary>
    /// 코어 스킨은 바인드 = 가져온 시점의 조인트 rest 포즈다(ResolveSkins). glTF 역바인드 행렬이 rest 포즈와 다른 파일(BrainStem 등)은
    /// 메시 정점이 바인드 공간에 있어 그대로 두면 rest에서 메시와 스켈레톤이 어긋난다 → Godot이 rest에서 그리는 것과 같게
    /// 삼각형 수프 정점·노멀을 LBS로 rest 포즈에 굽는다(v' = Σ w · inv(메시)·스켈레톤·본 전역 rest·바인드 포즈 · v).
    /// 용접 전에 구워야 바인드 공간에서 우연히 겹친 다른 부품의 정점이 합쳐지지 않는다.
    /// </summary>
    /// <param name="meshNode">스킨 메시 노드(정점이 이 노드 공간에 있음).</param>
    /// <param name="skel">메시가 가리키는 스켈레톤.</param>
    private static void BakeBindToRest(Node3D meshNode, Skeleton3D skel, SkinData sd, List<TriangleSoupToPolyMesh.Surface> surfaces)
    {
        if (sd.Skin is not { } gs || sd.BindToBone == null) return;
        // rel = 메시 공간 기준 스켈레톤 트랜스폼. 바인드마다 메시 공간 → 본 rest 위치로 옮기는 행렬을 미리 계산
        var rel = RootRelative(meshNode).AffineInverse() * RootRelative(skel);
        var mats = new Transform3D[sd.BindToBone.Length];
        bool differs = false;
        for (int i = 0; i < mats.Length; i++)
        {
            int bone = sd.BindToBone[i];
            mats[i] = bone >= 0 && bone < skel.GetBoneCount() ? rel * skel.GetBoneGlobalRest(bone) * gs.GetBindPose(i) : Transform3D.Identity;
            if (!mats[i].IsEqualApprox(Transform3D.Identity)) differs = true;
        }
        // 모든 행렬이 단위면 이미 rest 공간이므로 아무것도 하지 않는다
        if (!differs) return;
        for (int s = 0; s < surfaces.Count && s < sd.PerSurface.Count; s++)
        {
            var (bones, weights, stride) = sd.PerSurface[s];
            if (stride <= 0) continue;
            var surf = surfaces[s];
            int n = Math.Min(surf.Positions.Length, bones.Length / stride);
            // 정점마다 가중 평균으로 위치·노멀을 변환(가중치 합으로 정규화)
            for (int v = 0; v < n; v++)
            {
                var p = surf.Positions[v].ToGodot();
                var nr = surf.Normals != null && v < surf.Normals.Length ? surf.Normals[v].ToGodot() : Vector3.Zero;
                Vector3 accP = Vector3.Zero, accN = Vector3.Zero; float sum = 0;
                for (int k = 0; k < stride; k++)
                {
                    float w = weights[v * stride + k]; int b = bones[v * stride + k];
                    if (w <= 0f || b < 0 || b >= mats.Length) continue;
                    accP += mats[b] * p * w; accN += mats[b].Basis * nr * w; sum += w;
                }
                if (sum <= 1e-12f) continue;
                surf.Positions[v] = (accP / sum).ToNumerics();
                if (surf.Normals != null && v < surf.Normals.Length && accN.LengthSquared() > 1e-20f) surf.Normals[v] = accN.Normalized().ToNumerics();
            }
        }
    }

    /// <summary>트리에 들어가지 않은 가져오기 씬에서 노드의 루트 기준 트랜스폼(GlobalTransform 대용).</summary>
    private static Transform3D RootRelative(Node3D n)
    {
        var t = n.Transform;
        // 부모를 따라 올라가며 로컬 트랜스폼을 왼쪽에 곱한다
        for (var p = n.GetParent() as Node3D; p != null; p = p.GetParent() as Node3D) t = p.Transform * t;
        return t;
    }

    /// <summary>
    /// 메시 노드가 쓰는 Skeleton3D를 찾는다. MeshInstance3D.Skeleton / ImporterMeshInstance3D.SkeletonPath 경로를 먼저,
    /// 없으면 부모 노드를 본다(Godot glTF 임포터는 스킨 메시를 스켈레톤 자식으로 둔다).
    /// </summary>
    private static bool TryFindSkeleton(Node3D g, out Skeleton3D skel)
    {
        skel = null!;
        NodePath? path = g switch { MeshInstance3D mi => mi.Skeleton, ImporterMeshInstance3D imi => imi.SkeletonPath, _ => null };
        Node? found = null;
        if (path != null && !path.IsEmpty) found = g.GetNodeOrNull(path);
        found ??= g.GetParent();
        if (found is Skeleton3D s) { skel = s; return true; }
        return false;
    }

    /// <summary>메시 하나의 스킨 원자료: Godot Skin(바인드)과 서피스별 BONES/WEIGHTS 배열.</summary>
    private sealed class SkinData
    {
        /// <summary>메시의 Godot Skin(바인드 목록, 역바인드 행렬). 없을 수 있다.</summary>
        public Skin? Skin;
        /// <summary>바인드 번호 → 스켈레톤 본 번호(없으면 BONES 값을 본 번호로 본다).</summary>
        public int[]? BindToBone;
        /// <summary>서피스별 (BONES, WEIGHTS, 정점당 영향 수 stride). 스킨 데이터가 없는 서피스는 빈 배열과 stride 0.</summary>
        public List<(int[] bones, float[] weights, int stride)> PerSurface = new();
    }

    /// <summary>
    /// 폴리 메시 정점마다 원본 수프 정점의 가중치를 옮겨 <see cref="SkinCluster"/>를 만든다.
    /// BONES 값은 바인드 번호 → 본 번호 → 조인트 노드 순으로 바꾸고, 처음 등장한 본 순서로 스킨 조인트 인덱스를 매긴다.
    /// 가중치가 하나도 없으면 null. 마지막에 정점별 가중치를 정규화한다.
    /// </summary>
    /// <param name="perSurface">이 메시 묶음의 서피스별 BONES/WEIGHTS(서피스 순서 = TriangleSoupToPolyMesh 입력 순서).</param>
    /// <param name="boneNodes">스켈레톤 본 인덱스 → 조인트 SceneNode.</param>
    /// <param name="vsrc">폴리 정점 → (서피스, 수프 정점 인덱스).</param>
    /// <param name="jointNodes">스킨 조인트 인덱스 순서의 조인트 노드(ID는 아직 없음; ResolveSkins가 채움).</param>
    private static SkinCluster? BuildSkin(SkinData sd, List<(int[] bones, float[] weights, int stride)> perSurface, SceneNode[] boneNodes, (int Surface, int Index)[] vsrc, PolyMesh mesh, out List<SceneNode> jointNodes)
    {
        // 본 인덱스 → 스킨 조인트 인덱스(조인트 ID는 트리 조립 후 ResolveSkins에서 채운다)
        var skin = new SkinCluster();
        var joints = new List<SceneNode>();
        jointNodes = joints;
        var jointIndexOfBone = new Dictionary<int, int>();
        // BONES 값 → 스킨 조인트 인덱스(유효하지 않은 본이면 -1). 새 본이면 조인트 목록에 추가
        int Joint(int bone)
        {
            if (sd.BindToBone != null) bone = bone >= 0 && bone < sd.BindToBone.Length ? sd.BindToBone[bone] : -1;
            if (bone < 0 || bone >= boneNodes.Length) return -1;
            if (!jointIndexOfBone.TryGetValue(bone, out int j)) { j = joints.Count; joints.Add(boneNodes[bone]); jointIndexOfBone[bone] = j; }
            return j;
        }
        skin.EnsureSize(mesh.VertexCount);
        bool any = false;
        // 폴리 정점마다 그 정점을 만든 수프 정점의 가중치(비매니폴드 복제 정점도 빠짐없이)
        for (int v = 0; v < vsrc.Length && v < mesh.VertexCount; v++)
        {
            var (s, i) = vsrc[v];
            if (s < 0 || s >= perSurface.Count) continue;
            var (bones, weights, stride) = perSurface[s];
            if (stride <= 0 || (i + 1) * stride > bones.Length) continue;
            var list = new List<(int, float)>();
            for (int k = 0; k < stride; k++)
            {
                float w = weights[i * stride + k];
                if (w <= 0f) continue;
                int j = Joint(bones[i * stride + k]);
                if (j >= 0) list.Add((j, w));
            }
            if (list.Count > 0) { skin.Weights[v] = list; any = true; }
        }
        if (!any) return null;
        Core.Rig.SkinOps.NormalizeAll(mesh, skin);
        return skin;
    }

    /// <summary>빈 이름을 "node"로 바꾼다.</summary>
    private static string SafeName(string s) => string.IsNullOrWhiteSpace(s) ? "node" : s;

    /// <summary>
    /// 노드의 메시 서피스를 삼각형 수프로 모은다(삼각형 프리미티브만). 위치·노멀·UV·인덱스를 코어 규약으로 바꾸고
    /// BONES/WEIGHTS가 있으면 <paramref name="skinData"/>에, 서피스 머티리얼(오버라이드 우선)은 <paramref name="materials"/>에 담는다.
    /// </summary>
    private static List<TriangleSoupToPolyMesh.Surface> CollectSurfaces(Node3D g, out SkinData? skinData, out List<Material?> materials)
    {
        skinData = null;
        materials = new List<Material?>();
        var list = new List<TriangleSoupToPolyMesh.Surface>();
        // ImporterMeshInstance3D(가져오기 직후 형태)는 ArrayMesh로 바꿔서 읽는다
        Mesh? mesh = g switch
        {
            MeshInstance3D mi => mi.Mesh,
            ImporterMeshInstance3D imi => imi.Mesh?.GetMesh(),
            _ => null,
        };
        if (mesh == null) return list;
        Skin? gskin = g switch { MeshInstance3D mi => mi.Skin, ImporterMeshInstance3D imi => imi.Skin, _ => null };
        for (int s = 0; s < mesh.GetSurfaceCount(); s++)
        {
            if (mesh is ArrayMesh am && am.SurfaceGetPrimitiveType(s) != Mesh.PrimitiveType.Triangles) continue;
            var arrays = mesh.SurfaceGetArrays(s);
            var pos = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            var nrmV = arrays[(int)Mesh.ArrayType.Normal];
            var uvV = arrays[(int)Mesh.ArrayType.TexUV];
            var idxV = arrays[(int)Mesh.ArrayType.Index];
            var nrm = nrmV.VariantType != Variant.Type.Nil ? nrmV.AsVector3Array() : null;
            var uv = uvV.VariantType != Variant.Type.Nil ? uvV.AsVector2Array() : null;
            // 스킨 서피스: stride = 정점당 영향 수(보통 4 또는 8). 앞선 비스킨 서피스 자리는 빈 항목으로 채워 인덱스를 맞춘다
            var bonesV = arrays[(int)Mesh.ArrayType.Bones]; var weightsV = arrays[(int)Mesh.ArrayType.Weights];
            if (bonesV.VariantType != Variant.Type.Nil && weightsV.VariantType != Variant.Type.Nil)
            {
                var bones = bonesV.AsInt32Array(); var weights = weightsV.AsFloat32Array();
                int stride = pos.Length > 0 ? bones.Length / pos.Length : 0;
                skinData ??= new SkinData { Skin = gskin };
                while (skinData.PerSurface.Count < list.Count) skinData.PerSurface.Add((Array.Empty<int>(), Array.Empty<float>(), 0));
                skinData.PerSurface.Add((bones, weights, stride));
            }
            else if (skinData != null) skinData.PerSurface.Add((Array.Empty<int>(), Array.Empty<float>(), 0));
            // 인덱스가 없으면(비인덱스 메시) 0..n-1
            int[] idx = idxV.VariantType != Variant.Type.Nil ? idxV.AsInt32Array() : Enumerable.Range(0, pos.Length).ToArray();
            // Godot(CW 앞면) → 코어(CCW): 삼각형마다 1,2 교환. UV v 뒤집기(상단 원점 → 하단 원점)
            var indices = new int[idx.Length];
            for (int t = 0; t + 2 < idx.Length; t += 3) { indices[t] = idx[t]; indices[t + 1] = idx[t + 2]; indices[t + 2] = idx[t + 1]; }
            list.Add(new TriangleSoupToPolyMesh.Surface
            {
                Positions = pos.Select(p => p.ToNumerics()).ToArray(),
                Normals = nrm?.Select(n => n.ToNumerics()).ToArray(),
                Uvs = uv?.Select(u => new NVec2(u.X, 1f - u.Y)).ToArray(),
                Indices = indices,
                Material = s,
            });
            // 서피스 머티리얼: 인스턴스 오버라이드가 있으면 우선
            materials.Add((g as MeshInstance3D)?.GetSurfaceOverrideMaterial(s) ?? mesh.SurfaceGetMaterial(s));
        }
        return list;
    }
}

/// <summary>glTF 2.0(.glb/.gltf) 가져오기. Godot 내장 GltfDocument 사용.</summary>
public sealed class GltfImporter : GodotSceneImporterBase
{
    public override string Name => "glTF 2.0";
    public override IReadOnlyList<string> Extensions { get; } = new[] { ".glb", ".gltf" };
    protected override (GltfDocument, GltfState) CreateDocument() => (new GltfDocument(), new GltfState());
}

/// <summary>FBX 가져오기. Godot 내장 FbxDocument(ufbx 기반) 사용. 내보내기는 자체 FBX writer(Cube.Core.IO.Fbx)가 따로 한다.</summary>
public sealed class FbxImporter : GodotSceneImporterBase
{
    public override string Name => "FBX";
    public override IReadOnlyList<string> Extensions { get; } = new[] { ".fbx" };
    protected override (GltfDocument, GltfState) CreateDocument() => (new FbxDocument(), new FbxState());
}
