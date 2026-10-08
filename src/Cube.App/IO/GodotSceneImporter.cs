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
public abstract class GodotSceneImporterBase : IImporter
{
    public abstract string Name { get; }
    public abstract IReadOnlyList<string> Extensions { get; }

    protected abstract (GltfDocument doc, GltfState state) CreateDocument();

    public ImportResult Import(string path, Document doc, ImportOptions options)
    {
        Node? scene = null;
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var (gltf, state) = CreateDocument();
            var err = gltf.AppendFromFile(path, state);
            long tAppend = sw.ElapsedMilliseconds;
            if (err != Error.Ok) return ImportResult.Fail($"AppendFromFile failed: {err}");
            scene = gltf.GenerateScene(state);
            if (scene == null) return ImportResult.Fail("GenerateScene returned null.");
            long tScene = sw.ElapsedMilliseconds;
            var nodes = new List<SceneNode>();
            var ictx = new ImportCtx(doc, options);
            // 루트 자체가 메시를 가지면 루트도 노드로, 아니면 루트의 자식들을 최상위로
            if (scene is Node3D rootN3 && HasMesh(rootN3)) nodes.Add(Convert(rootN3, ictx));
            else foreach (var child in scene.GetChildren()) if (child is Node3D c3) nodes.Add(Convert(c3, ictx));
            long tConvert = sw.ElapsedMilliseconds;
            ictx.ResolveSkins(nodes);
            long tSkins = sw.ElapsedMilliseconds;
            var clips = AnimationImport.Extract(scene, ictx.NodeMap, ictx.BoneNodes);
            GD.Print($"[ImportPerf] {System.IO.Path.GetFileName(path)}: append {tAppend} ms, generateScene {tScene - tAppend} ms, convert {tConvert - tScene} ms (meshes {ictx.Meshes}), skins {tSkins - tConvert} ms, animations {sw.ElapsedMilliseconds - tSkins} ms ({clips.Count} clips, {clips.Sum(c => c.KeyCount)} keys)");
            string msg = $"Imported {ictx.Meshes} mesh(es)" + (ictx.Joints > 0 ? $", {ictx.Joints} joint(s), {ictx.Skins} skin(s)" : "")
                + (clips.Count > 0 ? $", {clips.Count} animation(s)" : "") + $" from {System.IO.Path.GetFileName(path)}";
            return new ImportResult(true, msg, nodes) { Animations = clips };
        }
        catch (Exception ex) { return ImportResult.Fail(ex.Message); }
        finally { scene?.Free(); }
    }

    private static bool HasMesh(Node n) => n is MeshInstance3D { Mesh: not null } || n is ImporterMeshInstance3D { Mesh: not null };

    private sealed class ImportCtx
    {
        public readonly Document Doc; public readonly ImportOptions Options;
        public int Meshes, Joints, Skins;
        private readonly HashSet<string> _used = new();
        /// <summary>문서 안 이름과 이번 가져오기에서 이미 쓴 이름을 모두 피한다(아직 문서에 없는 노드끼리도 겹치지 않게).</summary>
        public string Unique(string baseName)
        {
            bool Taken(string n) => _used.Contains(n) || Doc.Nodes.Values.Any(x => x.Name == n);
            string name = baseName;
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
        public ImportCtx(Document doc, ImportOptions options) { Doc = doc; Options = options; }

        /// <summary>최상위 노드들에 ID를 먼저 배정한 뒤(문서에 넣어도 유지됨) 스킨의 조인트 ID와 바인드 행렬을 채운다.</summary>
        public void ResolveSkins(IEnumerable<SceneNode> tops)
        {
            foreach (var t in tops) Doc.AssignIds(t);
            foreach (var (mesh, skin, joints) in Pending)
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
        var surfaces = CollectSurfaces(g, out var skinData);
        if (surfaces.Count > 0)
        {
            Skeleton3D? skelNode = null;
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
            }
            var swm = System.Diagnostics.Stopwatch.StartNew();
            var mesh = TriangleSoupToPolyMesh.Convert(surfaces, ctx.Options, out var st, out var vmap);
            if (swm.ElapsedMilliseconds > 200) GD.Print($"[ImportPerf]   mesh {g.Name}: {surfaces.Sum(x => x.Indices.Length) / 3} tris → {swm.ElapsedMilliseconds} ms (merged quads {st.MergedQuads})");
            var shape = new MeshShape(mesh);
            node.Shape = shape;
            ctx.Meshes++;
            if (skinData != null && skelNode != null && ctx.BoneNodes.TryGetValue(skelNode, out var boneNodes))
            {
                var skin = BuildSkin(skinData, boneNodes, vmap, mesh, out var jointNodes);
                if (skin != null) { shape.Skin = skin; ctx.Pending.Add((node, skin, jointNodes)); ctx.Skins++; }
            }
        }
        foreach (var child in g.GetChildren())
            if (child is Node3D c3)
            {
                var cn = Convert(c3, ctx);
                node.AttachChild(cn);
            }
        return node;
    }

    /// <summary>
    /// 코어 스킨은 바인드 = 가져온 시점의 조인트 rest 포즈다(ResolveSkins). glTF 역바인드 행렬이 rest 포즈와 다른 파일(BrainStem 등)은
    /// 메시 정점이 바인드 공간에 있어 그대로 두면 rest에서 메시와 스켈레톤이 어긋난다 → Godot이 rest에서 그리는 것과 같게
    /// 삼각형 수프 정점·노멀을 LBS로 rest 포즈에 굽는다(v' = Σ w · inv(메시)·스켈레톤·본 전역 rest·바인드 포즈 · v).
    /// 용접 전에 구워야 바인드 공간에서 우연히 겹친 다른 부품의 정점이 합쳐지지 않는다.
    /// </summary>
    private static void BakeBindToRest(Node3D meshNode, Skeleton3D skel, SkinData sd, List<TriangleSoupToPolyMesh.Surface> surfaces)
    {
        if (sd.Skin is not { } gs || sd.BindToBone == null) return;
        var rel = RootRelative(meshNode).AffineInverse() * RootRelative(skel);
        var mats = new Transform3D[sd.BindToBone.Length];
        bool differs = false;
        for (int i = 0; i < mats.Length; i++)
        {
            int bone = sd.BindToBone[i];
            mats[i] = bone >= 0 && bone < skel.GetBoneCount() ? rel * skel.GetBoneGlobalRest(bone) * gs.GetBindPose(i) : Transform3D.Identity;
            if (!mats[i].IsEqualApprox(Transform3D.Identity)) differs = true;
        }
        if (!differs) return;
        for (int s = 0; s < surfaces.Count && s < sd.PerSurface.Count; s++)
        {
            var (bones, weights, stride) = sd.PerSurface[s];
            if (stride <= 0) continue;
            var surf = surfaces[s];
            int n = Math.Min(surf.Positions.Length, bones.Length / stride);
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
        for (var p = n.GetParent() as Node3D; p != null; p = p.GetParent() as Node3D) t = p.Transform * t;
        return t;
    }

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

    private sealed class SkinData
    {
        public Skin? Skin;
        /// <summary>바인드 번호 → 스켈레톤 본 번호(없으면 BONES 값을 본 번호로 본다).</summary>
        public int[]? BindToBone;
        public List<(int[] bones, float[] weights, int stride)> PerSurface = new();
    }

    private static SkinCluster? BuildSkin(SkinData sd, SceneNode[] boneNodes, int[][] vmap, PolyMesh mesh, out List<SceneNode> jointNodes)
    {
        // 본 인덱스 → 스킨 조인트 인덱스(조인트 ID는 트리 조립 후 ResolveSkins에서 채운다)
        var skin = new SkinCluster();
        var joints = new List<SceneNode>();
        jointNodes = joints;
        var jointIndexOfBone = new Dictionary<int, int>();
        int Joint(int bone)
        {
            if (sd.BindToBone != null) bone = bone >= 0 && bone < sd.BindToBone.Length ? sd.BindToBone[bone] : -1;
            if (bone < 0 || bone >= boneNodes.Length) return -1;
            if (!jointIndexOfBone.TryGetValue(bone, out int j)) { j = joints.Count; joints.Add(boneNodes[bone]); jointIndexOfBone[bone] = j; }
            return j;
        }
        skin.EnsureSize(mesh.VertexCount);
        bool any = false;
        for (int s = 0; s < sd.PerSurface.Count && s < vmap.Length; s++)
        {
            var (bones, weights, stride) = sd.PerSurface[s];
            if (stride <= 0) continue;
            int soupCount = Math.Min(vmap[s].Length, bones.Length / stride);
            for (int i = 0; i < soupCount; i++)
            {
                int v = vmap[s][i];
                if (v < 0 || (v < skin.Weights.Length && skin.Weights[v] != null)) continue;
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
        }
        if (!any) return null;
        Core.Rig.SkinOps.NormalizeAll(mesh, skin);
        return skin;
    }

    private static string SafeName(string s) => string.IsNullOrWhiteSpace(s) ? "node" : s;

    private static List<TriangleSoupToPolyMesh.Surface> CollectSurfaces(Node3D g, out SkinData? skinData)
    {
        skinData = null;
        var list = new List<TriangleSoupToPolyMesh.Surface>();
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
        }
        return list;
    }
}

public sealed class GltfImporter : GodotSceneImporterBase
{
    public override string Name => "glTF 2.0";
    public override IReadOnlyList<string> Extensions { get; } = new[] { ".glb", ".gltf" };
    protected override (GltfDocument, GltfState) CreateDocument() => (new GltfDocument(), new GltfState());
}

public sealed class FbxImporter : GodotSceneImporterBase
{
    public override string Name => "FBX";
    public override IReadOnlyList<string> Extensions { get; } = new[] { ".fbx" };
    protected override (GltfDocument, GltfState) CreateDocument() => (new FbxDocument(), new FbxState());
}
