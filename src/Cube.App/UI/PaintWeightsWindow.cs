using Cube.App.Tools;
using Cube.Core.Rig;
using Godot;

namespace Cube.App.UI;

/// <summary>Paint Skin Weights Tool 설정 창: 영향(조인트) 목록, 모드, 값, 반지름, Flood.</summary>
public partial class PaintWeightsWindow : FloatingPanel
{
    private Shell _shell = null!;
    private PaintWeightsTool _tool = null!;
    private ItemList _influences = null!;
    private OptionButton _mode = null!;
    private HSlider _value = null!, _radius = null!;
    private Label _valueLabel = null!, _radiusLabel = null!, _target = null!;

    public void Setup(Shell shell, PaintWeightsTool tool)
    {
        _shell = shell; _tool = tool;
        float s = CubeApp.Instance.UiScale;
        Title = "Paint Skin Weights Tool";
        Size = new Vector2(300 * s, 420 * s);
        MinPanelSize = new Vector2(240 * s, 300 * s);
        Closed += () => { if (shell.Tools.Current?.Id == "paintWeights") shell.Tools.SetTool("select"); };

        var root = new VBoxContainer();
        root.SizeFlagsVertical = Control.SizeFlags.ExpandFill; root.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        root.AddThemeConstantOverride("separation", (int)(6 * s));
        Content.AddChild(root);

        _target = new Label { Text = "Mesh: -" };
        root.AddChild(_target);
        root.AddChild(new Label { Text = "Influences" });
        _influences = new ItemList { SizeFlagsVertical = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 120 * s) };
        _influences.ItemSelected += i => _tool.SetJoint((int)i);
        root.AddChild(_influences);

        var modeRow = new HBoxContainer();
        modeRow.AddChild(new Label { Text = "Mode", CustomMinimumSize = new Vector2(60 * s, 0) });
        _mode = new OptionButton { FocusMode = Control.FocusModeEnum.None, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        foreach (var m in new[] { "Replace", "Add", "Smooth" }) _mode.AddItem(m);
        _mode.ItemSelected += i => _tool.Mode = (PaintMode)(int)i;
        modeRow.AddChild(_mode);
        root.AddChild(modeRow);

        root.AddChild(Slider("Value", 0, 1, 0.01, tool.Value, out _value, out _valueLabel, v => _tool.Value = v, s));
        root.AddChild(Slider("Radius", 0.01, 5, 0.01, tool.Radius, out _radius, out _radiusLabel, v => _tool.Radius = v, s));

        var buttons = new HBoxContainer();
        var flood = new Button { Text = "Flood", FocusMode = Control.FocusModeEnum.None, TooltipText = "Apply the value to every vertex" };
        flood.Pressed += () => _tool.Flood();
        buttons.AddChild(flood);
        var normalize = new Button { Text = "Normalize", FocusMode = Control.FocusModeEnum.None };
        normalize.Pressed += () => shell.Actions.Invoke("skin.normalize");
        buttons.AddChild(normalize);
        root.AddChild(buttons);

        tool.TargetChanged += RefreshTarget;
        RefreshTarget();
    }

    private static Control Slider(string label, double min, double max, double step, float initial, out HSlider slider, out Label valueLabel, Action<float> set, float s)
    {
        var row = new HBoxContainer();
        row.AddChild(new Label { Text = label, CustomMinimumSize = new Vector2(60 * s, 0) });
        var sl = new HSlider { MinValue = min, MaxValue = max, Step = step, Value = initial, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, FocusMode = Control.FocusModeEnum.None };
        var lbl = new Label { Text = initial.ToString("0.00"), CustomMinimumSize = new Vector2(40 * s, 0) };
        sl.ValueChanged += v => { set((float)v); lbl.Text = v.ToString("0.00"); };
        row.AddChild(sl); row.AddChild(lbl);
        slider = sl; valueLabel = lbl;
        return row;
    }

    public void RefreshTarget()
    {
        _influences.Clear();
        var mesh = _tool.Mesh;
        _target.Text = mesh != null ? $"Mesh: {mesh.Name}" : "Mesh: (none — Bind Skin first)";
        if (mesh?.Skin is { } skin)
        {
            foreach (var id in skin.Joints) _influences.AddItem(_shell.Document.Find(id)?.Name ?? id.ToString());
            if (_tool.Joint < _influences.ItemCount) _influences.Select(_tool.Joint);
        }
        _mode.Selected = (int)_tool.Mode;
    }

    public void Show(Shell shell)
    {
        if (Docked) { Open(); return; }
        if (!Visible)
        {
            var host = shell.GetViewport().GetVisibleRect().Size;
            bool redock = LastDock != null;
            Open();
            if (!redock) Position = new Vector2(host.X - Size.X - 24, host.Y * 0.25f);
        }
    }
}
