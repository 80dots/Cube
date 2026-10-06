using Cube.Core.Selection;
using Godot;

namespace Cube.App.UI.UvEditor;

/// <summary>UV 편집기 창(임베디드 서브윈도우). 툴바(모드/투영/편집) + UvCanvas.</summary>
public partial class UvEditorWindow : Window
{
    private Shell _shell = null!;
    public UvCanvas Canvas { get; private set; } = null!;
    private readonly Dictionary<SelectMode, Button> _modeButtons = new();

    public void Setup(Shell shell)
    {
        _shell = shell;
        float s = CubeApp.Instance.UiScale;
        Title = "UV Editor";
        Visible = false;
        var host = shell.GetViewport().GetVisibleRect().Size;
        Size = new Vector2I((int)MathF.Min(820 * s, host.X * 0.8f), (int)MathF.Min(700 * s, host.Y * 0.85f));
        MinSize = new Vector2I((int)(400 * s), (int)(300 * s));
        Unresizable = false;
        Theme = shell.Theme;
        CloseRequested += Hide;

        var root = new VBoxContainer();
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(root);

        var bar = new HBoxContainer();
        bar.AddThemeConstantOverride("separation", (int)(2 * s));
        foreach (var (mode, label, action) in new[] { (SelectMode.Object, "Obj", "mode.object"), (SelectMode.Uv, "UV", "mode.uv"), (SelectMode.Edge, "Edge", "mode.edge"), (SelectMode.Face, "Face", "mode.face") })
        {
            var b = new Button { Text = label, ToggleMode = true, FocusMode = Control.FocusModeEnum.None, TooltipText = shell.Actions.Get(action)?.Label };
            string a = action; b.Pressed += () => shell.Actions.Invoke(a);
            _modeButtons[mode] = b; bar.AddChild(b);
        }
        bar.AddChild(new VSeparator());
        foreach (var (action, label) in new[] { ("uv.planarBest", "Planar"), ("uv.planarX", "X"), ("uv.planarY", "Y"), ("uv.planarZ", "Z"), ("uv.cylindrical", "Cylindrical"), ("uv.spherical", "Spherical") })
            bar.AddChild(ActionButton(action, label));
        bar.AddChild(new VSeparator());
        foreach (var (action, label) in new[] { ("uv.unfold", "Unfold"), ("uv.layout", "Layout"), ("uv.cut", "Cut"), ("uv.sew", "Sew"), ("uv.flipU", "Flip U"), ("uv.flipV", "Flip V") })
            bar.AddChild(ActionButton(action, label));
        bar.AddChild(new VSeparator());
        var frame = new Button { Text = "Frame", FocusMode = Control.FocusModeEnum.None, TooltipText = "Frame selection (F) / all (A)" };
        frame.Pressed += () => Canvas.FrameSelected();
        bar.AddChild(frame);
        root.AddChild(bar);

        Canvas = new UvCanvas();
        Canvas.Setup(shell);
        root.AddChild(Canvas);

        shell.Document.Selection.ModeChanged += RefreshModes;
        shell.Document.Undo.Changed += RefreshEnabled;
        shell.Document.Selection.Changed += RefreshEnabled;
        RefreshModes(); RefreshEnabled();
    }

    private readonly List<(Button b, string action)> _actionButtons = new();

    private Button ActionButton(string action, string label)
    {
        var a = _shell.Actions.Get(action);
        var b = new Button { Text = label, FocusMode = Control.FocusModeEnum.None, TooltipText = a?.Label ?? action };
        b.Pressed += () => _shell.Actions.Invoke(action);
        _actionButtons.Add((b, action));
        return b;
    }

    private void RefreshModes()
    {
        var mode = _shell.Document.Selection.Mode;
        foreach (var (m, b) in _modeButtons) b.SetPressedNoSignal(m == mode);
    }

    private void RefreshEnabled()
    {
        foreach (var (b, action) in _actionButtons) b.Disabled = !(_shell.Actions.Get(action)?.Enabled ?? false);
    }

    public void Toggle()
    {
        if (Visible) Hide();
        else { PopupCentered(); Canvas.Invalidate(); Canvas.CallDeferred(nameof(UvCanvas.FrameAll)); }
    }
}
