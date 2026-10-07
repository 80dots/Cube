using Cube.Core.Commands;
using Cube.Core.Scene;
using Godot;

namespace Cube.App.UI.UvEditor;

/// <summary>UV Set Editor(플로팅 패널): 활성 메시의 UV 세트 목록. New / Copy / Rename / Delete / Set Current.</summary>
public partial class UvSetEditorWindow : FloatingPanel
{
    private Shell _shell = null!;
    private ItemList _list = null!;
    private LineEdit _name = null!;
    private Label _info = null!;

    public void Setup(Shell shell)
    {
        _shell = shell;
        float s = CubeApp.Instance.UiScale;
        Title = "UV Set Editor";
        Size = new Vector2(320 * s, 300 * s);
        MinPanelSize = new Vector2(260 * s, 220 * s);
        var box = new VBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        _info = new Label { Text = "" };
        box.AddChild(_info);
        _list = new ItemList { SizeFlagsVertical = Control.SizeFlags.ExpandFill, FocusMode = Control.FocusModeEnum.Click };
        _list.ItemSelected += i => { var n = Node(); if (n?.Mesh != null) { int idx = (int)i; if (n.Mesh.UvSets.Count > idx && n.Mesh.CurrentUvSet != idx) Op("Switch UV Set", n, m => m.SwitchUvSet(idx)); } };
        box.AddChild(_list);
        var row = new HBoxContainer();
        foreach (var (label, act) in new[] { ("New", "uv.setCreate"), ("Copy", "uv.setCopy"), ("Delete", "uv.setDelete") })
        {
            var b = new Button { Text = label, FocusMode = Control.FocusModeEnum.None }; string a = act;
            b.Pressed += () => shell.Actions.Invoke(a); row.AddChild(b);
        }
        box.AddChild(row);
        var row2 = new HBoxContainer();
        _name = new LineEdit { PlaceholderText = "rename current set", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _name.TextSubmitted += t => Rename(t);
        row2.AddChild(_name);
        var rb = new Button { Text = "Rename", FocusMode = Control.FocusModeEnum.None };
        rb.Pressed += () => Rename(_name.Text);
        row2.AddChild(rb);
        box.AddChild(row2);
        Content.AddChild(box);
        shell.Document.Changed += c => { if (c.Kind is ChangeKind.MeshAttributes or ChangeKind.Reset or ChangeKind.NodeRemoved) Refresh(); };
        shell.Document.Selection.Changed += Refresh;
        Refresh();
    }

    private SceneNode? Node() { var doc = _shell.Document; return doc.Find(doc.Selection.ActiveObject) ?? doc.Selection.Components.Keys.Select(id => doc.Find(id)).FirstOrDefault(n => n?.Mesh != null); }

    private void Op(string name, SceneNode n, Action<Core.Mesh.PolyMesh> op)
    {
        _shell.Document.Undo.Push(new UvSetsCommand(name, n.Id, op));
        _shell.UvEditorWindow?.Canvas.Invalidate();
        Refresh();
    }

    private void Rename(string text)
    {
        var n = Node(); if (n?.Mesh == null || string.IsNullOrWhiteSpace(text)) return;
        string t = text.Trim();
        Op("Rename UV Set", n, m => { m.EnsureUvSets(); m.UvSets[m.CurrentUvSet].Name = t; });
    }

    public void Refresh()
    {
        _list.Clear();
        var n = Node();
        if (n?.Mesh == null) { _info.Text = "(select a mesh)"; return; }
        var m = n.Mesh;
        if (m.UvSets.Count == 0) { _list.AddItem("map1 (current)"); _list.Select(0); _info.Text = n.Name; return; }
        for (int i = 0; i < m.UvSets.Count; i++) _list.AddItem(m.UvSets[i].Name + (i == m.CurrentUvSet ? " (current)" : ""));
        _list.Select(Math.Clamp(m.CurrentUvSet, 0, m.UvSets.Count - 1));
        _info.Text = $"{n.Name}: {m.UvSets.Count} UV set(s)";
    }

    public void Toggle() { if (Visible) Close(); else { Open(); Refresh(); } }
}
