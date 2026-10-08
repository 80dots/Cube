using Cube.Core.Commands;
using Cube.Core.Mesh;
using Cube.Core.Scene;
using Cube.Core.Selection;
using Godot;
using NVec3 = System.Numerics.Vector3;

namespace Cube.App.UI;

/// <summary>
/// Action Popup(Blender "Adjust Last Operation"): 마지막으로 실행한 기능의 이름과 파라미터를 활성 뷰포트 좌하단에 띄우고,
/// 값을 바꾸면 그 기능을 새 값으로 다시 적용한다. 지원하는 경우:
/// ① 옵션이 있는 액션(RegisterOptionPair): 옵션 창 필드 그대로. 바꾸면 마지막 명령을 Undo하고 같은 액션을 새 옵션으로 다시 실행.
/// ② 면 Extrude: 두께(Thickness, 법선 방향 오프셋). 바꾸면 이전 두께 명령을 Undo하고 새 두께 명령을 넣는다.
/// ③ 구성 이력 파라미터가 있는 명령(조작기 이동/회전/스케일, Insert Edge Loop, Crease Tool, Extrude Thickness 드래그 등): EditHistoryCommand로 다시 계산.
/// ④ 오브젝트 이동/회전/스케일(TransformNodesCommand): Move / Rotate / Scale 델타.
/// 그 밖의 명령은 이름만 보여 준다. 선택을 바꾸거나 Undo/Redo로 그 명령이 마지막이 아니게 되면 사라진다.
/// ①의 기능 바로 뒤에 조작기 드래그(Extrude 두께, 컴포넌트 이동/회전/스케일)가 이어지면 그 드래그를 "후속 단계"로 묶어
/// 원래 옵션 필드를 그대로 두고 아래에 드래그 값 필드를 더한다. 옵션을 바꾸면 후속 단계와 기능을 되돌린 뒤 기능을 새 옵션으로 다시 실행하고,
/// 후속 단계를 새 선택(예: 새 캡 면)에 같은 값으로 다시 적용한다. 드래그 값을 바꾸면 그 단계부터 다시 적용한다.
/// </summary>
public partial class ActionPopup : PanelContainer
{
    private Shell _shell = null!;
    private Button _header = null!;
    private GridContainer _grid = null!;
    private Label _note = null!;
    private bool _collapsed;
    private string _title = "";

    private enum Kind { None, Option, Extrude, History, Transform }
    private Kind _kind;
    private ICommand? _command;          // 팝업이 가리키는 마지막 명령(Undo 대상)
    private string? _optionId;           // ① 옵션 키(Options(id))
    private string? _applyId;            // ① 다시 실행할 액션
    private List<(NodeId node, int[] faces)> _extrude = new(); // ② 면 Extrude 결과(캡 면)
    private float _thickness;
    private List<(NodeId node, int index)> _historyEntries = new(); // ③
    private HistoryParams? _historyParams;
    private TransformNodesCommand? _transform; // ④
    private NVec3 _tMove, _tRotate, _tScale = NVec3.One;

    /// <summary>① 기능 뒤에 이어진 조작기 드래그(후속 단계).</summary>
    private sealed class FollowUp
    {
        public string Name = "";
        public HistoryParams Params = new();
        public bool Thickness;                                   // Extrude Thickness(면별 법선 오프셋)
        public readonly List<(NodeId node, ComponentTransformOp? op)> Targets = new();
        public ICommand? Command;                                // 이 단계가 넣은 명령(Undo 대상)
    }
    private readonly List<FollowUp> _follow = new();

    private bool _busy;                  // 팝업이 스스로 명령을 넣는 중
    private (string id, ICommand? last)? _invoking;
    private readonly Dictionary<NodeId, int> _historyCounts = new();
    private bool _rebuildQueued;

    public void Setup(Shell shell)
    {
        _shell = shell;
        float s = CubeApp.Instance.UiScale;
        MouseFilter = MouseFilterEnum.Stop;
        Visible = false;
        ZIndex = 5;
        AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = new Color(0.12f, 0.12f, 0.12f, 0.94f), BorderColor = MayaTheme.Separator, BorderWidthLeft = 1, BorderWidthTop = 1, BorderWidthRight = 1, BorderWidthBottom = 1, CornerRadiusTopLeft = (int)(4 * s), CornerRadiusTopRight = (int)(4 * s), CornerRadiusBottomLeft = (int)(4 * s), CornerRadiusBottomRight = (int)(4 * s), ContentMarginLeft = 6 * s, ContentMarginRight = 6 * s, ContentMarginTop = 4 * s, ContentMarginBottom = 6 * s });
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", (int)(4 * s));
        AddChild(box);
        _header = new Button { Flat = true, Alignment = HorizontalAlignment.Left, FocusMode = FocusModeEnum.None };
        _header.Pressed += () => { _collapsed = !_collapsed; UpdateHeader(); _grid.Visible = !_collapsed && _grid.GetChildCount() > 0; _note.Visible = !_collapsed && _note.Text.Length > 0; ResetSize(); };
        box.AddChild(_header);
        _grid = new GridContainer { Columns = 2 };
        _grid.AddThemeConstantOverride("h_separation", (int)(10 * s));
        _grid.AddThemeConstantOverride("v_separation", (int)(3 * s));
        box.AddChild(_grid);
        _note = new Label { Text = "" };
        _note.AddThemeColorOverride("font_color", MayaTheme.TextDim);
        box.AddChild(_note);

        _shell.Actions.Invoking += id => _invoking = (id, _shell.Document.Undo.LastCommand);
        _shell.Actions.Invoked += OnInvoked;
        _shell.Document.Undo.Changed += OnUndoChanged;
        SnapshotHistoryCounts();
    }

    public override void _Process(double delta)
    {
        if (!Visible) return;
        var vp = _shell.Viewport;
        if (vp == null || !vp.IsVisibleInTree()) return;
        float s = CubeApp.Instance.UiScale;
        var min = GetCombinedMinimumSize();
        if (Size != min) Size = min; // 필드 수가 바뀌면 다음 프레임에 최소 크기가 갱신되므로 매 프레임 맞춘다
        GlobalPosition = vp.GlobalPosition + new Vector2(8 * s, vp.Size.Y - Size.Y - 8 * s);
    }

    // ---------------------------------------------------------------- 컨텍스트 감지

    private void OnInvoked(string id)
    {
        var inv = _invoking; _invoking = null;
        if (_busy) return;
        if (id is "edit.undo" or "edit.redo") { Hide(); SnapshotHistoryCounts(); return; } // Undo/Redo는 조정할 작업이 아니다
        var last = _shell.Document.Undo.LastCommand;
        if (inv == null || last == null || ReferenceEquals(last, inv.Value.last)) { SnapshotHistoryCounts(); return; } // 명령을 만들지 않은 액션(모드 전환 등)
        ShowFor(last, id);
    }

    private void OnUndoChanged()
    {
        if (_busy || _invoking != null) return;
        var last = _shell.Document.Undo.LastCommand;
        if (ReferenceEquals(last, _command)) return;
        // 새 명령이 들어왔는지(Undo/Redo가 아니라): 컨텍스트 명령이 더 이상 마지막이 아니면 우선 숨기고, 새 명령이면 그 명령으로
        if (last == null || last is SelectionCommand || _shell.Document.Undo.RedoCount > 0) { Hide(); SnapshotHistoryCounts(); return; }
        if (Visible && _kind == Kind.Option && TryAppendFollowUp(last)) { SnapshotHistoryCounts(); Rebuild(); return; }
        ShowFor(last, null);
    }

    private void SnapshotHistoryCounts()
    {
        _historyCounts.Clear();
        foreach (var n in _shell.Document.Nodes.Values) if (n.MeshShape is { } ms) _historyCounts[n.Id] = ms.History.Count;
    }

    private List<(NodeId node, int index)> NewHistoryEntries()
    {
        var list = new List<(NodeId, int)>();
        foreach (var n in _shell.Document.Nodes.Values)
        {
            if (n.MeshShape is not { } ms) continue;
            int before = _historyCounts.TryGetValue(n.Id, out var c) ? c : 0;
            for (int i = before; i < ms.History.Count; i++) list.Add((n.Id, i));
        }
        return list;
    }

    private static IEnumerable<ICommand> Flatten(ICommand c)
    {
        if (c is CompoundCommand cc) { foreach (var x in cc.Items) foreach (var y in Flatten(x)) yield return y; }
        else yield return c;
    }

    private void ShowFor(ICommand cmd, string? actionId)
    {
        _command = cmd; _baseCommand = cmd; // 기능 명령 = 후속 단계의 바탕(후속 단계가 없어도 UndoFollowUps가 이 값을 되돌려 준다)
        _kind = Kind.None; _optionId = null; _extrude.Clear(); _historyEntries.Clear(); _historyParams = null; _transform = null; _follow.Clear();
        _title = cmd.Name;
        var newEntries = NewHistoryEntries();
        SnapshotHistoryCounts();

        string? optId = actionId == null ? null : _shell.OptionKeyFor(actionId);
        var flat = Flatten(cmd).ToList();
        if (optId != null)
        {
            _kind = Kind.Option; _optionId = optId; _applyId = actionId; _title = _shell.Actions.Get(actionId!)?.Label ?? cmd.Name;
        }
        else if (flat.Count > 0 && flat.All(c => c is ExtrudeFacesCommand))
        {
            _kind = Kind.Extrude; _thickness = 0f; _title = "Extrude";
            foreach (ExtrudeFacesCommand e in flat) _extrude.Add((e.NodeIdPublic, e.NewFaces.ToArray()));
        }
        else if (flat.Count == 1 && flat[0] is TransformNodesCommand tn)
        {
            _kind = Kind.Transform; _transform = tn; _tMove = NVec3.Zero; _tRotate = NVec3.Zero; _tScale = NVec3.One;
            if (tn.Ids.Count > 0)
            {
                _tMove = tn.After[0].Translation - tn.Before[0].Translation;
                _tRotate = tn.After[0].RotationDegrees - tn.Before[0].RotationDegrees;
                var b = tn.Before[0].Scale; var a = tn.After[0].Scale;
                _tScale = new NVec3(Ratio(a.X, b.X), Ratio(a.Y, b.Y), Ratio(a.Z, b.Z));
            }
        }
        else
        {
            var editable = newEntries.Where(e => _shell.Document.Find(e.node)?.MeshShape is { } ms && e.index < ms.History.Count && ms.History[e.index].Editable).ToList();
            if (editable.Count > 0)
            {
                _kind = Kind.History; _historyEntries = editable;
                var first = _shell.Document.Get(editable[0].node).MeshShape!.History[editable[0].index];
                _historyParams = first.Params.Clone();
                _title = first.Name;
            }
        }
        Rebuild();
        Visible = true;
    }

    private static float Ratio(float a, float b) => MathF.Abs(b) > 1e-8f ? a / b : 1f;

    private new void Hide() { Visible = false; _command = null; _kind = Kind.None; }

    /// <summary>DebugDriver: 라벨이 label인 필드(벡터면 axis 0/1/2 성분)를 value로 바꾼다.</summary>
    public bool DebugSet(string label, float value, int axis = 0)
    {
        var kids = _grid.GetChildren();
        for (int i = 0; i + 1 < kids.Count; i += 2)
        {
            if (kids[i] is not Label l || !string.Equals(l.Text, label, StringComparison.OrdinalIgnoreCase)) continue;
            switch (kids[i + 1])
            {
                case SpinBox sb: sb.Value = value; return true;
                case CheckBox cb: cb.ButtonPressed = value > 0.5f; return true;
                case OptionButton ob: ob.Select((int)value); ob.EmitSignal(OptionButton.SignalName.ItemSelected, (int)value); return true;
                case HBoxContainer row: if (row.GetChild(axis) is SpinBox s2) { s2.Value = value; return true; } break;
            }
        }
        return false;
    }

    /// <summary>DebugDriver print용 요약.</summary>
    public string DebugSummary()
    {
        if (!Visible) return "popup=(hidden)";
        var kids = _grid.GetChildren(); var parts = new List<string>();
        for (int i = 0; i + 1 < kids.Count; i += 2)
        {
            string val = kids[i + 1] switch { SpinBox sb => sb.Value.ToString("0.###"), CheckBox cb => cb.ButtonPressed.ToString(), OptionButton ob => ob.Selected.ToString(), HBoxContainer row => string.Join(",", row.GetChildren().OfType<SpinBox>().Select(x => x.Value.ToString("0.###"))), _ => "?" };
            parts.Add($"{(kids[i] as Label)?.Text}={val}");
        }
        return $"popup=[{_title}] {string.Join(" ", parts)}";
    }

    // ---------------------------------------------------------------- UI

    private void UpdateHeader() => _header.Text = (_collapsed ? "▸ " : "▾ ") + _title;

    private void Rebuild()
    {
        _rebuildQueued = false;
        foreach (var c in _grid.GetChildren()) { _grid.RemoveChild(c); c.QueueFree(); } // 즉시 빼야 이전 필드 크기가 남지 않는다
        _note.Text = "";
        float s = CubeApp.Instance.UiScale;
        switch (_kind)
        {
            case Kind.Option:
                {
                    var spec = _shell.OptionSpecFor(_optionId!);
                    var values = _shell.Options(_optionId!);
                    if (spec != null)
                        foreach (var f in spec.Fields)
                        {
                            var field = f;
                            AddField(field.Label, field.Kind switch
                            {
                                OptionField.FieldKind.Bool => FieldType.Bool, OptionField.FieldKind.Int => FieldType.Int,
                                OptionField.FieldKind.Enum => FieldType.Enum, OptionField.FieldKind.Vector3 => FieldType.Vec3, _ => FieldType.Float,
                            }, field.Min, field.Max, field.Step, field.Choices,
                            () => field.Kind == OptionField.FieldKind.Vector3 ? Conv(values.Vec(field.Key)) : new NVec3(values.Float(field.Key), 0, 0),
                            v => { if (field.Kind == OptionField.FieldKind.Vector3) values.Set(field.Key, new Vector3(v.X, v.Y, v.Z)); else values.Set(field.Key, v.X); ReapplyOption(); }, s);
                        }
                    // 후속 단계(조작기 드래그) 값
                    for (int i = 0; i < _follow.Count; i++)
                    {
                        var fu = _follow[i]; int index = i;
                        foreach (var p in fu.Params.Items)
                        {
                            var prm = p;
                            var type = prm.Kind switch { HistoryParamKind.Int => FieldType.Int, HistoryParamKind.Bool => FieldType.Bool, HistoryParamKind.Vector3 => FieldType.Vec3, _ => FieldType.Float };
                            string label = fu.Params.Items.Count == 1 && prm.Name == fu.Name.Split(' ').Last() ? fu.Name : $"{fu.Name}: {prm.Name}";
                            AddField(label, type, prm.Min, prm.Max, prm.Step, null, () => prm.Value, v => { prm.Value = v; ReapplyFollowUps(index); }, s);
                        }
                    }
                    if (_follow.Count > 0) _note.Text = "Then: " + string.Join(", ", _follow.Select(f => f.Name));
                    break;
                }
            case Kind.Extrude:
                AddField("Thickness", FieldType.Float, -1000, 1000, 0.01, null, () => new NVec3(_thickness, 0, 0), v => { _thickness = v.X; ApplyExtrudeThickness(); }, s);
                _note.Text = "Offset along each face normal";
                break;
            case Kind.History:
                foreach (var p in _historyParams!.Items)
                {
                    var prm = p;
                    var type = prm.Kind switch { HistoryParamKind.Int => FieldType.Int, HistoryParamKind.Bool => FieldType.Bool, HistoryParamKind.Vector3 => FieldType.Vec3, _ => FieldType.Float };
                    AddField(prm.Name, type, prm.Min, prm.Max, prm.Step, null, () => prm.Value, v => { prm.Value = v; ApplyHistory(); }, s);
                }
                break;
            case Kind.Transform:
                AddField("Move", FieldType.Vec3, -1e6, 1e6, 0.01, null, () => _tMove, v => { _tMove = v; ApplyTransform(); }, s);
                AddField("Rotate", FieldType.Vec3, -1e6, 1e6, 0.1, null, () => _tRotate, v => { _tRotate = v; ApplyTransform(); }, s);
                AddField("Scale", FieldType.Vec3, -1e6, 1e6, 0.01, null, () => _tScale, v => { _tScale = v; ApplyTransform(); }, s);
                break;
            default:
                _note.Text = "No adjustable parameters";
                break;
        }
        UpdateHeader();
        _grid.Visible = !_collapsed && _kind != Kind.None;
        _note.Visible = !_collapsed && _note.Text.Length > 0;
        Size = Vector2.Zero; ResetSize();
        Callable.From(() => { Size = Vector2.Zero; ResetSize(); }).CallDeferred();
    }

    private static NVec3 Conv(Vector3 v) => new(v.X, v.Y, v.Z);

    private enum FieldType { Float, Int, Bool, Enum, Vec3 }

    private void AddField(string label, FieldType type, double min, double max, double step, string[]? choices, Func<NVec3> get, Action<NVec3> set, float s)
    {
        _grid.AddChild(new Label { Text = label });
        var v = get();
        Control c;
        double lo = Math.Max(min, -1e9), hi = Math.Min(max, 1e9);
        SpinBox Spin(float value, double st) => new() { MinValue = lo, MaxValue = hi, Step = st, Value = value, AllowGreater = max >= 1e9, AllowLesser = min <= -1e9, CustomMinimumSize = new Vector2(90 * s, 0), SelectAllOnFocus = true };
        switch (type)
        {
            case FieldType.Bool:
                {
                    var cb = new CheckBox { ButtonPressed = v.X > 0.5f, FocusMode = FocusModeEnum.None };
                    cb.Toggled += on => Defer(() => set(new NVec3(on ? 1 : 0, 0, 0)));
                    c = cb; break;
                }
            case FieldType.Enum:
                {
                    var ob = new OptionButton { FocusMode = FocusModeEnum.None };
                    foreach (var ch in choices ?? Array.Empty<string>()) ob.AddItem(ch);
                    ob.Selected = Math.Clamp((int)MathF.Round(v.X), 0, Math.Max(0, ob.ItemCount - 1));
                    ob.ItemSelected += i => Defer(() => set(new NVec3(i, 0, 0)));
                    c = ob; break;
                }
            case FieldType.Vec3:
                {
                    var row = new HBoxContainer();
                    var sx = Spin(v.X, step); var sy = Spin(v.Y, step); var sz = Spin(v.Z, step);
                    void Changed(double _) => Defer(() => set(new NVec3((float)sx.Value, (float)sy.Value, (float)sz.Value)));
                    sx.ValueChanged += Changed; sy.ValueChanged += Changed; sz.ValueChanged += Changed;
                    row.AddChild(sx); row.AddChild(sy); row.AddChild(sz);
                    c = row; break;
                }
            default:
                {
                    var sb = Spin(v.X, type == FieldType.Int ? 1 : step);
                    sb.CustomMinimumSize = new Vector2(120 * s, 0);
                    sb.ValueChanged += val => Defer(() => set(new NVec3((float)val, 0, 0)));
                    c = sb; break;
                }
        }
        _grid.AddChild(c);
    }

    /// <summary>스핀박스 연속 변경을 한 프레임에 한 번만 적용한다.</summary>
    private Action? _pendingApply;
    private void Defer(Action a)
    {
        bool queued = _pendingApply != null;
        _pendingApply = a;
        if (!queued) Callable.From(() => { var p = _pendingApply; _pendingApply = null; p?.Invoke(); }).CallDeferred();
    }

    // ---------------------------------------------------------------- 다시 적용

    private bool UndoOwn()
    {
        var undo = _shell.Document.Undo;
        if (_command == null || !ReferenceEquals(undo.LastCommand, _command)) { _shell.HelpLine.Text = "Action Popup: the operation is no longer the last one (selection or another edit changed it)."; Hide(); return false; }
        return undo.Undo();
    }

    private void ReapplyOption()
    {
        if (_optionId == null) return;
        _busy = true;
        try
        {
            if (!UndoFollowUps(0)) return;
            if (!UndoOwn()) return;
            var before = _shell.Document.Undo.LastCommand;
            SnapshotHistoryCounts();
            _shell.Actions.Invoke(_applyId ?? _optionId + "Apply");
            var last = _shell.Document.Undo.LastCommand;
            _command = ReferenceEquals(last, before) ? null : last;
            if (_command != null && _follow.Count > 0) { _baseCommand = _command; RedoFollowUps(0); }
            SnapshotHistoryCounts();
            if (_command == null) Hide();
        }
        finally { _busy = false; }
    }

    private ICommand? _thicknessCmd;

    private void ApplyExtrudeThickness()
    {
        var doc = _shell.Document;
        _busy = true;
        try
        {
            // 이전 두께 명령(팝업이 넣은 것)이 마지막이면 되돌린다
            if (_thicknessCmd != null && ReferenceEquals(doc.Undo.LastCommand, _thicknessCmd)) doc.Undo.Undo();
            else if (!ReferenceEquals(doc.Undo.LastCommand, _command)) { Hide(); return; }
            _thicknessCmd = null;
            if (MathF.Abs(_thickness) < 1e-7f) { _command = doc.Undo.LastCommand; return; }
            using (doc.Undo.BeginGroup("Extrude Thickness"))
                foreach (var (node, faces) in _extrude)
                    if (Tools.ExtrudeThickness.Make(doc, node, faces, _thickness) is { } cmd) doc.Undo.Push(cmd);
            _thicknessCmd = doc.Undo.LastCommand;
            _command = _thicknessCmd;
            SnapshotHistoryCounts();
        }
        finally { _busy = false; }
    }

    private ICommand? _ownEdit;

    private void ApplyHistory()
    {
        var doc = _shell.Document;
        _busy = true;
        try
        {
            if (_ownEdit != null && ReferenceEquals(doc.Undo.LastCommand, _ownEdit)) doc.Undo.Undo();
            else if (!ReferenceEquals(doc.Undo.LastCommand, _command)) { Hide(); return; }
            // EditHistoryCommand는 ID가 바뀔 수 있어 컴포넌트 선택을 비운다. 위상이 그대로면(이동/회전/스케일 등) 선택을 되살린다.
            var selBefore = doc.Selection.Capture();
            var counts = _historyEntries.Select(e => e.node).Distinct().ToDictionary(id => id, id => { var m = doc.Find(id)?.Mesh; return m == null ? (0, 0) : (m.AliveVertexCount, m.AliveFaceCount); });
            using (doc.Undo.BeginGroup("Edit " + _title))
                foreach (var (node, index) in _historyEntries)
                {
                    var ms = doc.Find(node)?.MeshShape; if (ms == null || index >= ms.History.Count) continue;
                    var p = ms.History[index].Params.Clone();
                    foreach (var mine in _historyParams!.Items) { var target = p.Items.FirstOrDefault(x => x.Name == mine.Name); if (target != null) target.Value = mine.Value; }
                    doc.Undo.Push(new EditHistoryCommand(node, index, p, "Edit " + _title));
                }
            _ownEdit = doc.Undo.LastCommand;
            _command = _ownEdit;
            bool same = counts.All(kv => { var m = doc.Find(kv.Key)?.Mesh; return m != null && (m.AliveVertexCount, m.AliveFaceCount) == kv.Value; });
            if (same) doc.Selection.Restore(selBefore);
            SnapshotHistoryCounts();
        }
        finally { _busy = false; }
    }

    private void ApplyTransform()
    {
        var doc = _shell.Document; var tn = _transform; if (tn == null) return;
        _busy = true;
        try
        {
            if (!UndoOwn()) return;
            var after = new Transform3[tn.Ids.Count];
            for (int i = 0; i < after.Length; i++)
            {
                var b = tn.Before[i];
                var t = b; t.Translation = b.Translation + _tMove; t.RotationDegrees = b.RotationDegrees + _tRotate; t.Scale = b.Scale * _tScale;
                after[i] = t;
            }
            var cmd = new TransformNodesCommand(tn.Name, tn.Ids.ToArray(), tn.Before.ToArray(), after);
            doc.Undo.Push(cmd);
            _command = cmd; _transform = cmd;
        }
        finally { _busy = false; }
    }

    // ---------------------------------------------------------------- 후속 단계(기능 뒤 조작기 드래그)

    private ICommand? _baseCommand; // ① 기능이 넣은 명령(후속 단계 아래)

    /// <summary>새 명령이 조작기 드래그(두께/이동/회전/스케일)면 현재 ① 기능의 후속 단계로 붙인다.</summary>
    private bool TryAppendFollowUp(ICommand last)
    {
        var moves = Flatten(last).ToList();
        if (moves.Count == 0 || !moves.All(c => c is MoveVerticesCommand mv && mv.Params != null && (mv.Op != null || mv.Name == "Extrude Thickness"))) return false;
        if (_follow.Count == 0) _baseCommand = _command;
        var first = (MoveVerticesCommand)moves[0];
        var fu = new FollowUp { Name = first.Name, Params = first.Params!.Clone(), Thickness = first.Name == "Extrude Thickness", Command = last };
        foreach (MoveVerticesCommand mv in moves) fu.Targets.Add((mv.Node, mv.Op));
        _follow.Add(fu);
        _command = last;
        return true;
    }

    /// <summary>후속 단계를 from부터 끝까지 되돌린다(각 단계 명령이 차례로 마지막이어야 한다).</summary>
    private bool UndoFollowUps(int from)
    {
        var undo = _shell.Document.Undo;
        for (int i = _follow.Count - 1; i >= from; i--)
        {
            var c = _follow[i].Command;
            if (c == null) continue;
            if (!ReferenceEquals(undo.LastCommand, c)) { _shell.HelpLine.Text = "Action Popup: the operation is no longer the last one."; Hide(); return false; }
            undo.Undo();
            _follow[i].Command = null;
        }
        // 후속 단계가 없으면 _command는 그대로(기능 명령). v0.0.34~46: 여기서 _baseCommand(null)로 덮어써 옵션 변경이 항상 "더 이상 마지막이 아님"으로 숨겨졌다
        if (_follow.Count > 0) _command = from == 0 ? _baseCommand : _follow[from - 1].Command;
        return true;
    }

    /// <summary>후속 단계를 from부터 현재 선택(다시 실행한 기능의 결과)에 같은 값으로 다시 적용한다.</summary>
    private void RedoFollowUps(int from)
    {
        var doc = _shell.Document; var sel = doc.Selection;
        for (int i = from; i < _follow.Count; i++)
        {
            var fu = _follow[i];
            var before = doc.Undo.LastCommand;
            using (doc.Undo.BeginGroup(fu.Name))
                foreach (var (node, op) in fu.Targets)
                {
                    var mesh = doc.Find(node)?.Mesh; if (mesh == null) continue;
                    var comps = sel.GetComponents(node);
                    if (fu.Thickness)
                    {
                        var faces = sel.Mode == SelectMode.Face ? comps.Faces.ToArray() : SelectionOps.Convert(mesh, comps, sel.Mode, SelectMode.Face).ToArray();
                        if (Tools.ExtrudeThickness.Make(doc, node, faces, fu.Params.Float("Thickness")) is { } tc) doc.Undo.Push(tc);
                        continue;
                    }
                    if (op == null) continue;
                    var verts = (sel.Mode == SelectMode.Vertex ? comps.Verts : SelectionOps.Convert(mesh, comps, sel.Mode, SelectMode.Vertex)).Where(v => v >= 0 && v < mesh.VertexCount && mesh.Verts[v].Alive).Distinct().ToArray();
                    if (verts.Length == 0) continue;
                    var mat = op.LocalMatrix(fu.Params);
                    var b = verts.Select(v => mesh.Verts[v].Position).ToArray();
                    var a = b.Select(pp => NVec3.Transform(pp, mat)).ToArray();
                    doc.Undo.Push(new MoveVerticesCommand(fu.Name, node, verts, b, a, op, fu.Params.Clone()));
                }
            var last = doc.Undo.LastCommand;
            fu.Command = ReferenceEquals(last, before) ? null : last;
            if (fu.Command != null) _command = fu.Command;
        }
    }

    /// <summary>후속 단계 i의 값을 바꿨을 때: i부터 되돌리고 다시 적용한다.</summary>
    private void ReapplyFollowUps(int index)
    {
        _busy = true;
        try
        {
            if (!UndoFollowUps(index)) return;
            RedoFollowUps(index);
            SnapshotHistoryCounts();
        }
        finally { _busy = false; }
    }
}

