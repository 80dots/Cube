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
            case ChangeKind.Reset: RebuildAll(); break;
            case ChangeKind.NodeAdded: AddView(_doc!.Get(c.Node)); RefreshJoints(); UpdateSkins(); break;
            case ChangeKind.NodeRemoved: RemoveView(c.Node); RefreshJoints(); UpdateSkins(); break;
            case ChangeKind.SkinChanged:
                UpdateSkin(c.Node);
                if (_meshViews.TryGetValue(c.Node, out var sv)) StyleRefreshRequested?.Invoke(sv);
                break;
            case ChangeKind.NodeReparented:
                {
                    var n = _doc!.Get(c.Node);
                    if (_views.TryGetValue(c.Node, out var v))
                    {
                        var newParent = ParentViewFor(n);
                        if (v.GetParent() != newParent) v.Reparent(newParent, keepGlobalTransform: false);
                        v.Transform = n.Local.ToGodot();
                    }
                    RefreshJoints(); UpdateSkins();
                    break;
                }
            case ChangeKind.TransformChanged:
                if (_views.TryGetValue(c.Node, out var tv)) tv.Transform = _doc!.Get(c.Node).Local.ToGodot();
                if (_doc!.Get(c.Node).IsJoint || _doc.Get(c.Node).Parent?.IsJoint == true) RefreshJoints();
                UpdateSkins();
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
        else if (n.IsJoint)
        {
            var jv = new JointView(n);
            _jointViews[n.Id] = jv;
            view = jv;
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
            if (k != id && IsDescendantOf(child, v)) { _views.Remove(k); _meshViews.Remove(k); _jointViews.Remove(k); }
        _views.Remove(id); _meshViews.Remove(id); _jointViews.Remove(id);
        v.QueueFree();
    }

    /// <summary>모든 조인트의 본(자식 위치)을 다시 그린다.</summary>
    public void RefreshJoints() { foreach (var jv in _jointViews.Values) jv.Refresh(); }

    /// <summary>스킨 메시 전체의 변형 위치를 다시 계산한다(조인트 이동 등).</summary>
    public void UpdateSkins()
    {
        if (_doc == null) return;
        foreach (var n in _doc.SkinnedNodes()) UpdateSkin(n.Id);
    }

    private readonly Dictionary<NodeId, System.Numerics.Vector3[]> _deformBuf = new();

    public void UpdateSkin(NodeId id)
    {
        if (_doc == null || !_meshViews.TryGetValue(id, out var mv)) return;
        var n = _doc.Find(id);
        if (n?.Mesh == null) return;
        if (n.Skin == null) { if (mv.Deformed != null) mv.SetDeformed(null); _deformBuf.Remove(id); return; }
        if (!_deformBuf.TryGetValue(id, out var buf) || buf.Length != n.Mesh.VertexCount) { buf = new System.Numerics.Vector3[n.Mesh.VertexCount]; _deformBuf[id] = buf; }
        System.Numerics.Matrix4x4.Invert(n.WorldMatrix, out var inv);
        Core.Rig.SkinOps.Deform(n.Mesh, n.Skin, jid => _doc.Find(jid)?.WorldMatrix, inv, buf);
        mv.SetDeformed(buf);
    }

    private static bool IsDescendantOf(Node node, Node ancestor)
    {
        for (var p = node.GetParent(); p != null; p = p.GetParent()) if (p == ancestor) return true;
        return false;
    }

    private void RebuildAll()
    {
        foreach (var v in _views.Values) v.QueueFree();
        _views.Clear(); _meshViews.Clear(); _jointViews.Clear();
        if (_doc == null) return;
        foreach (var c in _doc.Root.Children) AddView(c);
        RefreshJoints(); UpdateSkins();
        Rebuilt?.Invoke();
    }
}
