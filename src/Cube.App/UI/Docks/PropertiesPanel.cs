using Cube.Core.Commands;
using Cube.Core.Scene;
using Godot;
using NVec3 = System.Numerics.Vector3;

namespace Cube.App.UI.Docks;

/// <summary>Properties 패널(Maya Channel Box 역할): 활성 오브젝트의 Translate/Rotate/Scale XYZ. 편집은 TransformNodesCommand로 기록.</summary>
public partial class PropertiesPanel : VBoxContainer
{
    private Document _doc = null!;
    private Label _title = null!;
    private readonly SpinBox[] _fields = new SpinBox[9];
    private bool _updating;
    private NodeId _node;

    public void Bind(Document doc)
    {
        _doc = doc;
        doc.Selection.Changed += Refresh;
        doc.Selection.ModeChanged += Refresh;
        doc.Changed += c => { if (c.Kind is ChangeKind.TransformChanged or ChangeKind.NodeRenamed or ChangeKind.Reset or ChangeKind.NodeRemoved) Refresh(); };
        Refresh();
    }

    public override void _Ready()
    {
        float s = CubeApp.Instance.UiScale;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _title = new Label { Text = "" };
        AddChild(_title);
        var grid = new GridContainer { Columns = 4 };
        grid.AddChild(new Label { Text = "" });
        foreach (var h in new[] { "X", "Y", "Z" }) grid.AddChild(new Label { Text = h, HorizontalAlignment = HorizontalAlignment.Center });
        string[] rows = { "Translate", "Rotate", "Scale" };
        for (int r = 0; r < 3; r++)
        {
            grid.AddChild(new Label { Text = rows[r] });
            for (int c = 0; c < 3; c++)
            {
                var sb = new SpinBox { Step = 0.001, MinValue = -1e9, MaxValue = 1e9, AllowGreater = true, AllowLesser = true, CustomMinimumSize = new Vector2(64 * s, 0), UpdateOnTextChanged = false };
                sb.GetLineEdit().ContextMenuEnabled = false;
                // Enter로 확정하면 Maya처럼 포커스를 뷰포트로 돌린다
                sb.GetLineEdit().TextSubmitted += _ => CallDeferred(nameof(ReturnFocus));
                int idx = r * 3 + c;
                sb.ValueChanged += v => OnValueChanged(idx, (float)v);
                _fields[idx] = sb;
                grid.AddChild(sb);
            }
        }
        AddChild(grid);
    }

    private static void ReturnFocus() => Shell.Instance?.Viewport.GrabFocus();

    private void Refresh()
    {
        var sel = _doc.Selection;
        var node = sel.Mode == Core.Selection.SelectMode.Object ? _doc.Find(sel.ActiveObject) : null;
        _updating = true;
        if (node == null)
        {
            _node = NodeId.None;
            _title.Text = sel.IsComponentMode ? "(component mode)" : "";
            foreach (var f in _fields) { f.Editable = false; f.Value = 0; }
        }
        else
        {
            _node = node.Id;
            _title.Text = node.Name;
            var t = node.Local;
            Set(0, t.Translation); Set(1, t.RotationDegrees); Set(2, t.Scale);
            foreach (var f in _fields) f.Editable = true;
        }
        _updating = false;
    }

    private void Set(int row, NVec3 v)
    {
        _fields[row * 3].Value = Math.Round(v.X, 3); _fields[row * 3 + 1].Value = Math.Round(v.Y, 3); _fields[row * 3 + 2].Value = Math.Round(v.Z, 3);
    }

    private void OnValueChanged(int idx, float value)
    {
        if (_updating || _node.IsNone) return;
        var node = _doc.Find(_node); if (node == null) return;
        var before = node.Local; var after = before;
        int row = idx / 3, col = idx % 3;
        NVec3 v = row == 0 ? after.Translation : row == 1 ? after.RotationDegrees : after.Scale;
        if (col == 0) v.X = value; else if (col == 1) v.Y = value; else v.Z = value;
        if (row == 0) after.Translation = v; else if (row == 1) after.RotationDegrees = v; else after.Scale = v;
        if (after == before) return;
        node.Local = after;
        _doc.Notify(new DocChange(ChangeKind.TransformChanged, node.Id));
        _doc.Undo.Push(new TransformNodesCommand("Set Attribute", new[] { node.Id }, new[] { before }, new[] { after }), alreadyApplied: true);
    }
}
