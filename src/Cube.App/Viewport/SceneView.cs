using Cube.App.Bridge;
using Cube.Core.Scene;
using Godot;

namespace Cube.App.Viewport;

/// <summary>
/// Document의 DAG를 Godot Node3D 트리로 미러링한다. DocChange를 받아 추가/제거/트랜스폼/메시 변경을 동기화한다.
/// </summary>
public partial class SceneView : Node3D
{
    private Document? _doc;
    private readonly Dictionary<NodeId, Node3D> _views = new();
    private readonly Dictionary<NodeId, MeshView> _meshViews = new();
    private readonly Dictionary<NodeId, JointView> _jointViews = new();
    private readonly Dictionary<NodeId, LightView> _lightViews = new();
    public IReadOnlyDictionary<NodeId, LightView> LightViews => _lightViews;

    public IReadOnlyDictionary<NodeId, MeshView> MeshViews => _meshViews;
    public IReadOnlyDictionary<NodeId, JointView> JointViews => _jointViews;
    public event Action? Rebuilt;
    /// <summary>스킨/가중치가 바뀌어 스타일을 다시 적용해야 할 때.</summary>
    public event Action<MeshView>? StyleRefreshRequested;
    /// <summary>새 MeshView가 트리에 들어간 직후(스타일 적용용).</summary>
    public event Action<MeshView>? MeshViewCreated;

    public void Bind(Document doc)
    {
        if (_doc != null) _doc.Changed -= OnDocChanged;
        _doc = doc;
        _doc.Changed += OnDocChanged;
        RebuildAll();
    }

    public override void _ExitTree()
    {
        if (_doc != null) _doc.Changed -= OnDocChanged;
    }

    public MeshView? GetMeshView(NodeId id) => _meshViews.TryGetValue(id, out var v) ? v : null;
    public Node3D? GetView(NodeId id) => _views.TryGetValue(id, out var v) ? v : null;

    private void OnDocChanged(DocChange c)
    {
        switch (c.Kind)
        {
            case ChangeKind.Reset: SkinDeformCache.Clear(); RebuildAll(); break;
            case ChangeKind.NodeAdded: AddView(_doc!.Get(c.Node)); RefreshJoints(); UpdateSkins(); break;
            case ChangeKind.NodeRemoved: RemoveView(c.Node); SkinDeformCache.Forget(c.Node); RefreshJoints(); UpdateSkins(); break;
            case ChangeKind.SkinChanged:
                UpdateSkin(c.Node);
                if (_meshViews.TryGetValue(c.Node, out var sv)) StyleRefreshRequested?.Invoke(sv);
                break;
            case ChangeKind.LightChanged:
                if (_lightViews.TryGetValue(c.Node, out var lv)) lv.Refresh();
                break;
            case ChangeKind.MaterialChanged:
                if (c.Node.IsNone) { foreach (var m in _meshViews.Values) StyleRefreshRequested?.Invoke(m); }
                else if (_meshViews.TryGetValue(c.Node, out var mm)) StyleRefreshRequested?.Invoke(mm);
                break;
            case ChangeKind.NodeReparented:
                {
                    var n = _doc!.Get(c.Node);
                    if (_views.TryGetValue(c.Node, out var v))
                    {
                        var newParent = ParentViewFor(n);
                        if (v.GetParent() != newParent) v.Reparent(newParent, keepGlobalTransform: false);
                        v.Transform = n.Evaluated.ToGodot();
                    }
                    RefreshJoints(); UpdateSkins();
                    break;
                }
            case ChangeKind.TransformChanged:
                if (_views.TryGetValue(c.Node, out var tv)) tv.Transform = _doc!.Get(c.Node).Evaluated.ToGodot();
                if (_doc!.Get(c.Node).IsJoint || _doc.Get(c.Node).Parent?.IsJoint == true) RefreshJoints();
                UpdateSkins();
                break;
            case ChangeKind.PoseChanged:
                // 숨은 패널(단일 뷰의 나머지 3개)은 아무것도 하지 않고 다시 보일 때 한 번에 반영한다
                if (IsShown != null && !IsShown()) { _poseDirty = true; break; }
                ApplyPose();
                break;
            case ChangeKind.VisibilityChanged:
                if (_views.TryGetValue(c.Node, out var vv)) vv.Visible = _doc!.Get(c.Node).Visible;
                break;
            case ChangeKind.NodeRenamed:
                if (_views.TryGetValue(c.Node, out var rv)) rv.Name = _doc!.Get(c.Node).Name;
                break;
            case ChangeKind.MeshTopology:
            case ChangeKind.MeshAttributes:
            case ChangeKind.DisplayChanged:
                if (_meshViews.TryGetValue(c.Node, out var mv)) mv.Rebuild();
                break;
            case ChangeKind.MeshGeometry:
                // 스킨 메시는 변형 위치를 다시 계산해야 바뀐 정점이 보인다(캐시가 같은 통지 안에서는 한 번만 계산)
                if (_doc!.Find(c.Node)?.Skin != null) UpdateSkin(c.Node);
                else if (_meshViews.TryGetValue(c.Node, out var gv)) gv.UpdatePositions();
                break;
        }
    }

    private Node3D ParentViewFor(SceneNode n)
        => n.Parent != null && !n.Parent.IsRoot && _views.TryGetValue(n.Parent.Id, out var p) ? p : this;

    private void AddView(SceneNode n)
    {
        if (_views.ContainsKey(n.Id)) return;
        Node3D view;
        if (n.MeshShape != null)
        {
            var mv = new MeshView(n);
            _meshViews[n.Id] = mv;
            view = mv;
        }
        else if (n.IsJoint)
        {
            var jv = new JointView(n);
            _jointViews[n.Id] = jv;
            view = jv;
        }
        else if (n.IsLight)
        {
            var lv = new LightView(n);
            _lightViews[n.Id] = lv;
            view = lv;
        }
        else view = new Node3D { Name = n.Name };
        view.Transform = n.Evaluated.ToGodot();
        view.Visible = n.Visible;
        _views[n.Id] = view;
        ParentViewFor(n).AddChild(view);
        if (view is MeshView created) MeshViewCreated?.Invoke(created);
        foreach (var c in n.Children) AddView(c);
    }

    private void RemoveView(NodeId id)
    {
        if (!_views.TryGetValue(id, out var v)) return;
        // 하위 뷰 사전도 정리
        foreach (var (k, child) in _views.ToArray())
            if (k != id && IsDescendantOf(child, v)) { _views.Remove(k); _meshViews.Remove(k); _jointViews.Remove(k); _lightViews.Remove(k); }
        _views.Remove(id); _meshViews.Remove(id); _jointViews.Remove(id); _lightViews.Remove(id);
        v.QueueFree();
    }

    /// <summary>재생 포즈 반영: 모든 뷰 트랜스폼(Evaluated) + 조인트 본 + 스킨 변형.</summary>
    private void ApplyPose()
    {
        _poseDirty = false;
        long t0 = AnimPerf.Begin();
        foreach (var (id, view) in _views)
            if (_doc!.Find(id) is { } pn) view.Transform = pn.Evaluated.ToGodot();
        AnimPerf.End("view.xform", t0);
        long t1 = AnimPerf.Begin();
        RefreshJoints();
        AnimPerf.End("view.joints", t1);
        UpdateSkins();
    }

    /// <summary>모든 조인트의 본(자식 위치)을 다시 그린다(바뀌지 않은 조인트는 JointView가 건너뛴다).</summary>
    public void RefreshJoints() { foreach (var jv in _jointViews.Values) jv.Refresh(); }

    /// <summary>스킨 메시 전체의 변형 위치를 다시 계산한다(조인트 이동 등).</summary>
    public void UpdateSkins()
    {
        if (_doc == null) return;
        if (IsShown != null && !IsShown()) { _skinsDirty = true; return; }
        _skinsDirty = false;
        _skinNodes.Clear(); _skinViews.Clear();
        foreach (var n in _doc.SkinnedNodes())
        {
            if (n.Mesh == null || !_meshViews.TryGetValue(n.Id, out var mv)) continue;
            _skinNodes.Add(n); _skinViews.Add(mv);
        }
        if (_skinNodes.Count == 0) return;
        // 1) 변형(LBS): 통지 일련번호로 패널 4개가 공유, 메시별 병렬
        long t0 = AnimPerf.Begin();
        SkinDeformCache.Update(_doc, _skinNodes);
        AnimPerf.End("skin.deform", t0);
        // 2) 렌더 배열 위치 갱신: 순수 C#이라 메시별 병렬
        long t1 = AnimPerf.Begin();
        for (int i = 0; i < _skinViews.Count; i++) _skinViews[i].SetDeformedNoUpload(SkinDeformCache.Get(_doc, _skinNodes[i])); // 캐시 접근은 순차로
        if (_skinViews.Count == 1) _skinViews[0].PreparePositions();
        else Parallel.For(0, _skinViews.Count, i => _skinViews[i].PreparePositions());
        AnimPerf.End("mesh.updatePositions", t1);
        // 3) 업로드(메인 스레드)
        long t2 = AnimPerf.Begin();
        foreach (var mv in _skinViews) mv.CommitPositions();
        AnimPerf.End("skin.setDeformed", t2);
    }

    private readonly List<SceneNode> _skinNodes = new();
    private readonly List<MeshView> _skinViews = new();

    /// <summary>이 뷰를 담은 패널이 보이는지. 안 보이면 포즈/스킨 갱신을 미룬다(<see cref="FlushSkins"/>).</summary>
    public Func<bool>? IsShown;
    private bool _skinsDirty, _poseDirty;

    /// <summary>숨어 있는 동안 미룬 포즈·스킨 변형을 반영한다.</summary>
    public void FlushSkins()
    {
        if (_poseDirty) ApplyPose();
        else if (_skinsDirty) UpdateSkins();
    }

    public void UpdateSkin(NodeId id)
    {
        if (_doc == null || !_meshViews.TryGetValue(id, out var mv)) return;
        var n = _doc.Find(id);
        if (n?.Mesh == null) return;
        if (n.Skin == null) { if (mv.Deformed != null) mv.SetDeformed(null); SkinDeformCache.Forget(id); return; }
        long t0 = AnimPerf.Begin();
        var buf = SkinDeformCache.Get(_doc, n);   // 패널 4개가 같은 통지에서 공유
        AnimPerf.End("skin.deform", t0);
        long t1 = AnimPerf.Begin();
        mv.SetDeformed(buf);
        AnimPerf.End("skin.setDeformed", t1);
    }

    private static bool IsDescendantOf(Node node, Node ancestor)
    {
        for (var p = node.GetParent(); p != null; p = p.GetParent()) if (p == ancestor) return true;
        return false;
    }

    private void RebuildAll()
    {
        foreach (var v in _views.Values) v.QueueFree();
        _views.Clear(); _meshViews.Clear(); _jointViews.Clear(); _lightViews.Clear();
        if (_doc == null) return;
        foreach (var c in _doc.Root.Children) AddView(c);
        RefreshJoints(); UpdateSkins();
        Rebuilt?.Invoke();
    }
}
