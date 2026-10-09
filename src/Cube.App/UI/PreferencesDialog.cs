using Godot;

namespace Cube.App.UI;

/// <summary>Edit → Preferences: UI Scale(적용 시 셸 재구성), 표시/선택 옵션, 그리드 간격(cm), 스냅 증분(Maya 스냅 설정).</summary>
/// <remarks>
/// 컨트롤은 열 때(_Ready) 현재 Settings 값으로 채우고, Save(확인)를 눌렀을 때만 <see cref="Apply"/>로 되돌려 쓴다.
/// UI 배율이 바뀐 경우에는 셸 전체를 다시 만들고(문서 유지), 아니면 뷰포트 표시·상태 라인·그리드·HUD만 즉시 갱신한다.
/// </remarks>
public partial class PreferencesDialog : AcceptDialog
{
    /// <summary>UI 배율(%) 입력(50~300).</summary>
    private SpinBox _uiScale = null!;
    /// <summary>셰이딩 위에 와이어 표시 기본값.</summary>
    private CheckBox _wireOnShaded = null!;
    /// <summary>클릭 선택 시 가려진 요소 무시(카메라 기준 선택).</summary>
    private CheckBox _cameraBased = null!;
    /// <summary>마우스 내비게이션 감도(%).</summary>
    private SpinBox _mouse = null!;
    /// <summary>박스(마키) 선택이 가려진 요소도 포함하는지.</summary>
    private CheckBox _selectThrough = null!;
    /// <summary>그리드 간격(cm, 그리드 스냅 단위), J 홀드 회전 스냅(°), J 홀드 스케일 스냅 증분.</summary>
    private SpinBox _gridSpacing = null!, _rotateSnap = null!, _scaleSnap = null!;
    /// <summary>점 스냅 시 선택 요소 간 간격 유지(끄면 모두 스냅 점으로 모음).</summary>
    private CheckBox _retainSpacing = null!;

    /// <summary>다이얼로그 내용을 만든다: 크기 조절 그립, SpinBox MMB 드래그, 2열 그리드(라벨 / 컨트롤), 안내 문구, Cancel 버튼.</summary>
    public override void _Ready()
    {
        Title = "Preferences";
        OkButtonText = "Save";
        ResizeGrip.AttachToWindow(this);
        AddChild(new SpinDrag());
        var s = CubeApp.Instance.Settings;
        float k = CubeApp.Instance.UiScale;

        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", (int)(12 * k));
        grid.AddThemeConstantOverride("v_separation", (int)(8 * k));

        // UI 배율 + 기본값(130%) 버튼.
        grid.AddChild(new Label { Text = "UI Scale (%)" });
        var row = new HBoxContainer();
        _uiScale = new SpinBox { MinValue = 50, MaxValue = 300, Step = 10, Value = s.UiScalePercent, Suffix = "%", CustomMinimumSize = new Vector2(110 * k, 0) };
        row.AddChild(_uiScale);
        var reset = new Button { Text = "Default (130%)", FocusMode = Control.FocusModeEnum.None };
        reset.Pressed += () => _uiScale.Value = 130;
        row.AddChild(reset);
        grid.AddChild(row);

        // 표시/선택 옵션 체크박스들.
        grid.AddChild(new Label { Text = "Wireframe on Shaded" });
        _wireOnShaded = new CheckBox { ButtonPressed = s.WireOnShaded };
        grid.AddChild(_wireOnShaded);

        grid.AddChild(new Label { Text = "Camera-based Selection" });
        _cameraBased = new CheckBox { ButtonPressed = s.CameraBasedSelection, TooltipText = "Click selection ignores occluded components" };
        grid.AddChild(_cameraBased);

        grid.AddChild(new Label { Text = "Box Select Through" });
        _selectThrough = new CheckBox { ButtonPressed = s.MarqueeSelectThrough, TooltipText = "Marquee (box) selection also selects hidden components" };
        grid.AddChild(_selectThrough);

        // 마우스 감도 + 기본값(80%) 버튼.
        grid.AddChild(new Label { Text = "Mouse Sensitivity (%)" });
        var mrow = new HBoxContainer();
        _mouse = new SpinBox { MinValue = 10, MaxValue = 300, Step = 5, Value = s.MouseSensitivityPercent, Suffix = "%", CustomMinimumSize = new Vector2(110 * k, 0) };
        mrow.AddChild(_mouse);
        var mreset = new Button { Text = "Default (80%)", FocusMode = Control.FocusModeEnum.None };
        mreset.Pressed += () => _mouse.Value = 80;
        mrow.AddChild(mreset);
        grid.AddChild(mrow);

        // 그리드 간격 + 기본값(100cm) 버튼.
        grid.AddChild(new Label { Text = "Grid Spacing (cm)" });
        var grow = new HBoxContainer();
        _gridSpacing = new SpinBox { MinValue = 1, MaxValue = 100000, Step = 1, Value = s.GridSpacingCm, Suffix = "cm", CustomMinimumSize = new Vector2(110 * k, 0), TooltipText = "Distance between grid lines; also the grid snap step (X)" };
        grow.AddChild(_gridSpacing);
        var greset = new Button { Text = "Default (100cm)", FocusMode = Control.FocusModeEnum.None };
        greset.Pressed += () => _gridSpacing.Value = 100;
        grow.AddChild(greset);
        grid.AddChild(grow);

        // Maya J 홀드 증분 스냅(회전·스케일)과 점 스냅 간격 유지.
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

    /// <summary>
    /// 컨트롤 값을 Settings에 쓰고 저장한다. UI 배율이 바뀌었으면 셸 재구성을 지연 호출(다이얼로그 콜백 안에서 셸을 지우지 않도록)하고,
    /// 아니면 활성 뷰포트의 와이어 표시·상태 라인 스냅 버튼·그리드 간격·모든 패널 HUD를 즉시 갱신한다.
    /// </summary>
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
        // 배율 변경: 다음 프레임에 셸 전체를 다시 만든다(모든 픽셀 상수가 배율을 곱하므로).
        if (scaleChanged) app.CallDeferred(nameof(CubeApp.ReloadShell));
        else
        {
            var shell = Shell.Instance;
            // 전역 설정이므로 4분할의 모든 패널에 적용(전에는 활성 패널에만 적용되어 나머지 패널은 다시 시작해야 바뀌었음)
            foreach (var p in shell.Layout.Panels) { p.Display.WireOnShaded = s.WireOnShaded; p.Display.RefreshAll(); }
            shell.SyncStatusLine();
            shell.ApplyGridSettings();
            foreach (var p in shell.Layout.Panels) p.Hud.Refresh();
        }
    }
}
