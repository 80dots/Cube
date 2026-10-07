using Cube.Core.Commands;
using Cube.Core.Scene;
using Godot;
using GColor = Godot.Color;
using NVec3 = System.Numerics.Vector3;

namespace Cube.App.UI;

/// <summary>
/// 머티리얼 하나의 속성 편집기(Name/Type/Color/Specular/Shininess/Metallic/Roughness/Matcap/Texture).
/// Material Editor와 Properties 패널이 공유한다. 모든 변경은 SetMaterialCommand로 Undo 가능하며, 슬라이더 드래그 중에는 미리보기만 한다.
/// </summary>
public partial class MaterialPropsEditor : VBoxContainer
{
    private Shell _shell = null!;
    private LineEdit _name = null!;
    private OptionButton _type = null!;
    private ColorPickerButton _color = null!, _specular = null!;
    private HSlider _shininess = null!, _metallic = null!, _roughness = null!;
    private Label _matcapLabel = null!, _texLabel = null!;
    private Control _nameRow = null!, _blinnRow = null!, _pbrRow = null!, _matcapRow = null!, _specRow = null!;
    private bool _updating;

    /// <summary>편집 대상 머티리얼 ID(0 = 없음/기본 lambert1).</summary>
    public int MaterialId { get; private set; }
    public bool ShowName { get => _nameRow.Visible; set => _nameRow.Visible = value; }

    public void Setup(Shell shell)
    {
        _shell = shell;
        float s = CubeApp.Instance.UiScale;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        AddThemeConstantOverride("separation", (int)(6 * s));

        _name = new LineEdit { PlaceholderText = "name" };
        _name.TextSubmitted += t => { Commit(m => m.Name = string.IsNullOrWhiteSpace(t) ? m.Name : t.Trim()); CallDeferred(nameof(ReturnFocus)); };
        _nameRow = Row("Name", _name, s); AddChild(_nameRow);
        _type = new OptionButton { FocusMode = FocusModeEnum.None, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        foreach (var t in Enum.GetNames<MaterialType>()) _type.AddItem(t);
        _type.ItemSelected += i => Commit(m => m.Type = (MaterialType)(int)i);
        AddChild(Row("Type", _type, s));
        _color = new ColorPickerButton { EditAlpha = false, SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Godot.Vector2(0, 24 * s) };
        _color.ColorChanged += c => PreviewColor(c, specular: false);
        _color.PopupClosed += () => Commit(m => m.Color = ToVec(_color.Color));
        AddChild(Row("Color", _color, s));
        _specular = new ColorPickerButton { EditAlpha = false, SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Godot.Vector2(0, 24 * s) };
        _specular.ColorChanged += c => PreviewColor(c, specular: true);
        _specular.PopupClosed += () => Commit(m => m.Specular = ToVec(_specular.Color));
        _specRow = Row("Specular", _specular, s); AddChild(_specRow);
        _shininess = Slider(1, 128, 1, v => Commit(m => m.Shininess = v));
        _blinnRow = Row("Shininess", _shininess, s); AddChild(_blinnRow);
        _metallic = Slider(0, 1, 0.01, v => Commit(m => m.Metallic = v));
        _pbrRow = new VBoxContainer();
        _pbrRow.AddChild(Row("Metallic", _metallic, s));
        _roughness = Slider(0, 1, 0.01, v => Commit(m => m.Roughness = v));
        _pbrRow.AddChild(Row("Roughness", _roughness, s));
        AddChild(_pbrRow);

        var matcapBox = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _matcapLabel = new Label { Text = "(built-in)", SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipText = true };
        matcapBox.AddChild(_matcapLabel);
        var btnMatcap = new Button { Text = "Load...", FocusMode = FocusModeEnum.None };
        btnMatcap.Pressed += () => PickImage("Load Matcap Image", path => Commit(m => m.MatcapPath = path));
        matcapBox.AddChild(btnMatcap);
        var btnMatcapClear = new Button { Text = "Built-in", FocusMode = FocusModeEnum.None };
        btnMatcapClear.Pressed += () => Commit(m => m.MatcapPath = null);
        matcapBox.AddChild(btnMatcapClear);
        _matcapRow = Row("Matcap", matcapBox, s); AddChild(_matcapRow);

        var texBox = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _texLabel = new Label { Text = "(none)", SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipText = true };
        texBox.AddChild(_texLabel);
        var btnTex = new Button { Text = "Load...", FocusMode = FocusModeEnum.None, TooltipText = "Color (albedo) texture mapped with the mesh UVs" };
        btnTex.Pressed += () => PickImage("Load Color Texture", path => Commit(m => m.TexturePath = path));
        texBox.AddChild(btnTex);
        var btnTexClear = new Button { Text = "Clear", FocusMode = FocusModeEnum.None };
        btnTexClear.Pressed += () => Commit(m => m.TexturePath = null);
        texBox.AddChild(btnTexClear);
        AddChild(Row("Texture", texBox, s));

        Refresh();
    }

    private static void ReturnFocus() => Shell.Instance?.Viewport.GrabFocus();
    private static NVec3 ToVec(GColor c) => new(c.R, c.G, c.B);
    private static GColor ToColor(NVec3 v) => new(v.X, v.Y, v.Z);

    private static Control Row(string label, Control c, float s)
    {
        var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddChild(new Label { Text = label, CustomMinimumSize = new Godot.Vector2(70 * s, 0) });
        c.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        row.AddChild(c);
        return row;
    }

    private HSlider Slider(double min, double max, double step, Action<float> set)
    {
        var sl = new HSlider { MinValue = min, MaxValue = max, Step = step, FocusMode = FocusModeEnum.None, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        sl.DragEnded += changed => { if (changed) set((float)sl.Value); };
        sl.ValueChanged += v => { if (!_updating) PreviewSlider(sl, (float)v); };
        return sl;
    }

    private void NotifyUsers(int id)
    {
        foreach (var n in _shell.Document.Nodes.Values) if (n.MaterialId == id) _shell.Document.Notify(new DocChange(ChangeKind.MaterialChanged, n.Id));
    }

    private void PreviewSlider(HSlider sl, float v)
    {
        var m = _shell.Document.FindMaterial(MaterialId); if (m == null) return;
        if (sl == _shininess) m.Shininess = v; else if (sl == _metallic) m.Metallic = v; else if (sl == _roughness) m.Roughness = v;
        NotifyUsers(MaterialId);
    }

    private void PreviewColor(GColor c, bool specular)
    {
        if (_updating) return;
        var m = _shell.Document.FindMaterial(MaterialId); if (m == null) return;
        if (specular) m.Specular = ToVec(c); else m.Color = ToVec(c);
        NotifyUsers(MaterialId);
    }

    /// <summary>변경을 명령으로 기록한다. 미리보기로 이미 바뀐 값은 원래대로 되돌린 뒤 명령이 적용한다.</summary>
    private void Commit(Action<MaterialDef> change)
    {
        if (_updating) return;
        var m = _shell.Document.FindMaterial(MaterialId); if (m == null) return;
        var after = m.Clone(); change(after);
        if (_original != null && _original.Id == m.Id) { m.CopyFrom(_original); }
        if (after.ValuesEqual(m)) { NotifyUsers(MaterialId); return; }
        _shell.Document.Undo.Push(new SetMaterialCommand(MaterialId, after));
    }

    private MaterialDef? _original;

    private void PickImage(string title, Action<string> onPicked)
    {
        var dlg = new FileDialog { FileMode = FileDialog.FileModeEnum.OpenFile, Access = FileDialog.AccessEnum.Filesystem, UseNativeDialog = true, Title = title };
        dlg.Filters = new[] { "*.png, *.jpg, *.jpeg, *.bmp, *.tga, *.webp ; Images" };
        dlg.FileSelected += path => { onPicked(path); dlg.QueueFree(); };
        dlg.Canceled += () => dlg.QueueFree();
        _shell.AddChild(dlg);
        dlg.PopupCentered();
    }

    public void SetMaterial(int id) { MaterialId = id; Refresh(); }

    public void Refresh()
    {
        var m = _shell.Document.FindMaterial(MaterialId);
        _updating = true;
        bool has = m != null;
        _name.Editable = has; _type.Disabled = !has; _color.Disabled = !has;
        if (m != null)
        {
            _original = m.Clone();
            _name.Text = m.Name; _type.Selected = (int)m.Type; _color.Color = ToColor(m.Color); _specular.Color = ToColor(m.Specular);
            _shininess.Value = m.Shininess; _metallic.Value = m.Metallic; _roughness.Value = m.Roughness;
            _matcapLabel.Text = string.IsNullOrEmpty(m.MatcapPath) ? "(built-in)" : System.IO.Path.GetFileName(m.MatcapPath);
            _texLabel.Text = string.IsNullOrEmpty(m.TexturePath) ? "(none)" : System.IO.Path.GetFileName(m.TexturePath);
            _texLabel.TooltipText = m.TexturePath ?? "";
            _specRow.Visible = _blinnRow.Visible = m.Type == MaterialType.BlinnPhong;
            _pbrRow.Visible = m.Type == MaterialType.Pbr;
            _matcapRow.Visible = m.Type == MaterialType.Matcap;
        }
        else { _original = null; _name.Text = ""; _texLabel.Text = "(none)"; _specRow.Visible = _blinnRow.Visible = _pbrRow.Visible = _matcapRow.Visible = false; }
        _updating = false;
    }
}
