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
    private ViewCube _cube = null!;
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

        // 1줄: 오른쪽 정렬된 뷰 큐브
        var cubeRow = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End, MouseFilter = MouseFilterEnum.Pass };
        _cube = new ViewCube { CustomMinimumSize = new Vector2(84 * s, 84 * s) };
        _cube.Setup(panel);
        cubeRow.AddChild(_cube);
        AddChild(cubeRow);

        // 2줄: 투영 토글 | 셰이딩 모드 버튼들 | 와이어·조인트 | 박스 관통 | 그리드·조인트 축
        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End, MouseFilter = MouseFilterEnum.Pass };
        row.AddThemeConstantOverride("separation", (int)(2 * s));

        _proj = Icons.IconButton("persp", "Perspective / Orthographic", icon, toggle: false);
        _proj.Pressed += () => { panel.CameraController.ToggleOrtho(); Refresh(); };
        row.AddChild(_proj);
        row.AddChild(new VSeparator());

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

        // 모드·카메라가 바뀌면 버튼 상태를 다시 맞춘다
        panel.Display.ModeChanged += Refresh;
        panel.CameraController.Changed += Refresh;
        Refresh();
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

/// <summary>카메라 방향을 따라 도는 뷰 큐브. 면을 클릭하면 해당 정면 뷰로 전환한다.</summary>
/// <remarks>
/// 카메라 기저(right/up)로 단위 큐브 면 6개를 화면에 정사영해 뒤 → 앞(깊이 순)으로 그리고, 카메라를 향하는 면만 라벨과 클릭 영역을 갖는다.
/// 클릭하면 <c>ViewportPanel.SetView</c>로 해당 프리셋(직교)으로 애니메이션 전환한다. 큐브 바깥에 축 색 점(X/Y/Z)도 찍는다.
/// </remarks>
public partial class ViewCube : Control
{
    /// <summary>카메라와 뷰 전환 대상 패널.</summary>
    private ViewportPanel _panel = null!;
    /// <summary>마지막으로 그린 앞면들의 화면 다각형과 뷰 종류(클릭 판정용, 앞쪽 면이 뒤에 들어 있다).</summary>
    private readonly List<(Vector2[] poly, ViewKind kind)> _hit = new();

    /// <summary>큐브 면 정의: 바깥 법선, 라벨, 클릭 시 전환할 뷰(+Z 면 = FRONT, +X 면 = RIGHT/Side).</summary>
    private static readonly (Vector3 n, string label, ViewKind kind)[] Faces =
    {
        (Vector3.Back, "FRONT", ViewKind.Front), (Vector3.Forward, "BACK", ViewKind.Back),
        (Vector3.Right, "RIGHT", ViewKind.Side), (Vector3.Left, "LEFT", ViewKind.Left),
        (Vector3.Up, "TOP", ViewKind.Top), (Vector3.Down, "BOTTOM", ViewKind.Bottom),
    };

    /// <summary>패널을 연결하고 마우스 입력을 받도록 설정한다.</summary>
    public void Setup(ViewportPanel panel)
    {
        _panel = panel;
        MouseFilter = MouseFilterEnum.Stop;
        TooltipText = "Click a face to look from that side";
    }

    /// <summary>카메라가 움직일 수 있으므로 매 프레임 다시 그린다.</summary>
    public override void _Process(double delta) => QueueRedraw();

    /// <summary>카메라 방향에 맞춰 뷰 큐브를 그리고 클릭 영역(<c>_hit</c>)을 다시 만든다.</summary>
    public override void _Draw()
    {
        if (_panel?.Camera == null) return;
        // 카메라 기저: 월드 점 v를 화면 (v·right, −v·up)으로 정사영(P), 깊이는 v·forward(작을수록 카메라 쪽)
        float s = CubeApp.Instance.UiScale;
        var b = _panel.Camera.GlobalTransform.Basis;
        var right = b.X; var up = b.Y; var fwd = -b.Z;
        var center = Size / 2;
        float half = Size.X * 0.26f;
        Vector2 P(Vector3 v) => center + new Vector2(v.Dot(right), -v.Dot(up)) * half;
        float Depth(Vector3 v) => v.Dot(fwd);

        // 면마다 네 꼭짓점을 만들고 깊이(면 법선·forward)가 큰(먼) 면부터 그리도록 정렬
        _hit.Clear();
        var font = GetThemeDefaultFont();
        int fs = (int)(8 * s);
        var ordered = Faces.Select(f =>
        {
            var (u, v) = Perp(f.n);
            var corners = new[] { f.n + u + v, f.n - u + v, f.n - u - v, f.n + u - v };
            return (f, corners, depth: Depth(f.n));
        }).OrderBy(t => t.depth).ToList();
        foreach (var (f, corners, depth) in ordered)
        {
            // 앞면(depth < 0)은 밝고 불투명하게(정면일수록 밝게), 뒷면은 어둡고 반투명하게
            var poly = corners.Select(P).ToArray();
            bool front = depth < 0;
            float shade = front ? 0.55f + 0.35f * (-depth) : 0.25f;
            var col = new Color(shade, shade, shade, front ? 0.95f : 0.4f);
            DrawColoredPolygon(poly, col);
            for (int i = 0; i < 4; i++) DrawLine(poly[i], poly[(i + 1) % 4], new Color(0.1f, 0.1f, 0.1f, 0.9f), 1 * s, true);
            if (front)
            {
                var c = (poly[0] + poly[1] + poly[2] + poly[3]) / 4;
                var size = font.GetStringSize(f.label, HorizontalAlignment.Left, -1, fs);
                DrawString(font, c - size / 2 + new Vector2(0, size.Y * 0.75f), f.label, HorizontalAlignment.Left, -1, fs, new Color(0.08f, 0.08f, 0.08f));
                _hit.Add((poly, f.kind));
            }
        }
        // 큐브 바깥(1.65배)에 축 방향 색 점
        foreach (var (axis, color) in new[] { (Vector3.Right, GizmoRed), (Vector3.Up, GizmoGreen), (Vector3.Back, GizmoBlue) })
            DrawCircle(P(axis * 1.65f), 3 * s, color);
    }

    /// <summary>축 점 색(조작기와 같은 X 빨강 / Y 초록 / Z 파랑).</summary>
    private static readonly Color GizmoRed = MathConvert.Rgb(0xff2a2a), GizmoGreen = MathConvert.Rgb(0x5aff2a), GizmoBlue = MathConvert.Rgb(0x2a6aff);

    /// <summary>면 법선 n에 수직인 두 단위 벡터(면 사각형의 두 변 방향).</summary>
    private static (Vector3 u, Vector3 v) Perp(Vector3 n)
    {
        var helper = Mathf.Abs(n.Y) < 0.9f ? Vector3.Up : Vector3.Right;
        var u = helper.Cross(n).Normalized();
        return (u, n.Cross(u));
    }

    /// <summary>좌클릭이 앞면 다각형 안이면(나중에 그린 = 앞쪽 면부터 검사) 그 뷰로 전환하고 이벤트를 소비한다.</summary>
    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } mb)
        {
            for (int i = _hit.Count - 1; i >= 0; i--)
                if (Geometry2D.IsPointInPolygon(mb.Position, _hit[i].poly))
                {
                    _panel.SetView(_hit[i].kind);
                    AcceptEvent();
                    return;
                }
        }
    }
}
