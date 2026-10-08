using Cube.App.Viewport;
using Godot;

namespace Cube.App.UI;

/// <remarks>
/// 컨트롤 → 설정 방향: 각 컨트롤의 변경 콜백이 <c>Settings.Render</c>(<see cref="R"/>)의 필드를 바꾸고 <see cref="Apply"/>(저장 + 모든 패널 적용)를 부른다.
/// 설정 → 컨트롤 방향: 메뉴/셸프 토글처럼 바깥에서 값이 바뀌면 셸이 <see cref="Rebuild"/>를 불러 다시 읽는다.
/// 두 방향이 서로를 다시 부르지 않도록 <c>_building</c> 동안에는 콜백이 아무것도 하지 않는다.
/// </remarks>
/// <summary>
/// Render → Render Settings...: IBL(내장 HDRI 10개 또는 파일, 세기/회전/배경 표시), 톤 매핑/노출, 헤드라이트/그림자/SSAO/MSAA/FXAA.
/// 값을 바꾸면 바로 Settings에 저장하고 모든 뷰포트 패널에 적용한다(<see cref="ViewportPanel.ApplyRenderSettings"/>).
/// </summary>
public partial class RenderSettingsWindow : FloatingPanel
{
    /// <summary>설정 적용·파일 다이얼로그 부모로 쓰는 셸.</summary>
    private Shell _shell = null!;
    /// <summary>Rebuild에서 직접 다시 읽는 체크박스들(IBL, 배경 표시, 헤드라이트, 그림자, SSAO, FXAA).</summary>
    private CheckBox _ibl = null!, _showBg = null!, _headlight = null!, _shadows = null!, _ssao = null!, _fxaa = null!;
    /// <summary>HDRI 선택(내장 목록 + 마지막 "Custom file..."), 톤 매핑, MSAA 드롭다운.</summary>
    private OptionButton _hdri = null!, _tonemap = null!, _msaa = null!;
    /// <summary>IBL 세기·회전(°), 노출, 배경 흐림 단계 숫자 칸.</summary>
    private SpinBox _intensity = null!, _rotation = null!, _exposure = null!, _blurSpin = null!;
    /// <summary>배경 흐림 단계 슬라이더(숫자 칸과 연동).</summary>
    private HSlider _blurSlider = null!;
    /// <summary>사용자 HDRI 파일 이름 표시.</summary>
    private Label _custom = null!;
    /// <summary>true인 동안은 컨트롤 변경 콜백이 설정을 바꾸지 않는다(초기 구성·Rebuild·프로그램적 값 설정 중).</summary>
    private bool _building;

    /// <summary>현재 렌더 설정 객체(Reset 시 새 인스턴스로 바뀌므로 매번 Settings에서 읽는다).</summary>
    private static RenderSettings R => CubeApp.Instance.Settings.Render;
    /// <summary>Post Effects 컨트롤: Rebuild에서 Settings 값을 다시 읽는 함수들.</summary>
    private readonly List<Action> _refreshers = new();

    /// <summary>
    /// 패널을 구성한다: 스크롤 안의 그룹들(IBL, Lighting, Color Management, Post Effects 5개 그룹, Anti-aliasing)과 Reset 버튼.
    /// 구성 중에는 <c>_building</c>을 켜 두어 초기값 설정이 Apply를 부르지 않게 한다.
    /// </summary>
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

        // 그룹 헬퍼: 흐린 제목 + 구분선 + 2열(라벨/컨트롤) 그리드를 만들어 돌려준다.
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
        // 체크박스 행 헬퍼: 토글되면 set → Apply.
        CheckBox Check(GridContainer g, string label, bool value, Action<bool> set)
        {
            g.AddChild(new Label { Text = label });
            var c = new CheckBox { ButtonPressed = value, FocusMode = Control.FocusModeEnum.None };
            c.Toggled += v => { if (!_building) { set(v); Apply(); } };
            g.AddChild(c);
            return c;
        }
        // 숫자 칸 행 헬퍼: 값이 바뀌면 set → Apply.
        SpinBox Spin(GridContainer g, string label, double min, double max, double step, double value, Action<float> set, string suffix = "")
        {
            g.AddChild(new Label { Text = label });
            var sb = new SpinBox { MinValue = min, MaxValue = max, Step = step, Value = value, Suffix = suffix, CustomMinimumSize = new Vector2(140 * s, 0) };
            sb.ValueChanged += v => { if (!_building) { set((float)v); Apply(); } };
            g.AddChild(sb);
            return sb;
        }
        // 드롭다운 행 헬퍼: 선택 인덱스를 범위로 자르고, 고르면 set → Apply.
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
        // HDRI 목록 = 내장 HDRI 라벨들 + "Custom file...". 현재 선택은 Custom이면 마지막, 아니면 내장 ID 위치.
        var names = HdriLibrary.BuiltIn.Select(b => b.label).Append("Custom file...").ToArray();
        int cur = R.Hdri == HdriLibrary.Custom ? names.Length - 1 : Math.Max(0, Array.FindIndex(HdriLibrary.BuiltIn, b => b.id == R.Hdri));
        _hdri = Option(ibl, "HDRI", names, cur, i =>
        {
            // Custom을 골랐는데 아직 파일이 없으면 파일 고르기 창을 띄우고, 있으면 그 파일로 전환한다.
            if (i == names.Length - 1) { if (string.IsNullOrEmpty(R.HdriPath)) BrowseHdr(); else R.Hdri = HdriLibrary.Custom; }
            else R.Hdri = HdriLibrary.BuiltIn[i].id;
        });
        // 사용자 파일 이름 + Browse 버튼.
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
        // 배경 흐림 0(끔)~9단계: 슬라이더와 숫자 칸이 함께 움직인다(조명은 원본 HDRI 그대로)
        ibl.AddChild(new Label { Text = "Background blur", TooltipText = "Blur the HDRI only where it is shown as the background (0 = sharp, 9 = very blurry). Lighting and reflections keep the sharp HDRI." });
        var blurRow = new HBoxContainer { CustomMinimumSize = new Vector2(200 * s, 0) };
        _blurSlider = new HSlider { MinValue = 0, MaxValue = Core.IO.PanoramaBlur.MaxLevel, Step = 1, Value = R.BackgroundBlur, TickCount = Core.IO.PanoramaBlur.MaxLevel + 1, TicksOnBorders = true, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter, FocusMode = Control.FocusModeEnum.None };
        _blurSpin = new SpinBox { MinValue = 0, MaxValue = Core.IO.PanoramaBlur.MaxLevel, Step = 1, Value = R.BackgroundBlur, Rounded = true };
        // 슬라이더와 숫자 칸 공용 핸들러: 정수 단계로 반올림해 두 컨트롤을 맞춘 뒤(재귀 방지로 _building), 값이 바뀌었을 때만 적용.
        void SetBlur(double v)
        {
            if (_building) return;
            int lv = (int)Math.Round(v);
            _building = true; _blurSlider.Value = lv; _blurSpin.Value = lv; _building = false;
            if (R.BackgroundBlur == lv) return;
            R.BackgroundBlur = lv; Apply();
        }
        _blurSlider.ValueChanged += SetBlur; _blurSpin.ValueChanged += SetBlur;
        blurRow.AddChild(_blurSlider); blurRow.AddChild(_blurSpin);
        ibl.AddChild(blurRow);
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

        // ---- Post Effects(Godot Environment / CameraAttributes): 메뉴·셸프 토글과 같은 값
        // Post Effects 전용 헬퍼: 값을 함수로 읽어 Rebuild 때 다시 반영할 수 있도록 refresher를 등록한다.
        void CheckG(GridContainer g, string label, Func<bool> get, Action<bool> set, string? tip = null)
        {
            var c = Check(g, label, get(), set);
            if (tip != null) c.TooltipText = tip;
            _refreshers.Add(() => c.ButtonPressed = get());
        }
        void SpinG(GridContainer g, string label, double min, double max, double step, Func<float> get, Action<float> set, string suffix = "")
        {
            var sb = Spin(g, label, min, max, step, get(), set, suffix);
            _refreshers.Add(() => sb.Value = get());
        }
        var glow = Group("Post Effects — Glow (bloom)");
        CheckG(glow, "Glow", () => R.Glow, v => R.Glow = v, "Bright areas bleed light (HDR glow/bloom)");
        SpinG(glow, "Intensity", 0, 8, 0.05, () => R.GlowIntensity, v => R.GlowIntensity = v);
        SpinG(glow, "Bloom", 0, 1, 0.01, () => R.GlowBloom, v => R.GlowBloom = v);
        SpinG(glow, "HDR threshold", 0, 4, 0.05, () => R.GlowThreshold, v => R.GlowThreshold = v);
        var glowBlend = Option(glow, "Blend mode", new[] { "Additive", "Screen", "Softlight", "Replace", "Mix" }, R.GlowBlend, i => R.GlowBlend = i);
        _refreshers.Add(() => glowBlend.Selected = R.GlowBlend);

        var ss = Group("Post Effects — Reflections & Global Illumination");
        CheckG(ss, "Screen-space reflections (SSR)", () => R.Ssr, v => R.Ssr = v, "Reflections of on-screen objects on glossy surfaces");
        SpinG(ss, "SSR max steps", 8, 512, 8, () => R.SsrMaxSteps, v => R.SsrMaxSteps = (int)v);
        CheckG(ss, "Screen-space indirect light (SSIL)", () => R.Ssil, v => R.Ssil = v, "Light bouncing between nearby surfaces (screen space)");
        SpinG(ss, "SSIL intensity", 0, 16, 0.1, () => R.SsilIntensity, v => R.SsilIntensity = v);
        CheckG(ss, "SDFGI (global illumination)", () => R.Sdfgi, v => R.Sdfgi = v, "Signed-distance-field global illumination (heavier)");

        var fog = Group("Post Effects — Fog");
        CheckG(fog, "Fog", () => R.Fog, v => R.Fog = v);
        SpinG(fog, "Fog density", 0, 1, 0.001, () => R.FogDensity, v => R.FogDensity = v);
        fog.AddChild(new Label { Text = "Fog color" });
        var fogCol = new ColorPickerButton { Color = new Color(R.FogColor[0], R.FogColor[1], R.FogColor[2]), CustomMinimumSize = new Vector2(140 * s, 0), EditAlpha = false };
        fogCol.ColorChanged += c => { if (_building) return; R.FogColor = new[] { c.R, c.G, c.B }; Apply(); };
        fog.AddChild(fogCol);
        _refreshers.Add(() => fogCol.Color = new Color(R.FogColor[0], R.FogColor[1], R.FogColor[2]));
        CheckG(fog, "Volumetric fog", () => R.VolumetricFog, v => R.VolumetricFog = v, "3D fog that scatters light (lit by scene lights / IBL)");
        SpinG(fog, "Volumetric density", 0, 1, 0.005, () => R.VolumetricFogDensity, v => R.VolumetricFogDensity = v);

        var adj = Group("Post Effects — Color Adjustment");
        CheckG(adj, "Color adjustment", () => R.Adjust, v => R.Adjust = v);
        SpinG(adj, "Brightness", 0.01, 8, 0.01, () => R.Brightness, v => R.Brightness = v);
        SpinG(adj, "Contrast", 0.01, 8, 0.01, () => R.Contrast, v => R.Contrast = v);
        SpinG(adj, "Saturation", 0, 8, 0.01, () => R.Saturation, v => R.Saturation = v);

        var cam = Group("Post Effects — Camera");
        CheckG(cam, "Depth of field: far blur", () => R.DofFar, v => R.DofFar = v);
        SpinG(cam, "Far distance", 0, 10000, 0.1, () => R.DofFarDistance, v => R.DofFarDistance = v, " m");
        SpinG(cam, "Far transition", 0, 10000, 0.1, () => R.DofFarTransition, v => R.DofFarTransition = v, " m");
        CheckG(cam, "Depth of field: near blur", () => R.DofNear, v => R.DofNear = v);
        SpinG(cam, "Near distance", 0, 10000, 0.05, () => R.DofNearDistance, v => R.DofNearDistance = v, " m");
        SpinG(cam, "Near transition", 0, 10000, 0.05, () => R.DofNearTransition, v => R.DofNearTransition = v, " m");
        SpinG(cam, "Blur amount", 0, 1, 0.01, () => R.DofAmount, v => R.DofAmount = v);
        CheckG(cam, "Auto exposure", () => R.AutoExposure, v => R.AutoExposure = v, "Adapts exposure to scene brightness over time");

        // ---- Quality
        var q = Group("Anti-aliasing");
        _msaa = Option(q, "MSAA", new[] { "Off", "2x", "4x", "8x" }, R.Msaa, i => R.Msaa = i);
        _fxaa = Check(q, "FXAA", R.Fxaa, v => R.Fxaa = v);
        CheckG(q, "SMAA (overrides FXAA)", () => R.Smaa, v => R.Smaa = v);
        CheckG(q, "TAA (temporal)", () => R.Taa, v => R.Taa = v);
        CheckG(q, "Debanding", () => R.Debanding, v => R.Debanding = v, "Dither to hide color banding in gradients");

        // Reset: 렌더 설정을 기본값 인스턴스로 바꾸고 적용한 뒤 컨트롤을 다시 읽는다.
        var buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        var reset = new Button { Text = "Reset to Defaults", FocusMode = Control.FocusModeEnum.None };
        reset.Pressed += () => { CubeApp.Instance.Settings.Render = new RenderSettings(); Apply(); Rebuild(); };
        buttons.AddChild(reset);
        box.AddChild(buttons);
        _building = false;
    }

    /// <summary>
    /// HDRI/이미지 파일을 고르는 네이티브 다이얼로그를 띄운다. 고르면 디코딩 캐시에서 그 경로를 지우고(같은 파일 재로드),
    /// 사용자 HDRI로 설정·드롭다운을 Custom으로 맞춘 뒤 적용한다. 취소하면 드롭다운을 원래 값으로 되돌린다.
    /// </summary>
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
        _blurSlider.Value = R.BackgroundBlur; _blurSpin.Value = R.BackgroundBlur;
        _tonemap.Selected = R.Tonemap; _msaa.Selected = R.Msaa;
        foreach (var r in _refreshers) r();
        _building = false;
    }

    /// <summary>렌더 설정을 저장하고 모든 뷰포트 패널에 반영한다(Shell.ApplyRenderSettings).</summary>
    private void Apply() => _shell.ApplyRenderSettings();
}
