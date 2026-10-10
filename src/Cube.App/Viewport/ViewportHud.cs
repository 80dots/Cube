using Cube.App.Bridge;
using Cube.App.UI;
using Cube.Core.Camera;
using Godot;

namespace Cube.App.Viewport;

/// <summary>
/// 뷰포트 우상단 HUD: 클릭 가능한 뷰 큐브, 그 아래 가로 한 줄로 Persp/Ortho 토글 | 표시 모드(Wireframe/Shaded/Textured/Lit/UV) | 박스 선택 관통 토글.
/// </summary>
/// <remarks>
/// 패널마다 하나 있다. 버튼은 대부분 ActionRegistry 액션(display.*)을 호출하고 모든 패널 HUD를 다시 동기화한다(설정이 전역이라).
/// 셰이딩 모드 버튼은 그 패널의 ViewportDisplay에만 적용된다. 버튼 눌림 상태는 <see cref="Refresh"/>가 실제 상태에서 다시 읽어 맞춘다.
/// </remarks>
public partial class ViewportHud : VBoxContainer
{
    /// <summary>HUD가 붙은 뷰포트 패널.</summary>
    private ViewportPanel _panel = null!;
    /// <summary>클릭 가능한 뷰 큐브.</summary>
    private NavGizmo _cube = null!;
    /// <summary>Persp/Ortho 토글 버튼(아이콘이 현재 투영을 표시).</summary>
    private Button _proj = null!;
    /// <summary>박스 선택 관통(Settings.MarqueeSelectThrough) 토글.</summary>
    private Button _through = null!;
    /// <summary>Grid / Joints / Joint Local Rotation Axes / Wireframe on Shaded 토글 버튼.</summary>
    private Button _grid = null!, _joints = null!, _jointAxes = null!, _wireOnShaded = null!;
    /// <summary>셰이딩 모드 → 그 모드 버튼(라디오처럼 현재 모드만 눌림).</summary>
    private readonly Dictionary<ShadingMode, Button> _modeButtons = new();

    /// <summary>HUD UI를 코드로 구성한다(뷰 큐브 줄 + 버튼 줄). 크기는 UI 배율을 곱하고, 카메라/모드 변경 시 <see cref="Refresh"/>되도록 구독한다.</summary>
    public void Setup(ViewportPanel panel)
    {
        _panel = panel;
        float s = CubeApp.Instance.UiScale;
        int icon = (int)(18 * s);
        MouseFilter = MouseFilterEnum.Pass;
        Alignment = AlignmentMode.Begin;
        AddThemeConstantOverride("separation", (int)(4 * s));

        // 1줄: 오른쪽 정렬된 내비게이션 기즈모(Blender 스타일) + 그 오른쪽에 세로로 줌/팬 끌기 버튼과 투영 토글(Blender 뷰포트 오른쪽 기둥)
        var cubeRow = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End, MouseFilter = MouseFilterEnum.Pass };
        cubeRow.AddThemeConstantOverride("separation", (int)(2 * s));
        _cube = new NavGizmo { CustomMinimumSize = new Vector2(84 * s, 84 * s) };
        _cube.Setup(panel);
        cubeRow.AddChild(_cube);
        var navCol = new VBoxContainer { MouseFilter = MouseFilterEnum.Pass, Alignment = BoxContainer.AlignmentMode.Begin };
        navCol.AddThemeConstantOverride("separation", (int)(2 * s));
        var zoomBtn = new NavDragButton(); zoomBtn.Setup("nav_zoom", "Zoom: drag up/down (or Alt+RMB in the view)", d => panel.CameraController.Dolly(d.X, d.Y));
        navCol.AddChild(zoomBtn);
        var panBtn = new NavDragButton(); panBtn.Setup("nav_pan", "Pan: drag to move the view (or Alt+MMB)", d => panel.CameraController.Track(d.X, d.Y, panel.Size.Y));
        navCol.AddChild(panBtn);
        _proj = Icons.IconButton("persp", "Perspective / Orthographic", icon, toggle: false);
        _proj.Pressed += () => { panel.CameraController.ToggleOrtho(); Refresh(); };
        navCol.AddChild(_proj);
        cubeRow.AddChild(navCol);
        AddChild(cubeRow);

        // 2줄: 투영 토글 | 셰이딩 모드 버튼들 | 와이어·조인트 | 박스 관통 | 그리드·조인트 축
        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End, MouseFilter = MouseFilterEnum.Pass };
        row.AddThemeConstantOverride("separation", (int)(2 * s));

        // 셰이딩 모드 버튼(Maya 숫자 키 4/5/6/7 + UV Grid)
        foreach (var (mode, iconName, tip) in new[]
                 {
                     (ShadingMode.Wireframe, "view_wire", "Wireframe (4)"), (ShadingMode.Shaded, "view_shade", "Smooth Shade All (5)"),
                     (ShadingMode.Textured, "view_tex", "Smooth Shade + Textured (6)"), (ShadingMode.Lit, "view_lit", "Use All Lights (7)"),
                     (ShadingMode.UvGrid, "view_uv", "UV Grid"),
                 })
        {
            var b = Icons.IconButton(iconName, tip, icon, toggle: true);
            var m = mode;
            b.Pressed += () => { panel.Display.SetMode(m); Refresh(); };
            _modeButtons[mode] = b;
            row.AddChild(b);
        }
        _wireOnShaded = Icons.IconButton("view_wire_on_shaded", "Wireframe on Shaded", icon, toggle: true);
        _wireOnShaded.Pressed += () => { UI.Shell.Instance.Actions.Invoke("display.wireOnShaded"); RefreshAllHuds(); };
        row.AddChild(_wireOnShaded);
        _joints = Icons.IconButton("view_joints", "Joints (Display → Joints)", icon, toggle: true);
        _joints.Pressed += () => { UI.Shell.Instance.Actions.Invoke("display.joints"); RefreshAllHuds(); };
        row.AddChild(_joints);
        row.AddChild(new VSeparator());

        _through = Icons.IconButton("select_through", "Box Select Through: marquee also selects hidden components", icon, toggle: true);
        _through.Pressed += () =>
        {
            CubeApp.Instance.Settings.MarqueeSelectThrough = _through.ButtonPressed;
            CubeApp.Instance.Settings.Save();
            foreach (var p in UI.Shell.Instance.Layout.Panels) p.Hud.Refresh();
        };
        row.AddChild(_through);
        row.AddChild(new VSeparator());
        _grid = Icons.IconButton("view_grid", "Grid (Display → Grid)", icon, toggle: true);
        _grid.Pressed += () => { UI.Shell.Instance.Actions.Invoke("display.grid"); RefreshAllHuds(); };
        row.AddChild(_grid);
        _jointAxes = Icons.IconButton("view_joint_axes", "Joint Local Rotation Axes", icon, toggle: true);
        _jointAxes.Pressed += () => { UI.Shell.Instance.Actions.Invoke("display.jointAxes"); RefreshAllHuds(); };
        row.AddChild(_jointAxes);
        AddChild(row);
        _row = row;
        // 좁은 패널(4분할 등)에서는 버튼 줄이 패널 가운데까지 덮어 조작기·선택 클릭을 가로채므로 숨긴다(뷰 큐브만 남김)
        panel.Resized += UpdateCompact;
        Callable.From(UpdateCompact).CallDeferred();

        // 모드·카메라가 바뀌면 버튼 상태를 다시 맞춘다
        panel.Display.ModeChanged += Refresh;
        panel.CameraController.Changed += Refresh;
        Refresh();
    }

    /// <summary>버튼 줄(투영·셰이딩·표시 토글).</summary>
    private HBoxContainer _row = null!;

    /// <summary>
    /// 패널 폭에 비해 버튼 줄이 너무 넓으면(폭의 55% 초과) 버튼 줄을 숨기고, 아주 작은 패널(높이 220px·폭 260px 미만, UI 배율 반영)에서는 뷰 큐브도 숨긴다.
    /// 예전에는 4분할 뷰(패널 폭 ~450px)에서 버튼 줄이 패널 위쪽 가운데를 덮어 조작기 핸들(축 화살표)을 누르면 HUD 버튼이 눌렸다.
    /// 같은 기능은 Display 메뉴·단축키(4/5/6/7)로 쓸 수 있다.
    /// </summary>
    private void UpdateCompact()
    {
        if (_panel == null || _row == null || !IsInstanceValid(_panel)) return;
        float s = CubeApp.Instance.UiScale;
        float rowW = _row.GetCombinedMinimumSize().X;
        bool showRow = _panel.Size.X <= 0 || rowW <= _panel.Size.X * 0.55f;
        bool showCube = _panel.Size.X <= 0 || (_panel.Size.X >= 260 * s && _panel.Size.Y >= 220 * s);
        if (_row.Visible == showRow && _cube.GetParent<Control>().Visible == showCube) return;
        _row.Visible = showRow;
        _cube.GetParent<Control>().Visible = showCube;
        // 오른쪽 위 모서리를 유지한 채 최소 크기로 다시 맞춘다(최소 크기는 다음 프레임에 갱신되므로 지연)
        Callable.From(FitToMinimum).CallDeferred();
    }

    /// <summary>오른쪽·위 오프셋은 그대로 두고 왼쪽·아래 오프셋을 최소 크기에 맞춘다(우상단 앵커).</summary>
    private void FitToMinimum()
    {
        var m = GetCombinedMinimumSize();
        OffsetLeft = OffsetRight - m.X;
        OffsetBottom = OffsetTop + m.Y;
    }

    /// <summary>모든 버튼 눌림 상태와 투영 아이콘·툴팁을 현재 상태(패널 표시 상태·전역 설정)에 맞추고 뷰 큐브를 다시 그린다. 신호는 내지 않는다.</summary>
    public void Refresh()
    {
        if (_panel == null) return;
        foreach (var (m, b) in _modeButtons) b.SetPressedNoSignal(_panel.Display.Mode == m);
        bool ortho = _panel.CameraController.IsOrtho;
        _proj.Icon = Icons.Get(ortho ? "ortho" : "persp", (int)(18 * CubeApp.Instance.UiScale));
        _proj.TooltipText = ortho ? "Orthographic (click for Perspective)" : "Perspective (click for Orthographic)";
        _through.SetPressedNoSignal(CubeApp.Instance.Settings.MarqueeSelectThrough);
        _grid.SetPressedNoSignal(_panel.Display.ShowGrid);
        _wireOnShaded.SetPressedNoSignal(_panel.Display.WireOnShaded);
        _jointAxes.SetPressedNoSignal(CubeApp.Instance.Settings.ShowJointAxes);
        _joints.SetPressedNoSignal(CubeApp.Instance.Settings.ShowJoints);
        _cube.QueueRedraw();
    }

    /// <summary>모든 패널의 HUD를 새로고침한다(전역 설정 토글 후).</summary>
    private static void RefreshAllHuds() { foreach (var p in UI.Shell.Instance.Layout.Panels) p.Hud.Refresh(); }
}
