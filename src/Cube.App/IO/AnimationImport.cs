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
public static class AnimationImport
{
    public static List<AnimationClip> Extract(Node scene, Dictionary<Node, SceneNode> nodeMap, Dictionary<Skeleton3D, SceneNode[]> boneNodes)
    {
        var clips = new List<AnimationClip>();
        foreach (var player in FindPlayers(scene))
        {
            var root = player.GetNodeOrNull(player.RootNode) ?? player.GetParent();
            if (root == null) continue;
            foreach (var libName in player.GetAnimationLibraryList())
            {
                var lib = player.GetAnimationLibrary(libName);
                foreach (var animName in lib.GetAnimationList())
                {
                    var anim = lib.GetAnimation(animName);
                    if (anim == null) continue;
                    string name = libName.ToString() is { Length: > 0 } ln ? $"{ln}/{animName}" : animName.ToString();
                    var clip = Convert(anim, name, root, nodeMap, boneNodes);
                    if (clip != null && clip.Tracks.Count > 0) clips.Add(clip);
                }
            }
        }
        return clips;
    }

    private static IEnumerable<AnimationPlayer> FindPlayers(Node n)
    {
        if (n is AnimationPlayer p) yield return p;
        foreach (var c in n.GetChildren())
            foreach (var x in FindPlayers(c)) yield return x;
    }

    private static SceneNode? Resolve(Node root, NodePath path, Dictionary<Node, SceneNode> nodeMap, Dictionary<Skeleton3D, SceneNode[]> boneNodes)
    {
        // 이름 부분만으로 노드를 찾고 하위 이름(:bone)이 있으면 본을 찾는다
        var names = new List<string>();
        for (int i = 0; i < path.GetNameCount(); i++) names.Add(path.GetName(i));
        string nodePath = (path.IsAbsolute() ? "/" : "") + string.Join("/", names);
        var target = names.Count == 0 ? root : root.GetNodeOrNull(nodePath);
        if (target == null) return null;
        if (path.GetSubNameCount() > 0)
        {
            if (target is not Skeleton3D skel || !boneNodes.TryGetValue(skel, out var bones)) return null;
            int bi = skel.FindBone(path.GetSubName(0));
            return bi >= 0 && bi < bones.Length ? bones[bi] : null;
        }
        return nodeMap.TryGetValue(target, out var sn) ? sn : null;
    }

    private static AnimationClip? Convert(Animation anim, string name, Node root, Dictionary<Node, SceneNode> nodeMap, Dictionary<Skeleton3D, SceneNode[]> boneNodes)
    {
        var clip = new AnimationClip
        {
            Name = name,
            Loop = anim.LoopMode != Animation.LoopModeEnum.None,
            FrameRate = anim.Step > 1e-4f ? MathF.Round(1f / anim.Step) : 30f,
        };
        if (clip.FrameRate < 1f || clip.FrameRate > 240f) clip.FrameRate = 30f;
        var byNode = new Dictionary<SceneNode, NodeTrack>();
        for (int t = 0; t < anim.GetTrackCount(); t++)
        {
            var type = anim.TrackGetType(t);
            if (type is not (Animation.TrackType.Position3D or Animation.TrackType.Rotation3D or Animation.TrackType.Scale3D)) continue;
            var sn = Resolve(root, anim.TrackGetPath(t), nodeMap, boneNodes);
            if (sn == null) continue;
            if (!byNode.TryGetValue(sn, out var tr)) { tr = new NodeTrack { Node = sn.Id, NodeName = sn.Name }; byNode[sn] = tr; clip.Tracks.Add(tr); }
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
                            var q = v.AsQuaternion();
                            tr.Rotation.Add(new AnimKey<NQuat>(time, NQuat.Normalize(new NQuat(q.X, q.Y, q.Z, q.W))));
                            break;
                        }
                }
            }
        }
        clip.UpdateLength();
        if (anim.Length > clip.Length) clip.Length = (float)anim.Length;
        return clip;
    }
}
