using Cube.App.Viewport;
using Godot;

namespace Cube.App.UI;

/// <summary>
/// Render → Render Settings...: IBL(내장 HDRI 10개 또는 파일, 세기/회전/배경 표시), 톤 매핑/노출, 헤드라이트/그림자/SSAO/MSAA/FXAA.
/// 값을 바꾸면 바로 Settings에 저장하고 모든 뷰포트 패널에 적용한다(<see cref="ViewportPanel.ApplyRenderSettings"/>).
/// </summary>
public partial class RenderSettingsWindow : FloatingPanel
{
    private Shell _shell = null!;
    private CheckBox _ibl = null!, _showBg = null!, _headlight = null!, _shadows = null!, _ssao = null!, _fxaa = null!;
    private OptionButton _hdri = null!, _tonemap = null!, _msaa = null!;
    private SpinBox _intensity = null!, _rotation = null!, _exposure = null!;
    private Label _custom = null!;
    private bool _building;

    private static RenderSettings R => CubeApp.Instance.Settings.Render;

    public void Setup(Shell shell)
    {
        _shell = shell;
        float s = CubeApp.Instance.UiScale;
        Title = "Render Settings";
        Size = new Vector2(460 * s, 520 * s);
        MinPanelSize = new Vector2(380 * s, 360 * s);
        _building = true;

        var scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        Content.AddChild(scroll);
        var box = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        box.AddThemeConstantOverride("separation", (int)(6 * s));
        scroll.AddChild(box);

        GridContainer Group(string title)
        {
            var header = new Label { Text = title };
            header.AddThemeColorOverride("font_color", MayaTheme.TextDim);
            box.AddChild(header);
            box.AddChild(new HSeparator());
            var g = new GridContainer { Columns = 2, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            g.AddThemeConstantOverride("h_separation", (int)(12 * s));
            g.AddThemeConstantOverride("v_separation", (int)(6 * s));
            box.AddChild(g);
            return g;
        }
        CheckBox Check(GridContainer g, string label, bool value, Action<bool> set)
        {
            g.AddChild(new Label { Text = label });
            var c = new CheckBox { ButtonPressed = value, FocusMode = Control.FocusModeEnum.None };
            c.Toggled += v => { if (!_building) { set(v); Apply(); } };
            g.AddChild(c);
            return c;
        }
        SpinBox Spin(GridContainer g, string label, double min, double max, double step, double value, Action<float> set, string suffix = "")
        {
            g.AddChild(new Label { Text = label });
            var sb = new SpinBox { MinValue = min, MaxValue = max, Step = step, Value = value, Suffix = suffix, CustomMinimumSize = new Vector2(140 * s, 0) };
            sb.ValueChanged += v => { if (!_building) { set((float)v); Apply(); } };
            g.AddChild(sb);
            return sb;
        }
        OptionButton Option(GridContainer g, string label, string[] items, int selected, Action<int> set)
        {
            g.AddChild(new Label { Text = label });
            var ob = new OptionButton { FocusMode = Control.FocusModeEnum.None, CustomMinimumSize = new Vector2(200 * s, 0) };
            foreach (var it in items) ob.AddItem(it);
            ob.Selected = Math.Clamp(selected, 0, items.Length - 1);
            ob.ItemSelected += i => { if (!_building) { set((int)i); Apply(); } };
            g.AddChild(ob);
            return ob;
        }

        // ---- IBL
        var ibl = Group("Image Based Lighting (IBL)");
        _ibl = Check(ibl, "Enable IBL", R.IblEnabled, v => R.IblEnabled = v);
        var names = HdriLibrary.BuiltIn.Select(b => b.label).Append("Custom file...").ToArray();
        int cur = R.Hdri == HdriLibrary.Custom ? names.Length - 1 : Math.Max(0, Array.FindIndex(HdriLibrary.BuiltIn, b => b.id == R.Hdri));
        _hdri = Option(ibl, "HDRI", names, cur, i =>
        {
            if (i == names.Length - 1) { if (string.IsNullOrEmpty(R.HdriPath)) BrowseHdr(); else R.Hdri = HdriLibrary.Custom; }
            else R.Hdri = HdriLibrary.BuiltIn[i].id;
        });
        ibl.AddChild(new Label { Text = "Custom file" });
        var fileRow = new HBoxContainer();
        _custom = new Label { Text = System.IO.Path.GetFileName(R.HdriPath ?? "") , SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipText = true, CustomMinimumSize = new Vector2(120 * s, 0) };
        fileRow.AddChild(_custom);
        var browse = new Button { Text = "Browse...", FocusMode = Control.FocusModeEnum.None };
        browse.Pressed += BrowseHdr;
        fileRow.AddChild(browse);
        ibl.AddChild(fileRow);
        _intensity = Spin(ibl, "Intensity", 0, 8, 0.05, R.IblIntensity, v => R.IblIntensity = v);
        _rotation = Spin(ibl, "Rotation", -360, 360, 5, R.IblRotation, v => R.IblRotation = v, "°");
        _showBg = Check(ibl, "Show HDRI as background", R.ShowBackground, v => R.ShowBackground = v);
        var note = new Label { Text = "Built-in HDRIs: Poly Haven, CC0 (1k). IBL affects Shaded/Textured/Lit modes.", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        note.AddThemeColorOverride("font_color", MayaTheme.TextDim);
        box.AddChild(note);

        // ---- Lighting
        var light = Group("Lighting");
        _headlight = Check(light, "Headlight (camera light)", R.Headlight, v => R.Headlight = v);
        _shadows = Check(light, "Shadows (scene lights, Lit mode)", R.Shadows, v => R.Shadows = v);
        _ssao = Check(light, "Ambient occlusion (SSAO)", R.Ssao, v => R.Ssao = v);

        // ---- Color
        var color = Group("Color Management");
        _tonemap = Option(color, "Tone mapping", new[] { "Linear", "Reinhard", "Filmic", "ACES", "AgX" }, R.Tonemap, i => R.Tonemap = i);
        _exposure = Spin(color, "Exposure", 0.05, 8, 0.05, R.Exposure, v => R.Exposure = v);

        // ---- Quality
        var q = Group("Anti-aliasing");
        _msaa = Option(q, "MSAA", new[] { "Off", "2x", "4x", "8x" }, R.Msaa, i => R.Msaa = i);
        _fxaa = Check(q, "FXAA", R.Fxaa, v => R.Fxaa = v);

        var buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        var reset = new Button { Text = "Reset to Defaults", FocusMode = Control.FocusModeEnum.None };
        reset.Pressed += () => { CubeApp.Instance.Settings.Render = new RenderSettings(); Apply(); Rebuild(); };
        buttons.AddChild(reset);
        box.AddChild(buttons);
        _building = false;
    }

    private void BrowseHdr()
    {
        var fd = new FileDialog { FileMode = FileDialog.FileModeEnum.OpenFile, Access = FileDialog.AccessEnum.Filesystem, UseNativeDialog = true, Title = "Choose HDRI" };
        fd.AddFilter("*.hdr,*.exr,*.png,*.jpg,*.jpeg,*.webp", "HDRI / image");
        _shell.AddChild(fd);
        fd.FileSelected += p =>
        {
            HdriLibrary.Forget(p);
            R.HdriPath = p; R.Hdri = HdriLibrary.Custom;
            _custom.Text = System.IO.Path.GetFileName(p);
            _building = true; _hdri.Selected = _hdri.ItemCount - 1; _building = false;
            Apply(); fd.QueueFree();
        };
        fd.Canceled += () => { Rebuild(); fd.QueueFree(); };
        fd.PopupCentered();
    }

    /// <summary>Settings에서 컨트롤 값을 다시 읽는다(메뉴 토글 등 바깥에서 바뀌었을 때).</summary>
    public void Rebuild()
    {
        _building = true;
        _ibl.ButtonPressed = R.IblEnabled; _showBg.ButtonPressed = R.ShowBackground; _headlight.ButtonPressed = R.Headlight;
        _shadows.ButtonPressed = R.Shadows; _ssao.ButtonPressed = R.Ssao; _fxaa.ButtonPressed = R.Fxaa;
        _hdri.Selected = R.Hdri == HdriLibrary.Custom ? _hdri.ItemCount - 1 : Math.Max(0, Array.FindIndex(HdriLibrary.BuiltIn, b => b.id == R.Hdri));
        _custom.Text = System.IO.Path.GetFileName(R.HdriPath ?? "");
        _intensity.Value = R.IblIntensity; _rotation.Value = R.IblRotation; _exposure.Value = R.Exposure;
        _tonemap.Selected = R.Tonemap; _msaa.Selected = R.Msaa;
        _building = false;
    }

    private void Apply() => _shell.ApplyRenderSettings();
}
