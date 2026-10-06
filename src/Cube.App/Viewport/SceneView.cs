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

    public IReadOnlyDictionary<NodeId, MeshView> MeshViews => _meshViews;
    public event Action? Rebuilt;
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
            case ChangeKind.Reset: RebuildAll(); break;
            case ChangeKind.NodeAdded: AddView(_doc!.Get(c.Node)); break;
            case ChangeKind.NodeRemoved: RemoveView(c.Node); break;
            case ChangeKind.NodeReparented:
                {
                    var n = _doc!.Get(c.Node);
                    if (_views.TryGetValue(c.Node, out var v))
                    {
                        var newParent = ParentViewFor(n);
                        if (v.GetParent() != newParent) v.Reparent(newParent, keepGlobalTransform: false);
                        v.Transform = n.Local.ToGodot();
                    }
                    break;
                }
            case ChangeKind.TransformChanged:
                if (_views.TryGetValue(c.Node, out var tv)) tv.Transform = _doc!.Get(c.Node).Local.ToGodot();
                break;
            case ChangeKind.VisibilityChanged:
                if (_views.TryGetValue(c.Node, out var vv)) vv.Visible = _doc!.Get(c.Node).Visible;
                break;
            case ChangeKind.NodeRenamed:
                if (_views.TryGetValue(c.Node, out var rv)) rv.Name = _doc!.Get(c.Node).Name;
                break;
            case ChangeKind.MeshTopology:
            case ChangeKind.MeshAttributes:
                if (_meshViews.TryGetValue(c.Node, out var mv)) mv.Rebuild();
                break;
            case ChangeKind.MeshGeometry:
                if (_meshViews.TryGetValue(c.Node, out var gv)) gv.UpdatePositions();
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
        else view = new Node3D { Name = n.Name };
        view.Transform = n.Local.ToGodot();
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
            if (k != id && IsDescendantOf(child, v)) { _views.Remove(k); _meshViews.Remove(k); }
        _views.Remove(id); _meshViews.Remove(id);
        v.QueueFree();
    }

    private static bool IsDescendantOf(Node node, Node ancestor)
    {
        for (var p = node.GetParent(); p != null; p = p.GetParent()) if (p == ancestor) return true;
        return false;
    }

    private void RebuildAll()
    {
        foreach (var v in _views.Values) v.QueueFree();
        _views.Clear(); _meshViews.Clear();
        if (_doc == null) return;
        foreach (var c in _doc.Root.Children) AddView(c);
        Rebuilt?.Invoke();
    }
}
