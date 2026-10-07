namespace Cube.Core.Scene;

/// <summary>
/// 재생 포즈(<see cref="SceneNode.Pose"/>) 적용/해제. 포즈는 표시 전용이며 Undo·저장 대상이 아니다.
/// 내보내기·저장·브리지 전송은 <see cref="RestScope"/> 안에서 해 바인드(rest) 포즈 기준으로 기록한다.
/// </summary>
public static class AnimationPose
{
    public static bool HasPose(Document doc) => doc.Nodes.Values.Any(n => n.Pose != null);

    /// <summary>클립을 t초로 평가해 트랙이 있는 노드에 포즈를 넣는다(없는 노드는 rest).</summary>
    public static void Apply(Document doc, AnimationClip clip, float t)
    {
        var touched = new HashSet<NodeId>();
        foreach (var tr in clip.Tracks)
        {
            var n = doc.Find(tr.Node);
            if (n == null) continue;
            n.Pose = tr.Evaluate(t, n.Local);
            touched.Add(n.Id);
        }
        foreach (var n in doc.Nodes.Values) if (n.Pose != null && !touched.Contains(n.Id)) n.Pose = null;
        doc.Notify(new DocChange(ChangeKind.PoseChanged, NodeId.None));
    }

    /// <summary>모든 포즈를 지워 rest로 되돌린다.</summary>
    public static void Clear(Document doc)
    {
        bool any = false;
        foreach (var n in doc.Nodes.Values) if (n.Pose != null) { n.Pose = null; any = true; }
        if (any) doc.Notify(new DocChange(ChangeKind.PoseChanged, NodeId.None));
    }

    /// <summary>범위 동안 포즈를 끄고(통지 없음) 끝나면 되돌린다. 내보내기/저장용.</summary>
    public static IDisposable RestScope(Document doc)
    {
        var saved = doc.Nodes.Values.Where(n => n.Pose != null).Select(n => (n, n.Pose)).ToList();
        foreach (var (n, _) in saved) n.Pose = null;
        return new Restore(saved);
    }

    private sealed class Restore : IDisposable
    {
        private List<(SceneNode n, Transform3? p)>? _saved;
        public Restore(List<(SceneNode, Transform3?)> s) { _saved = s; }
        public void Dispose() { if (_saved == null) return; foreach (var (n, p) in _saved) n.Pose = p; _saved = null; }
    }
}
