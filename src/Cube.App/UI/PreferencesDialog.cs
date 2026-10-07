using Godot;

namespace Cube.App.UI;

/// <summary>Edit → Preferences: UI Scale(적용 시 셸 재구성), 표시/선택 옵션, 그리드 간격(cm), 스냅 증분(Maya 스냅 설정).</summary>
public partial class PreferencesDialog : AcceptDialog
{
    private SpinBox _uiScale = null!;
    private CheckBox _wireOnShaded = null!;
    private CheckBox _cameraBased = null!;
    private SpinBox _mouse = null!;
    private CheckBox _selectThrough = null!;
    private SpinBox _gridSpacing = null!, _rotateSnap = null!, _scaleSnap = null!;
    private CheckBox _retainSpacing = null!;

    public override void _Ready()
    {
        Title = "Preferences";
        OkButtonText = "Save";
        ResizeGrip.AttachToWindow(this);
        var s = CubeApp.Instance.Settings;
        float k = CubeApp.Instance.UiScale;

        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", (int)(12 * k));
        grid.AddThemeConstantOverride("v_separation", (int)(8 * k));

        grid.AddChild(new Label { Text = "UI Scale (%)" });
        var row = new HBoxContainer();
        _uiScale = new SpinBox { MinValue = 50, MaxValue = 300, Step = 10, Value = s.UiScalePercent, Suffix = "%", CustomMinimumSize = new Vector2(110 * k, 0) };
        row.AddChild(_uiScale);
        var reset = new Button { Text = "Default (130%)", FocusMode = Control.FocusModeEnum.None };
        reset.Pressed += () => _uiScale.Value = 130;
        row.AddChild(reset);
        grid.AddChild(row);

        grid.AddChild(new Label { Text = "Wireframe on Shaded" });
        _wireOnShaded = new CheckBox { ButtonPressed = s.WireOnShaded };
        grid.AddChild(_wireOnShaded);

        grid.AddChild(new Label { Text = "Camera-based Selection" });
        _cameraBased = new CheckBox { ButtonPressed = s.CameraBasedSelection, TooltipText = "Click selection ignores occluded components" };
        grid.AddChild(_cameraBased);

        grid.AddChild(new Label { Text = "Box Select Through" });
        _selectThrough = new CheckBox { ButtonPressed = s.MarqueeSelectThrough, TooltipText = "Marquee (box) selection also selects hidden components" };
        grid.AddChild(_selectThrough);

        grid.AddChild(new Label { Text = "Mouse Sensitivity (%)" });
        var mrow = new HBoxContainer();
        _mouse = new SpinBox { MinValue = 10, MaxValue = 300, Step = 5, Value = s.MouseSensitivityPercent, Suffix = "%", CustomMinimumSize = new Vector2(110 * k, 0) };
        mrow.AddChild(_mouse);
        var mreset = new Button { Text = "Default (80%)", FocusMode = Control.FocusModeEnum.None };
        mreset.Pressed += () => _mouse.Value = 80;
        mrow.AddChild(mreset);
        grid.AddChild(mrow);

        grid.AddChild(new Label { Text = "Grid Spacing (cm)" });
        var grow = new HBoxContainer();
        _gridSpacing = new SpinBox { MinValue = 1, MaxValue = 100000, Step = 1, Value = s.GridSpacingCm, Suffix = "cm", CustomMinimumSize = new Vector2(110 * k, 0), TooltipText = "Distance between grid lines; also the grid snap step (X)" };
        grow.AddChild(_gridSpacing);
        var greset = new Button { Text = "Default (100cm)", FocusMode = Control.FocusModeEnum.None };
        greset.Pressed += () => _gridSpacing.Value = 100;
        grow.AddChild(greset);
        grid.AddChild(grow);

        grid.AddChild(new Label { Text = "Rotate Snap (J)" });
        _rotateSnap = new SpinBox { MinValue = 1, MaxValue = 180, Step = 1, Value = s.RotateSnapDegrees, Suffix = "°", CustomMinimumSize = new Vector2(110 * k, 0), TooltipText = "Discrete rotate increment while holding J" };
        grid.AddChild(_rotateSnap);

        grid.AddChild(new Label { Text = "Scale Snap (J)" });
        _scaleSnap = new SpinBox { MinValue = 0.01, MaxValue = 10, Step = 0.01, Value = s.ScaleSnapStep, CustomMinimumSize = new Vector2(110 * k, 0), TooltipText = "Discrete scale increment while holding J" };
        grid.AddChild(_scaleSnap);

        grid.AddChild(new Label { Text = "Retain Component Spacing" });
        _retainSpacing = new CheckBox { ButtonPressed = s.RetainComponentSpacing, TooltipText = "Point snap moves the selection as a whole (off: all selected points collapse onto the snap point)" };
        grid.AddChild(_retainSpacing);

        var box = new VBoxContainer();
        box.AddChild(grid);
        box.AddChild(new Label { Text = "UI Scale is applied by rebuilding the interface; the scene is kept.", Modulate = new Color(1, 1, 1, 0.7f) });
        AddChild(box);

        AddCancelButton("Cancel");
        Confirmed += Apply;
    }

    private void Apply()
    {
        var app = CubeApp.Instance;
        var s = app.Settings;
        int percent = (int)_uiScale.Value;
        bool scaleChanged = percent != s.UiScalePercent;
        s.UiScalePercent = percent;
        s.WireOnShaded = _wireOnShaded.ButtonPressed;
        s.CameraBasedSelection = _cameraBased.ButtonPressed;
        s.MarqueeSelectThrough = _selectThrough.ButtonPressed;
        s.MouseSensitivityPercent = (int)_mouse.Value;
        s.GridSpacingCm = (float)_gridSpacing.Value;
        s.RotateSnapDegrees = (float)_rotateSnap.Value;
        s.ScaleSnapStep = (float)_scaleSnap.Value;
        s.RetainComponentSpacing = _retainSpacing.ButtonPressed;
        s.Save();
        if (scaleChanged) app.CallDeferred(nameof(CubeApp.ReloadShell));
        else
        {
            var shell = Shell.Instance;
            shell.Viewport.Display.WireOnShaded = s.WireOnShaded;
            shell.Viewport.Display.RefreshAll();
            shell.SyncStatusLine();
            shell.ApplyGridSettings();
            foreach (var p in shell.Layout.Panels) p.Hud.Refresh();
        }
    }
}
