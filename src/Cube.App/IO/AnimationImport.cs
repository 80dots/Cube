using Cube.App.Bridge;
using Cube.Core.Scene;
using Godot;
using NQuat = System.Numerics.Quaternion;
using NVec3 = System.Numerics.Vector3;

namespace Cube.App.IO;

/// <summary>
/// GltfDocument/FbxDocument가 만든 AnimationPlayer의 애니메이션을 <see cref="AnimationClip"/>으로 바꾼다.
/// position_3d/rotation_3d/scale_3d 트랙(노드 경로 또는 "Skeleton:bone")을 가져온 SceneNode에 연결한다.
/// 키 값은 Godot 로컬 TRS(본은 부모 본 기준) 그대로이며 Cube의 조인트/노드 Local과 같은 공간이다.
/// </summary>
/// <remarks>
/// 사용 흐름: <see cref="GodotSceneImporter"/>가 GenerateScene 결과를 순회하며 Godot 노드 → SceneNode 맵과
/// Skeleton3D → 본별 SceneNode 배열을 만든 뒤 <see cref="Extract"/>를 부른다. 결과 클립은 ImportResult.Animations로
/// 넘어가 FileActions.Import가 노드 추가와 같은 Undo 그룹에 SetAnimationsCommand로 넣는다.
/// 위치/회전/스케일 이외의 트랙(블렌드 셰이프, 메서드, 값 트랙 등)은 무시한다.
/// </remarks>
public static class AnimationImport
{
    /// <summary>
    /// 씬 트리 안의 모든 AnimationPlayer에서 모든 라이브러리의 모든 애니메이션을 꺼내 클립 목록으로 만든다.
    /// </summary>
    /// <param name="scene">GltfDocument/FbxDocument.GenerateScene이 만든 Godot 씬 루트.</param>
    /// <param name="nodeMap">Godot 노드 → 가져온 SceneNode(일반 노드 트랙 경로 해석용).</param>
    /// <param name="boneNodes">Skeleton3D → 본 인덱스 순서의 조인트 SceneNode 배열("Skeleton:bone" 트랙 해석용).</param>
    /// <returns>트랙이 하나 이상 연결된 클립만 담은 목록. 이름은 기본 라이브러리면 애니메이션 이름, 아니면 "라이브러리/이름".</returns>
    public static List<AnimationClip> Extract(Node scene, Dictionary<Node, SceneNode> nodeMap, Dictionary<Skeleton3D, SceneNode[]> boneNodes)
    {
        // 결과 클립을 모을 목록
        var clips = new List<AnimationClip>();
        foreach (var player in FindPlayers(scene))
        {
            // 트랙 경로의 기준 노드: AnimationPlayer.RootNode(기본 "..")가 가리키는 노드, 없으면 플레이어의 부모
            var root = player.GetNodeOrNull(player.RootNode) ?? player.GetParent();
            if (root == null) continue;
            foreach (var libName in player.GetAnimationLibraryList())
            // 이름 없는 기본 라이브러리("")와 이름 있는 라이브러리를 모두 순회한다
            {
                var lib = player.GetAnimationLibrary(libName);
                foreach (var animName in lib.GetAnimationList())
                {
                    var anim = lib.GetAnimation(animName);
                    if (anim == null) continue;
                    // 라이브러리 이름이 있으면 "lib/anim" 형식으로 이름을 붙여 다른 라이브러리의 같은 이름과 구분한다
                    string name = libName.ToString() is { Length: > 0 } ln ? $"{ln}/{animName}" : animName.ToString();
                    var clip = Convert(anim, name, root, nodeMap, boneNodes);
                    // 연결된 트랙이 없는 클립(다른 노드만 움직이던 애니메이션)은 버린다
                    if (clip != null && clip.Tracks.Count > 0) clips.Add(clip);
                }
            }
        }
        return clips;
    }

    /// <summary>노드 <paramref name="n"/>과 그 하위 트리에서 AnimationPlayer를 깊이 우선으로 모두 찾는다(지연 열거).</summary>
    private static IEnumerable<AnimationPlayer> FindPlayers(Node n)
    {
        if (n is AnimationPlayer p) yield return p;
        foreach (var c in n.GetChildren())
            foreach (var x in FindPlayers(c)) yield return x;
    }

    /// <summary>
    /// 애니메이션 트랙의 NodePath를 가져온 SceneNode로 해석한다.
    /// 이름 부분(a/b/c)으로 Godot 노드를 찾고, 하위 이름(":bone")이 있으면 그 노드가 Skeleton3D여야 하며
    /// 본 이름 → 본 인덱스 → <paramref name="boneNodes"/>의 조인트 SceneNode로 바꾼다.
    /// </summary>
    /// <returns>대응하는 SceneNode, 찾지 못하거나 맵에 없는(가져오지 않은) 노드면 null.</returns>
    private static SceneNode? Resolve(Node root, NodePath path, Dictionary<Node, SceneNode> nodeMap, Dictionary<Skeleton3D, SceneNode[]> boneNodes)
    {
        // 이름 부분만으로 노드를 찾고 하위 이름(:bone)이 있으면 본을 찾는다
        // NodePath의 이름 요소만 모아 문자열 경로로 다시 조립한다(하위 이름은 제외)
        var names = new List<string>();
        for (int i = 0; i < path.GetNameCount(); i++) names.Add(path.GetName(i));
        string nodePath = (path.IsAbsolute() ? "/" : "") + string.Join("/", names);
        // 이름 요소가 없으면 루트 자신을 가리키는 경로다
        var target = names.Count == 0 ? root : root.GetNodeOrNull(nodePath);
        if (target == null) return null;
        // "Skeleton:bone" 형식: 스켈레톤의 본 인덱스로 조인트 노드를 찾는다
        if (path.GetSubNameCount() > 0)
        {
            if (target is not Skeleton3D skel || !boneNodes.TryGetValue(skel, out var bones)) return null;
            int bi = skel.FindBone(path.GetSubName(0));
            return bi >= 0 && bi < bones.Length ? bones[bi] : null;
        }
        // 일반 노드 트랙: Godot 노드 → SceneNode 맵에서 찾는다
        return nodeMap.TryGetValue(target, out var sn) ? sn : null;
    }

    /// <summary>
    /// Godot <see cref="Animation"/> 하나를 <see cref="AnimationClip"/>으로 변환한다.
    /// 같은 SceneNode를 가리키는 위치/회전/스케일 트랙은 하나의 <see cref="NodeTrack"/>에 합친다.
    /// 루프 여부는 LoopMode, 프레임 레이트는 Step(1/fps)에서 추정하고 1~240 범위를 벗어나면 30fps로 둔다.
    /// 키 시간은 초, 값은 Godot 로컬 TRS(본은 부모 본 기준)를 System.Numerics 형식으로 옮긴 것이다.
    /// </summary>
    private static AnimationClip? Convert(Animation anim, string name, Node root, Dictionary<Node, SceneNode> nodeMap, Dictionary<Skeleton3D, SceneNode[]> boneNodes)
    {
        var clip = new AnimationClip
        {
            Name = name,
            Loop = anim.LoopMode != Animation.LoopModeEnum.None,
            FrameRate = anim.Step > 1e-4f ? MathF.Round(1f / anim.Step) : 30f,
        };
        if (clip.FrameRate < 1f || clip.FrameRate > 240f) clip.FrameRate = 30f;
        // SceneNode별 트랙(한 노드의 T/R/S 트랙을 하나로 모은다)
        var byNode = new Dictionary<SceneNode, NodeTrack>();
        for (int t = 0; t < anim.GetTrackCount(); t++)
        {
            // 위치/회전/스케일 3D 트랙만 다룬다
            var type = anim.TrackGetType(t);
            if (type is not (Animation.TrackType.Position3D or Animation.TrackType.Rotation3D or Animation.TrackType.Scale3D)) continue;
            var sn = Resolve(root, anim.TrackGetPath(t), nodeMap, boneNodes);
            if (sn == null) continue;
            // 이 노드의 첫 트랙이면 NodeTrack을 새로 만들어 클립에 추가한다(NodeName은 표시·재연결용)
            if (!byNode.TryGetValue(sn, out var tr)) { tr = new NodeTrack { Node = sn.Id, NodeName = sn.Name }; byNode[sn] = tr; clip.Tracks.Add(tr); }
            // 키를 하나씩 읽어 트랙 종류에 맞는 키 목록에 추가한다
            int n = anim.TrackGetKeyCount(t);
            for (int k = 0; k < n; k++)
            {
                float time = (float)anim.TrackGetKeyTime(t, k);
                var v = anim.TrackGetKeyValue(t, k);
                switch (type)
                {
                    case Animation.TrackType.Position3D: tr.Position.Add(new AnimKey<NVec3>(time, v.AsVector3().ToNumerics())); break;
                    case Animation.TrackType.Scale3D: tr.Scale.Add(new AnimKey<NVec3>(time, v.AsVector3().ToNumerics())); break;
                    case Animation.TrackType.Rotation3D:
                        {
                            // 쿼터니언은 정규화해서 저장한다(slerp 보간 안정성)
                            var q = v.AsQuaternion();
                            tr.Rotation.Add(new AnimKey<NQuat>(time, NQuat.Normalize(new NQuat(q.X, q.Y, q.Z, q.W))));
                            break;
                        }
                }
            }
        }
        // 길이 = 마지막 키 시간. Godot 애니메이션 길이가 더 길면(뒤쪽 정지 구간) 그 값을 따른다
        clip.UpdateLength();
        if (anim.Length > clip.Length) clip.Length = (float)anim.Length;
        return clip;
    }
}
