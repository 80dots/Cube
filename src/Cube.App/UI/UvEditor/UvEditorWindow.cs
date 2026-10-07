using Cube.Core.Selection;
using Godot;

namespace Cube.App.UI.UvEditor;

/// <summary>UV 편집기(플로팅 패널). 아이콘 툴바(모드/투영/편집/Auto Seam·Wrap/Frame/배경) + UvCanvas.</summary>
public partial class UvEditorWindow : FloatingPanel
{
    private Shell _shell = null!;
    public UvCanvas Canvas { get; private set; } = null!;
    private readonly Dictionary<string, Button> _modeButtons = new();
    private readonly List<(Button b, string action)> _actionButtons = new();
    private OptionButton _background = null!;

    public void Setup(Shell shell)
    {
        _shell = shell;
        float s = CubeApp.Instance.UiScale;
        Title = "UV Editor";
        var host = shell.GetViewport().GetVisibleRect().Size;
        Size = new Vector2(MathF.Min(820 * s, host.X * 0.8f), MathF.Min(700 * s, host.Y * 0.85f));
        MinPanelSize = new Vector2(400 * s, 300 * s);

        Canvas = new UvCanvas();
        Canvas.Setup(shell);

        int icon = (int)(18 * s);
        var bar = new HBoxContainer();
        bar.AddThemeConstantOverride("separation", (int)(2 * s));
        // 모드: Obj / UV / Edge / Face / Island
        foreach (var (key, iconName, action, tip) in new[] {
            ("object", "mode_object", "mode.object", "Object Mode"), ("uv", "mode_uv", "mode.uv", "UV Mode (F12)"),
            ("edge", "mode_edge", "mode.edge", "Edge Mode (F10)"), ("face", "mode_face", "mode.face", "Face Mode (F11)"),
            ("island", "mode_island", "mode.uvIsland", "UV Island Mode (select whole islands separated by seams)") })
        {
            var b = Icons.IconButton(iconName, tip, icon, toggle: true);
            string a = action; b.Pressed += () => shell.Actions.Invoke(a);
            _modeButtons[key] = b; bar.AddChild(b);
        }
        bar.AddChild(new VSeparator());
        foreach (var (action, iconName) in new[] { ("uv.planarBest", "uv_planar"), ("uv.planarX", "uv_planar_x"), ("uv.planarY", "uv_planar_y"), ("uv.planarZ", "uv_planar_z"), ("uv.cylindrical", "uv_cylindrical"), ("uv.spherical", "uv_spherical") })
            bar.AddChild(ActionButton(action, iconName, icon));
        bar.AddChild(new VSeparator());
        foreach (var (action, iconName) in new[] { ("uv.unfold", "uv_unfold"), ("uv.layout", "uv_layout"), ("uv.cut", "uv_cut"), ("uv.sew", "uv_sew"), ("uv.flipU", "uv_flip_u"), ("uv.flipV", "uv_flip_v") })
            bar.AddChild(ActionButton(action, iconName, icon));
        bar.AddChild(new VSeparator());
        foreach (var (action, iconName) in new[] { ("uv.autoSeams", "uv_autoseam"), ("uv.autoWrap", "uv_autowrap") })
            bar.AddChild(ActionButton(action, iconName, icon));
        bar.AddChild(new VSeparator());
        var frame = Icons.IconButton("uv_frame", "Frame selection (F) / all (A)", icon);
        frame.Pressed += () => Canvas.FrameSelected();
        bar.AddChild(frame);
        bar.AddChild(new VSeparator());
        _background = new OptionButton { FocusMode = Control.FocusModeEnum.None, TooltipText = "Background" };
        foreach (var name in new[] { "No Background", "Grid", "UV Texture", "Mapped Texture" }) _background.AddItem(name);
        _background.Selected = (int)UvBackground.UvTexture;
        _background.ItemSelected += i => { Canvas.Background = (UvBackground)(int)i; };
        bar.AddChild(_background);
        Content.AddChild(bar);
        Content.AddChild(Canvas);

        shell.Document.Selection.ModeChanged += RefreshModes;
        Canvas.IslandModeChanged += RefreshModes;
        shell.Document.Undo.Changed += RefreshEnabled;
        shell.Document.Selection.Changed += RefreshEnabled;
        RefreshModes(); RefreshEnabled();
    }

    /// <summary>배경 옵션 순환(파이 메뉴용): None → Grid → UV Texture → Mapped → ...</summary>
    public void CycleBackground()
    {
        var next = (UvBackground)(((int)Canvas.Background + 1) % 4);
        Canvas.Background = next;
        _background.Selected = (int)next;
    }

    private Button ActionButton(string action, string iconName, int icon)
    {
        var a = _shell.Actions.Get(action);
        var b = Icons.IconButton(iconName, a?.Label ?? action, icon);
        b.Pressed += () => _shell.Actions.Invoke(action);
        _actionButtons.Add((b, action));
        return b;
    }

    private void RefreshModes()
    {
        var mode = _shell.Document.Selection.Mode;
        bool island = Canvas.IslandMode && mode == SelectMode.Uv;
        _modeButtons["object"].SetPressedNoSignal(mode == SelectMode.Object);
        _modeButtons["uv"].SetPressedNoSignal(mode == SelectMode.Uv && !island);
        _modeButtons["edge"].SetPressedNoSignal(mode == SelectMode.Edge);
        _modeButtons["face"].SetPressedNoSignal(mode == SelectMode.Face);
        _modeButtons["island"].SetPressedNoSignal(island);
    }

    private void RefreshEnabled()
    {
        foreach (var (b, action) in _actionButtons) b.Disabled = !(_shell.Actions.Get(action)?.Enabled ?? false);
    }

    public void Toggle()
    {
        if (Visible) { Close(); return; }
        Open();
        Canvas.Invalidate(); Canvas.CallDeferred(nameof(UvCanvas.FrameAll));
    }
}
