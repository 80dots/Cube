using Cube.Core.Commands;
using Cube.Core.Scene;
using Cube.Core.Selection;
using Godot;

namespace Cube.App.UI.Docks;

/// <summary>Document DAG를 Tree로 보여주고 선택을 양방향 동기화한다. 더블클릭으로 이름을 바꾼다.</summary>
public partial class Outliner : Tree
{
    private Document _doc = null!;
    private readonly Dictionary<NodeId, TreeItem> _items = new();
    private bool _syncing;

    public void Bind(Document doc)
    {
        _doc = doc;
        doc.Changed += OnDocChanged;
        doc.Selection.Changed += SyncFromSelection;
        Rebuild();
    }

    public override void _Ready()
    {
        HideRoot = true;
        SelectMode = SelectModeEnum.Multi;
        AllowReselect = true;
        FocusMode = FocusModeEnum.Click;
        Columns = 1;
        SizeFlagsVertical = SizeFlags.ExpandFill;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        MultiSelected += (_, _, _) => CallDeferred(nameof(SyncToSelection));
        ItemEdited += OnItemEdited;
        ItemActivated += () => { var it = GetSelected(); if (it != null) EditSelected(); };
        NothingSelected += () => { if (!_syncing) UI.Shell.Instance.RecordSelection(s => s.ClearAll()); };
    }

    /// <summary>도킹/떼어 내기로 트리를 옮겨도 구독을 유지하고, 실제로 지워질 때만 해제한다.</summary>
    public override void _Notification(int what)
    {
        if (what != (int)NotificationPredelete) return;
        if (_doc != null) { _doc.Changed -= OnDocChanged; _doc.Selection.Changed -= SyncFromSelection; }
    }

    private void OnDocChanged(DocChange c)
    {
        switch (c.Kind)
        {
            case ChangeKind.Reset: case ChangeKind.NodeAdded: case ChangeKind.NodeRemoved: case ChangeKind.NodeReparented:
                Rebuild(); break;
            case ChangeKind.NodeRenamed:
                if (_items.TryGetValue(c.Node, out var it)) it.SetText(0, _doc.Get(c.Node).Name);
                break;
        }
    }

    private void Rebuild()
    {
        _syncing = true;
        Clear(); _items.Clear();
        var root = CreateItem();
        foreach (var n in _doc.Root.Children) AddItem(n, root);
        _syncing = false;
        SyncFromSelection();
    }

    private void AddItem(SceneNode n, TreeItem parent)
    {
        var it = CreateItem(parent);
        it.SetText(0, n.Name);
        it.SetMetadata(0, n.Id.Value);
        it.SetEditable(0, true);
        _items[n.Id] = it;
        foreach (var c in n.Children) AddItem(c, it);
    }

    private void SyncFromSelection()
    {
        if (_syncing) return;
        _syncing = true;
        DeselectAll();
        foreach (var id in _doc.Selection.Objects)
            if (_items.TryGetValue(id, out var it)) it.Select(0);
        _syncing = false;
    }

    private void SyncToSelection()
    {
        if (_syncing) return;
        var ids = new List<NodeId>();
        for (var it = GetNextSelected(null); it != null; it = GetNextSelected(it))
            ids.Add(new NodeId(it.GetMetadata(0).AsInt32()));
        _syncing = true;
        UI.Shell.Instance.RecordSelection(s => { s.Mode = Core.Selection.SelectMode.Object; s.SelectObjects(ids); });
        _syncing = false;
    }

    private void OnItemEdited()
    {
        var it = GetEdited(); if (it == null) return;
        var id = new NodeId(it.GetMetadata(0).AsInt32());
        var name = it.GetText(0).Trim();
        var node = _doc.Find(id);
        if (node == null || name.Length == 0 || name == node.Name) { if (node != null) it.SetText(0, node.Name); return; }
        _doc.Undo.Push(new RenameNodeCommand(_doc, id, name));
    }
}
