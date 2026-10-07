using Cube.Core.Commands;
using Cube.Core.Scene;
using Cube.Core.Selection;
using Godot;
using NVec3 = System.Numerics.Vector3;

namespace Cube.App.UI.Docks;

/// <summary>
/// Properties 패널(Maya Channel Box + INPUTS 역할).
/// Transformation 그룹: 활성 오브젝트의 Translate/Rotate/Scale(편집은 TransformNodesCommand).
/// History 그룹: 메시 구성 이력 스택(최신이 위). 항목을 고르면 그 연산의 파라미터(Bevel Distance, Translate 등)를 수정할 수 있고
/// 수정은 EditHistoryCommand로 그 항목부터 다시 실행된다.
/// </summary>
public partial class PropertiesPanel : VBoxContainer
{
    private Document _doc = null!;
    private Label _title = null!;
    private readonly SpinBox[] _fields = new SpinBox[12];
    private bool _updating;
    private NodeId _node;

    private ItemList _history = null!;
    private int _dragId; private TransformNodesCommand? _dragCmd;
    private Control _lightGroup = null!;
    private OptionButton _lightType = null!;
    private ColorPickerButton _lightColor = null!;
    private SpinBox _lightIntensity = null!, _lightRange = null!, _lightAngle = null!;
    private Control _lightRangeRow = null!, _lightAngleRow = null!;
    private Control _materialGroup = null!;
    private OptionButton _materialPick = null!;
    private MaterialPropsEditor _materialProps = null!;
    private Label _materialNote = null!;
    private VBoxContainer _paramBox = null!;
    private Label _historyEmpty = null!;
    private int _historyIndex = -1;          // 선택된 히스토리 항목(오래된 것부터 센 인덱스)
    private readonly List<Control> _paramControls = new();

    public void Bind(Document doc)
    {
        _doc = doc;
        _materialProps.Setup(Shell.Instance);
        _materialProps.ShowName = true;
        doc.Selection.Changed += Refresh;
        doc.Selection.ModeChanged += Refresh;
        doc.Changed += c =>
        {
            if (c.Kind is ChangeKind.TransformChanged or ChangeKind.NodeRenamed or ChangeKind.Reset or ChangeKind.NodeRemoved or ChangeKind.LightChanged or ChangeKind.MaterialChanged) Refresh();
            else if (c.Kind is ChangeKind.MeshTopology or ChangeKind.HistoryChanged or ChangeKind.MeshGeometry) RefreshHistory();
        };
        Refresh();
    }

    public override void _Ready()
    {
        float s = CubeApp.Instance.UiScale;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _title = new Label { Text = "" };
        AddChild(_title);

        AddChild(Header("Transformation", s));
        var grid = new GridContainer { Columns = 4, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        grid.AddChild(new Label { Text = "" });
        foreach (var h in new[] { "X", "Y", "Z" }) grid.AddChild(new Label { Text = h, HorizontalAlignment = HorizontalAlignment.Center, SizeFlagsHorizontal = SizeFlags.ExpandFill });
        string[] rows = { "Translate", "Rotate", "Scale", "Pivot" };
        for (int r = 0; r < 4; r++)
        {
            grid.AddChild(new Label { Text = rows[r] });
            for (int c = 0; c < 3; c++)
            {
                var sb = Spin(s);
                int idx = r * 3 + c;
                sb.ValueChanged += v => OnValueChanged(idx, (float)v);
                _fields[idx] = sb;
                grid.AddChild(sb);
            }
        }
        AddChild(grid);

        // Material 그룹: 할당 선택 + 할당된 머티리얼의 속성 편집(Material Editor와 같은 편집기)
        var matBox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        matBox.AddChild(Header("Material", s));
        _materialPick = new OptionButton { FocusMode = FocusModeEnum.None, SizeFlagsHorizontal = SizeFlags.ExpandFill, TooltipText = "Material assigned to this object" };
        _materialPick.ItemSelected += i => { if (!_updating && !_node.IsNone) { int id = (int)_materialPick.GetItemId((int)i); if (_doc.Find(_node)?.MaterialId != id) _doc.Undo.Push(new AssignMaterialCommand(new[] { _node }, id)); } };
        matBox.AddChild(LabeledRow("Assigned", _materialPick, s));
        _materialNote = new Label { Text = "lambert1 is the built-in default material.", Modulate = new Color(1, 1, 1, 0.6f), AutowrapMode = TextServer.AutowrapMode.WordSmart };
        matBox.AddChild(_materialNote);
        _materialProps = new MaterialPropsEditor { Name = "MaterialProps" };
        matBox.AddChild(_materialProps);
        _materialGroup = matBox;
        AddChild(matBox);

        // Light 그룹
        var lightBox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        lightBox.AddChild(Header("Light", s));
        _lightType = new OptionButton { FocusMode = FocusModeEnum.None, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        foreach (var t in Enum.GetNames<Core.Scene.LightType>()) _lightType.AddItem(t);
        _lightType.ItemSelected += i => CommitLight(l => l.Type = (Core.Scene.LightType)(int)i);
        lightBox.AddChild(LabeledRow("Type", _lightType, s));
        _lightColor = new ColorPickerButton { EditAlpha = false, SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 22 * s) };
        _lightColor.PopupClosed += () => CommitLight(l => l.Color = new NVec3(_lightColor.Color.R, _lightColor.Color.G, _lightColor.Color.B));
        lightBox.AddChild(LabeledRow("Color", _lightColor, s));
        _lightIntensity = Spin(s); _lightIntensity.MinValue = 0; _lightIntensity.Step = 0.01; _lightIntensity.ValueChanged += v => CommitLight(l => l.Intensity = (float)v);
        lightBox.AddChild(LabeledRow("Intensity", _lightIntensity, s));
        _lightRange = Spin(s); _lightRange.MinValue = 0.01; _lightRange.Step = 0.1; _lightRange.ValueChanged += v => CommitLight(l => l.Range = (float)v);
        _lightRangeRow = LabeledRow("Range", _lightRange, s); lightBox.AddChild(_lightRangeRow);
        _lightAngle = Spin(s); _lightAngle.MinValue = 1; _lightAngle.MaxValue = 179; _lightAngle.Step = 0.5; _lightAngle.ValueChanged += v => CommitLight(l => l.SpotAngle = (float)v);
        _lightAngleRow = LabeledRow("Cone Angle", _lightAngle, s); lightBox.AddChild(_lightAngleRow);
        _lightGroup = lightBox;
        AddChild(lightBox);

        AddChild(Header("History", s));
        _historyEmpty = new Label { Text = "(no construction history)", Modulate = new Color(1, 1, 1, 0.6f) };
        AddChild(_historyEmpty);
        // 남는 세로 공간을 모두 History 목록에 준다(고정 높이로 키우면 도크 최소 높이가 창보다 커져 레이아웃이 넘친다)
        _history = new ItemList { CustomMinimumSize = new Vector2(0, 120 * s), SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill, FocusMode = FocusModeEnum.Click };
        _history.ItemSelected += i => { _historyIndex = HistoryCount - 1 - (int)i; RefreshParams(); };
        AddChild(_history);
        _paramBox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        AddChild(_paramBox);
    }

    private static Control Header(string text, float s)
    {
        var box = new PanelContainer();
        box.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = MayaTheme.PanelDark, ContentMarginLeft = 6 * s, ContentMarginTop = 2 * s, ContentMarginBottom = 2 * s });
        box.AddChild(new Label { Text = text });
        return box;
    }

    private SpinBox Spin(float s)
    {
        var sb = new SpinBox { Step = 0.001, MinValue = -1e9, MaxValue = 1e9, AllowGreater = true, AllowLesser = true, CustomMinimumSize = new Vector2(56 * s, 0), UpdateOnTextChanged = false, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        sb.GetLineEdit().ContextMenuEnabled = false;
        // Enter로 확정하면 Maya처럼 포커스를 뷰포트로 돌린다
        sb.GetLineEdit().TextSubmitted += _ => CallDeferred(nameof(ReturnFocus));
        return sb;
    }

    private static void ReturnFocus() => Shell.Instance?.Viewport.GrabFocus();

    private static Control LabeledRow(string label, Control c, float s)
    {
        var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddChild(new Label { Text = label, CustomMinimumSize = new Vector2(70 * s, 0) });
        c.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        row.AddChild(c);
        return row;
    }

    private void CommitLight(Action<Core.Scene.LightShape> change)
    {
        if (_updating || _node.IsNone) return;
        var node = _doc.Find(_node); if (node?.Light == null) return;
        var after = node.Light.Clone(); change(after);
        if (after.Type == node.Light.Type && after.Color == node.Light.Color && after.Intensity == node.Light.Intensity && after.Range == node.Light.Range && after.SpotAngle == node.Light.SpotAngle) return;
        _doc.Undo.Push(new SetLightCommand(_node, after));
    }

    private void RefreshLight(SceneNode? node)
    {
        var l = node?.Light;
        _lightGroup.Visible = l != null;
        if (l == null) return;
        _lightType.Selected = (int)l.Type;
        _lightColor.Color = new Color(l.Color.X, l.Color.Y, l.Color.Z);
        _lightIntensity.Value = l.Intensity; _lightRange.Value = l.Range; _lightAngle.Value = l.SpotAngle;
        _lightRangeRow.Visible = l.Type != Core.Scene.LightType.Directional;
        _lightAngleRow.Visible = l.Type == Core.Scene.LightType.Spot;
    }

    /// <summary>표시 대상 노드: 오브젝트 모드면 활성 오브젝트, 컴포넌트 모드면 활성 오브젝트 또는 컴포넌트가 선택된 노드.</summary>
    private SceneNode? TargetNode()
    {
        var sel = _doc.Selection;
        var n = _doc.Find(sel.ActiveObject);
        if (n == null && sel.IsComponentMode)
        {
            var id = sel.NodesWithComponents(sel.Mode).FirstOrDefault();
            if (!id.IsNone) n = _doc.Find(id);
        }
        return n;
    }

    private void Refresh()
    {
        var sel = _doc.Selection;
        var node = TargetNode();
        _updating = true;
        if (node == null)
        {
            _node = NodeId.None;
            _title.Text = sel.IsComponentMode ? "(component mode)" : "";
            foreach (var f in _fields) { f.Editable = false; f.Value = 0; }
            RefreshMaterial(null);
            RefreshLight(null);
        }
        else
        {
            _node = node.Id;
            _title.Text = node.Name;
            var t = node.Local;
            Set(0, t.Translation); Set(1, t.RotationDegrees); Set(2, t.Scale); Set(3, t.Pivot);
            bool editable = sel.Mode == SelectMode.Object;
            foreach (var f in _fields) f.Editable = editable;
            RefreshMaterial(node);
            RefreshLight(node);
        }
        _updating = false;
        RefreshHistory();
    }

    private void RefreshMaterial(SceneNode? node)
    {
        bool show = node?.Mesh != null;
        _materialGroup.Visible = show;
        if (!show) return;
        _materialPick.Clear();
        _materialPick.AddItem("lambert1", 0);
        foreach (var m in _doc.Materials) _materialPick.AddItem($"{m.Name}  [{m.Type}]", m.Id);
        int idx = _materialPick.GetItemIndex(node!.MaterialId);
        _materialPick.Selected = idx >= 0 ? idx : 0;
        var def = _doc.FindMaterial(node.MaterialId);
        _materialProps.Visible = def != null;
        _materialNote.Visible = def == null;
        _materialProps.SetMaterial(def?.Id ?? 0);
    }

    private int HistoryCount => _doc.Find(_node)?.MeshShape?.History.Count ?? 0;

    private void RefreshHistory()
    {
        var entries = _doc.Find(_node)?.MeshShape?.History;
        int prevSel = _historyIndex;
        _history.Clear();
        if (entries == null || entries.Count == 0)
        {
            _history.Visible = false; _historyEmpty.Visible = true; _historyIndex = -1; RefreshParams(); return;
        }
        _history.Visible = true; _historyEmpty.Visible = false;
        for (int i = entries.Count - 1; i >= 0; i--)
        {
            var e = entries[i];
            string label = e.Name + (e.Editable ? "  (" + string.Join(", ", e.Params.Items.Select(p => p.Name + "=" + FormatParam(p))) + ")" : "");
            _history.AddItem(label);
        }
        if (prevSel < 0 || prevSel >= entries.Count) _historyIndex = entries.Count - 1;
        _history.Select(entries.Count - 1 - _historyIndex);
        RefreshParams();
    }

    private static string FormatParam(HistoryParam p) => p.Kind switch
    {
        HistoryParamKind.Vector3 => $"{p.Value.X:0.###},{p.Value.Y:0.###},{p.Value.Z:0.###}",
        HistoryParamKind.Int => p.Int.ToString(),
        HistoryParamKind.Bool => p.Bool ? "on" : "off",
        _ => p.Float.ToString("0.###"),
    };

    private void RefreshParams()
    {
        foreach (var c in _paramControls) c.QueueFree();
        _paramControls.Clear();
        var entries = _doc.Find(_node)?.MeshShape?.History;
        if (entries == null || _historyIndex < 0 || _historyIndex >= entries.Count) return;
        var entry = entries[_historyIndex];
        float s = CubeApp.Instance.UiScale;
        if (!entry.Editable)
        {
            var l = new Label { Text = $"{entry.Name}: no editable parameters", Modulate = new Color(1, 1, 1, 0.6f) };
            _paramBox.AddChild(l); _paramControls.Add(l);
            return;
        }
        int entryIndex = _historyIndex;
        foreach (var p in entry.Params.Items)
        {
            var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            row.AddChild(new Label { Text = p.Name, CustomMinimumSize = new Vector2(70 * s, 0) });
            int comps = p.Kind == HistoryParamKind.Vector3 ? 3 : 1;
            for (int c = 0; c < comps; c++)
            {
                var sb = Spin(s);
                sb.Step = p.Kind == HistoryParamKind.Int ? 1 : p.Step;
                sb.MinValue = Math.Max(p.Min, -1e9); sb.MaxValue = Math.Min(p.Max, 1e9);
                sb.AllowGreater = p.Max >= 1e9; sb.AllowLesser = p.Min <= -1e9;
                sb.Value = c == 0 ? p.Value.X : c == 1 ? p.Value.Y : p.Value.Z;
                string pname = p.Name; int comp = c;
                sb.ValueChanged += v => OnParamChanged(entryIndex, pname, comp, (float)v);
                row.AddChild(sb);
            }
            _paramBox.AddChild(row); _paramControls.Add(row);
        }
    }

    private void OnParamChanged(int entryIndex, string name, int comp, float value)
    {
        if (_updating) return;
        var shape = _doc.Find(_node)?.MeshShape;
        if (shape == null || entryIndex < 0 || entryIndex >= shape.History.Count) return;
        var entry = shape.History[entryIndex];
        var np = entry.Params.Clone();
        var p = np[name];
        var v = p.Value;
        if (comp == 0) v.X = value; else if (comp == 1) v.Y = value; else v.Z = value;
        p.Value = v;
        if (np.ValuesEqual(entry.Params)) return;
        _historyIndex = entryIndex;
        _doc.Undo.Push(new EditHistoryCommand(_node, entryIndex, np, $"Edit {entry.Name}"));
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
        NVec3 v = row == 0 ? after.Translation : row == 1 ? after.RotationDegrees : row == 2 ? after.Scale : after.Pivot;
        if (col == 0) v.X = value; else if (col == 1) v.Y = value; else v.Z = value;
        if (row == 0) after.Translation = v; else if (row == 1) after.RotationDegrees = v; else if (row == 2) after.Scale = v;
        else after = before.WithPivotKeepingMatrix(v); // 피벗 편집은 월드를 유지한다(Maya 피벗 이동과 같음)
        if (after == before) return;
        // 가운데 버튼 드래그(SpinDrag) 한 번의 변경들은 Undo 한 단계로 합친다: 같은 드래그의 직전 명령을 빼고 처음 값부터의 명령으로 바꾼다
        if (SpinDrag.ActiveDrag != 0 && SpinDrag.ActiveDrag == _dragId && _dragCmd != null && ReferenceEquals(_doc.Undo.LastCommand, _dragCmd) && _dragCmd.Ids.Count == 1 && _dragCmd.Ids[0] == node.Id)
        {
            var start = _dragCmd.Before[0];
            _doc.Undo.Undo();
            before = start;
        }
        node.Local = after;
        _doc.Notify(new DocChange(ChangeKind.TransformChanged, node.Id));
        var cmd = new TransformNodesCommand("Set Attribute", new[] { node.Id }, new[] { before }, new[] { after });
        _doc.Undo.Push(cmd, alreadyApplied: true);
        _dragId = SpinDrag.ActiveDrag; _dragCmd = SpinDrag.ActiveDrag != 0 ? cmd : null;
    }
}
