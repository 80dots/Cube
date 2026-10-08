using Cube.Core.Commands;
using Cube.Core.Scene;
using Godot;
using GColor = Godot.Color;
using NVec3 = System.Numerics.Vector3;

namespace Cube.App.UI;

/// <summary>
/// 머티리얼 하나의 속성 편집기. <see cref="MaterialParams"/> 표에서 현재 타입에 해당하는 파라미터를 그룹별로 보여 주고,
/// 텍스처 가능한 파라미터마다 텍스처 버튼(Load / ✕ Clear)을 둔다. glTF 확장 그룹(Clearcoat, Transmission/Volume, Specular, Sheen, Iridescence,
/// Anisotropy)과 Alpha/Texture Transform은 접혀 있다가 제목을 눌러 펼친다(접힘 상태는 세션 동안 기억).
/// Material Editor와 Properties 패널이 공유한다. 모든 변경은 SetMaterialCommand로 Undo 가능하며, 숫자 칸 가운데 버튼 드래그·색 고르기 중에는 미리보기만 한다.
/// </summary>
public partial class MaterialPropsEditor : VBoxContainer
{
    private Shell _shell = null!;
    private LineEdit _name = null!;
    private OptionButton _type = null!;
    private Control _nameRow = null!;
    private VBoxContainer _body = null!;
    private bool _updating;
    private (int id, MaterialType type)? _built;
    private readonly Dictionary<string, Action<MaterialDef>> _updaters = new();
    private static readonly HashSet<string> Expanded = new() { MaterialParams.GBase, MaterialParams.GSurface, MaterialParams.GEmission };

    /// <summary>편집 대상 머티리얼 ID(0 = 없음/기본 lambert1).</summary>
    public int MaterialId { get; private set; }
    public bool ShowName { get => _nameRow.Visible; set => _nameRow.Visible = value; }

    public void Setup(Shell shell)
    {
        _shell = shell;
        float s = CubeApp.Instance.UiScale;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        AddThemeConstantOverride("separation", (int)(4 * s));

        _name = new LineEdit { PlaceholderText = "name" };
        _name.TextSubmitted += t => { Commit(m => m.Name = string.IsNullOrWhiteSpace(t) ? m.Name : t.Trim()); CallDeferred(nameof(ReturnFocus)); };
        _nameRow = Row("Name", _name, null, s); AddChild(_nameRow);
        _type = new OptionButton { FocusMode = FocusModeEnum.None };
        foreach (var t in Enum.GetNames<MaterialType>()) _type.AddItem(t);
        _type.ItemSelected += i => Commit(m => m.Type = (MaterialType)(int)i);
        AddChild(Row("Type", _type, null, s));
        _body = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _body.AddThemeConstantOverride("separation", (int)(3 * s));
        AddChild(_body);
        Refresh();
    }

    private static void ReturnFocus() => Shell.Instance?.Viewport.GrabFocus();
    private static NVec3 ToVec(GColor c) => new(c.R, c.G, c.B);
    private static GColor ToColor(NVec3 v) => new(v.X, v.Y, v.Z);

    private static Control Row(string label, Control c, Control? tail, float s, string? tip = null)
    {
        var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddChild(new Label { Text = label, CustomMinimumSize = new Godot.Vector2(110 * s, 0), ClipText = true, TooltipText = tip ?? label, MouseFilter = MouseFilterEnum.Pass });
        c.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        row.AddChild(c);
        if (tail != null) row.AddChild(tail);
        return row;
    }

    private void NotifyUsers(int id)
    {
        foreach (var n in _shell.Document.Nodes.Values) if (n.MaterialId == id) _shell.Document.Notify(new DocChange(ChangeKind.MaterialChanged, n.Id));
    }

    /// <summary>미리보기: 문서 머티리얼에 직접 쓰고 사용자에게 통지(명령 없음). 확정은 Commit.</summary>
    private void Preview(Action<MaterialDef> change)
    {
        if (_updating) return;
        var m = _shell.Document.FindMaterial(MaterialId); if (m == null) return;
        change(m);
        NotifyUsers(MaterialId);
    }

    /// <summary>변경을 명령으로 기록한다. 미리보기로 이미 바뀐 값은 원래대로 되돌린 뒤 명령이 적용한다.</summary>
    private void Commit(Action<MaterialDef> change)
    {
        if (_updating) return;
        var m = _shell.Document.FindMaterial(MaterialId); if (m == null) return;
        var after = m.Clone(); change(after);
        if (_original != null && _original.Id == m.Id) m.CopyFrom(_original);
        if (after.ValuesEqual(m)) { NotifyUsers(MaterialId); return; }
        _shell.Document.Undo.Push(new SetMaterialCommand(MaterialId, after));
    }

    private MaterialDef? _original;
    private Action? _pendingCommit;

    public override void _Process(double delta)
    {
        // 숫자 칸 가운데 버튼 드래그가 끝나면 미리보기 값을 한 번에 확정
        if (_pendingCommit != null && SpinDrag.ActiveDrag == 0) { var c = _pendingCommit; _pendingCommit = null; c(); }
    }

    private void PickImage(string title, Action<string> onPicked)
    {
        var dlg = new FileDialog { FileMode = FileDialog.FileModeEnum.OpenFile, Access = FileDialog.AccessEnum.Filesystem, UseNativeDialog = true, Title = title };
        dlg.Filters = new[] { "*.png, *.jpg, *.jpeg, *.bmp, *.tga, *.webp, *.exr, *.hdr ; Images" };
        dlg.FileSelected += path => { onPicked(path); dlg.QueueFree(); };
        dlg.Canceled += () => dlg.QueueFree();
        _shell.AddChild(dlg);
        dlg.PopupCentered();
    }

    public void SetMaterial(int id) { MaterialId = id; Refresh(); }

    /// <summary>텍스처를 할당할 때 glTF처럼 값(계수)이 텍스처를 곱하므로, 0이거나 검정이면 1/흰색으로 바꿔 텍스처가 그대로 보이게 한다.</summary>
    public static void AssignTexture(MaterialDef m, MatParamInfo p, string? path)
    {
        m.SetTex(p.Key, path);
        if (path == null) return;
        var v = m.Get(p.Key);
        if (p.Kind == MatParamKind.Color && (p.Key == "color" || v == NVec3.Zero)) m.Set(p.Key, NVec3.One);
        else if (p.Kind == MatParamKind.Float && v.X == 0f) m.Set(p.Key, MathF.Min(1f, p.Max));
    }

    private void Build(MaterialDef m)
    {
        float s = CubeApp.Instance.UiScale;
        foreach (var c in _body.GetChildren()) { _body.RemoveChild(c); c.QueueFree(); }
        _updaters.Clear();
        string? group = null; VBoxContainer? groupBox = null;
        var groups = MaterialParams.For(m.Type).GroupBy(p => p.Group).ToList();
        foreach (var g in groups)
        {
            group = g.Key;
            var header = new Button { Flat = true, Alignment = HorizontalAlignment.Left, FocusMode = FocusModeEnum.None, Text = (Expanded.Contains(group) ? "▾ " : "▸ ") + group, ClipText = true, TooltipText = group };
            header.AddThemeColorOverride("font_color", MayaTheme.TextDim);
            groupBox = new VBoxContainer { Visible = Expanded.Contains(group) };
            string gname = group; var box = groupBox;
            header.Pressed += () => { if (!Expanded.Remove(gname)) Expanded.Add(gname); box.Visible = Expanded.Contains(gname); header.Text = (box.Visible ? "▾ " : "▸ ") + gname; };
            _body.AddChild(header);
            _body.AddChild(groupBox);
            foreach (var p in g) groupBox.AddChild(ParamRow(p, s));
        }
        if (m.Type == MaterialType.Matcap) _body.AddChild(MatcapRow(s));
    }

    private Control ParamRow(MatParamInfo p, float s)
    {
        Control ctl;
        switch (p.Kind)
        {
            case MatParamKind.Color:
                {
                    var cp = new ColorPickerButton { EditAlpha = false, CustomMinimumSize = new Godot.Vector2(0, 22 * s) };
                    cp.ColorChanged += c => Preview(m => m.Set(p.Key, ToVec(c)));
                    cp.PopupClosed += () => Commit(m => m.Set(p.Key, ToVec(cp.Color)));
                    _updaters[p.Key] = m => cp.Color = ToColor(m.Get(p.Key));
                    ctl = cp; break;
                }
            case MatParamKind.Bool:
                {
                    var cb = new CheckBox { FocusMode = FocusModeEnum.None };
                    cb.Toggled += on => Commit(m => m.Set(p.Key, on ? 1f : 0f));
                    _updaters[p.Key] = m => cb.SetPressedNoSignal(m.GetF(p.Key) > 0.5f);
                    ctl = cb; break;
                }
            case MatParamKind.Enum:
                {
                    var ob = new OptionButton { FocusMode = FocusModeEnum.None };
                    foreach (var c in p.Choices ?? Array.Empty<string>()) ob.AddItem(c);
                    ob.ItemSelected += i => Commit(m => m.Set(p.Key, (float)i));
                    _updaters[p.Key] = m => ob.Selected = Math.Clamp((int)m.GetF(p.Key), 0, ob.ItemCount - 1);
                    ctl = ob; break;
                }
            default:
                {
                    double range = p.Max - p.Min;
                    var sb = new SpinBox { MinValue = p.Min, MaxValue = p.Max, Step = range <= 1.01 ? 0.01 : range <= 20 ? 0.05 : 1, CustomMinimumSize = new Godot.Vector2(80 * s, 0), SelectAllOnFocus = true };
                    sb.ValueChanged += v =>
                    {
                        if (_updating) return;
                        if (SpinDrag.ActiveDrag != 0) { Preview(m => m.Set(p.Key, (float)v)); _pendingCommit = () => Commit(m => m.Set(p.Key, (float)sb.Value)); }
                        else Commit(m => m.Set(p.Key, (float)v));
                    };
                    _updaters[p.Key] = m => sb.SetValueNoSignal(m.GetF(p.Key));
                    ctl = sb; break;
                }
        }
        Control? tail = null;
        if (p.Texturable)
        {
            var box = new HBoxContainer();
            var btn = new Button { FocusMode = FocusModeEnum.None, ClipText = true, CustomMinimumSize = new Godot.Vector2(64 * s, 0), TooltipText = "Load a texture for " + p.Label };
            btn.Pressed += () => PickImage("Texture: " + p.Label, path => Commit(m => AssignTexture(m, p, path)));
            var clear = new Button { Text = "✕", FocusMode = FocusModeEnum.None, TooltipText = "Remove the texture" };
            clear.Pressed += () => Commit(m => m.SetTex(p.Key, null));
            box.AddChild(btn); box.AddChild(clear);
            var prev = _updaters[p.Key];
            _updaters[p.Key] = m =>
            {
                prev(m);
                var t = m.Tex(p.Key);
                btn.Text = t == null ? "Map…" : System.IO.Path.GetFileName(t);
                btn.TooltipText = t ?? "Load a texture for " + p.Label;
                btn.Modulate = t == null ? new GColor(1, 1, 1, 0.75f) : new GColor(0.75f, 0.9f, 1f);
                clear.Visible = t != null;
            };
            tail = box;
        }
        string tip = p.Label + (p.Extension != null ? $"  [{p.Extension}]" : "") + (p.Tip != null ? "\n" + p.Tip : "") + (!p.Texturable && p.Kind is MatParamKind.Float or MatParamKind.Color ? "\n(glTF has no texture slot for this value)" : "");
        return Row(p.Label, ctl, tail, s, tip);
    }

    private Control MatcapRow(float s)
    {
        var box = new HBoxContainer();
        var lbl = new Label { Text = "(built-in)", SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipText = true };
        box.AddChild(lbl);
        var load = new Button { Text = "Load...", FocusMode = FocusModeEnum.None };
        load.Pressed += () => PickImage("Load Matcap Image", path => Commit(m => m.MatcapPath = path));
        box.AddChild(load);
        var builtIn = new Button { Text = "Built-in", FocusMode = FocusModeEnum.None };
        builtIn.Pressed += () => Commit(m => m.MatcapPath = null);
        box.AddChild(builtIn);
        _updaters["__matcap"] = m => lbl.Text = string.IsNullOrEmpty(m.MatcapPath) ? "(built-in)" : System.IO.Path.GetFileName(m.MatcapPath);
        return Row("Matcap", box, null, s);
    }

    public void Refresh()
    {
        var m = _shell.Document.FindMaterial(MaterialId);
        _updating = true;
        bool has = m != null;
        _name.Editable = has; _type.Disabled = !has;
        if (m != null)
        {
            _original = m.Clone();
            _name.Text = m.Name; _type.Selected = (int)m.Type;
            if (_built != (m.Id, m.Type)) { Build(m); _built = (m.Id, m.Type); }
            foreach (var u in _updaters.Values) u(m);
            _body.Visible = true;
        }
        else { _original = null; _name.Text = ""; _body.Visible = false; _built = null; }
        _updating = false;
    }
}
