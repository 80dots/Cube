namespace Cube.Core.Scene;

/// <summary>
/// 재생 포즈(<see cref="SceneNode.Pose"/>) 적용/해제. 포즈는 표시 전용이며 Undo·저장 대상이 아니다.
/// 내보내기·저장·브리지 전송은 <see cref="RestScope"/> 안에서 해 바인드(rest) 포즈 기준으로 기록한다.
/// </summary>
public static class AnimationPose
{
    /// <summary>포즈가 걸린 노드가 하나라도 있는지.</summary>
    public static bool HasPose(Document doc) => doc.Nodes.Values.Any(n => n.Pose != null);

    /// <summary>클립을 t초로 평가해 트랙이 있는 노드에 포즈를 넣는다(없는 노드는 rest).</summary>
    /// <remarks>모든 노드를 갱신한 뒤 PoseChanged를 한 번만 통지해 SceneView가 트랜스폼·조인트·스킨을 일괄 갱신하게 한다.</remarks>
    public static void Apply(Document doc, AnimationClip clip, float t)
    {
        // 트랙이 있는 노드: Local을 rest로 삼아 평가한 포즈를 넣는다.
        var touched = new HashSet<NodeId>();
        foreach (var tr in clip.Tracks)
        {
            var n = doc.Find(tr.Node);
            if (n == null) continue;
            n.Pose = tr.Evaluate(t, n.Local);
            touched.Add(n.Id);
        }
        // 이전 클립에서 남은 포즈(이번 클립에 트랙 없음)는 지운다.
        foreach (var n in doc.Nodes.Values) if (n.Pose != null && !touched.Contains(n.Id)) n.Pose = null;
        doc.Notify(new DocChange(ChangeKind.PoseChanged, NodeId.None));
    }

    /// <summary>모든 포즈를 지워 rest로 되돌린다.</summary>
    public static void Clear(Document doc)
    {
        bool any = false;
        foreach (var n in doc.Nodes.Values) if (n.Pose != null) { n.Pose = null; any = true; }
        // 실제로 지운 게 있을 때만 통지(불필요한 뷰 갱신 방지).
        if (any) doc.Notify(new DocChange(ChangeKind.PoseChanged, NodeId.None));
    }

    /// <summary>범위 동안 포즈를 끄고(통지 없음) 끝나면 되돌린다. 내보내기/저장용.</summary>
    /// <remarks><c>using (AnimationPose.RestScope(doc)) { ... }</c>. 통지를 하지 않으므로 화면은 깜빡이지 않는다. 새 exporter도 반드시 이걸로 감싼다.</remarks>
    public static IDisposable RestScope(Document doc)
    {
        var saved = doc.Nodes.Values.Where(n => n.Pose != null).Select(n => (n, n.Pose)).ToList();
        foreach (var (n, _) in saved) n.Pose = null;
        return new Restore(saved);
    }

    /// <summary>RestScope가 돌려주는 IDisposable. Dispose 때 저장한 포즈를 다시 넣는다(한 번만).</summary>
    private sealed class Restore : IDisposable
    {
        /// <summary>(노드, 원래 포즈) 목록. 복원 후 null.</summary>
        private List<(SceneNode n, Transform3? p)>? _saved;
        /// <summary>복원할 목록을 받는다.</summary>
        public Restore(List<(SceneNode, Transform3?)> s) { _saved = s; }
        /// <summary>포즈를 되돌린다.</summary>
        public void Dispose() { if (_saved == null) return; foreach (var (n, p) in _saved) n.Pose = p; _saved = null; }
    }
}
