using Cube.App.Bridge;
using Cube.App.UI;
using Cube.Core.Camera;
using Godot;

namespace Cube.App.Viewport;

/// <summary>
/// 뷰포트 우상단 HUD: 클릭 가능한 뷰 큐브, 그 아래 가로 한 줄로 Persp/Ortho 토글 | 표시 모드(Wireframe/Shaded/Textured/Lit/UV) | 박스 선택 관통 토글.
/// </summary>
public partial class ViewportHud : VBoxContainer
{
    private ViewportPanel _panel = null!;
    private ViewCube _cube = null!;
    private Button _proj = null!;
    private Button _through = null!;
    private Button _grid = null!, _jointAxes = null!;
    private readonly Dictionary<ShadingMode, Button> _modeButtons = new();

    public void Setup(ViewportPanel panel)
    {
        _panel = panel;
        float s = CubeApp.Instance.UiScale;
        int icon = (int)(18 * s);
        MouseFilter = MouseFilterEnum.Pass;
        Alignment = AlignmentMode.Begin;
        AddThemeConstantOverride("separation", (int)(4 * s));

        var cubeRow = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End, MouseFilter = MouseFilterEnum.Pass };
        _cube = new ViewCube { CustomMinimumSize = new Vector2(84 * s, 84 * s) };
        _cube.Setup(panel);
        cubeRow.AddChild(_cube);
        AddChild(cubeRow);

        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End, MouseFilter = MouseFilterEnum.Pass };
        row.AddThemeConstantOverride("separation", (int)(2 * s));

        _proj = Icons.IconButton("persp", "Perspective / Orthographic", icon, toggle: false);
        _proj.Pressed += () => { panel.CameraController.ToggleOrtho(); Refresh(); };
        row.AddChild(_proj);
        row.AddChild(new VSeparator());

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

        panel.Display.ModeChanged += Refresh;
        panel.CameraController.Changed += Refresh;
        Refresh();
    }

    public void Refresh()
    {
        if (_panel == null) return;
        foreach (var (m, b) in _modeButtons) b.SetPressedNoSignal(_panel.Display.Mode == m);
        bool ortho = _panel.CameraController.IsOrtho;
        _proj.Icon = Icons.Get(ortho ? "ortho" : "persp", (int)(18 * CubeApp.Instance.UiScale));
        _proj.TooltipText = ortho ? "Orthographic (click for Perspective)" : "Perspective (click for Orthographic)";
        _through.SetPressedNoSignal(CubeApp.Instance.Settings.MarqueeSelectThrough);
        _grid.SetPressedNoSignal(_panel.Display.ShowGrid);
        _jointAxes.SetPressedNoSignal(CubeApp.Instance.Settings.ShowJointAxes);
        _cube.QueueRedraw();
    }

    private static void RefreshAllHuds() { foreach (var p in UI.Shell.Instance.Layout.Panels) p.Hud.Refresh(); }
}

/// <summary>카메라 방향을 따라 도는 뷰 큐브. 면을 클릭하면 해당 정면 뷰로 전환한다.</summary>
public partial class ViewCube : Control
{
    private ViewportPanel _panel = null!;
    private readonly List<(Vector2[] poly, ViewKind kind)> _hit = new();

    private static readonly (Vector3 n, string label, ViewKind kind)[] Faces =
    {
        (Vector3.Back, "FRONT", ViewKind.Front), (Vector3.Forward, "BACK", ViewKind.Back),
        (Vector3.Right, "RIGHT", ViewKind.Side), (Vector3.Left, "LEFT", ViewKind.Left),
        (Vector3.Up, "TOP", ViewKind.Top), (Vector3.Down, "BOTTOM", ViewKind.Bottom),
    };

    public void Setup(ViewportPanel panel)
    {
        _panel = panel;
        MouseFilter = MouseFilterEnum.Stop;
        TooltipText = "Click a face to look from that side";
    }

    public override void _Process(double delta) => QueueRedraw();

    public override void _Draw()
    {
        if (_panel?.Camera == null) return;
        float s = CubeApp.Instance.UiScale;
        var b = _panel.Camera.GlobalTransform.Basis;
        var right = b.X; var up = b.Y; var fwd = -b.Z;
        var center = Size / 2;
        float half = Size.X * 0.26f;
        Vector2 P(Vector3 v) => center + new Vector2(v.Dot(right), -v.Dot(up)) * half;
        float Depth(Vector3 v) => v.Dot(fwd);

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
        foreach (var (axis, color) in new[] { (Vector3.Right, GizmoRed), (Vector3.Up, GizmoGreen), (Vector3.Back, GizmoBlue) })
            DrawCircle(P(axis * 1.65f), 3 * s, color);
    }

    private static readonly Color GizmoRed = MathConvert.Rgb(0xff2a2a), GizmoGreen = MathConvert.Rgb(0x5aff2a), GizmoBlue = MathConvert.Rgb(0x2a6aff);

    private static (Vector3 u, Vector3 v) Perp(Vector3 n)
    {
        var helper = Mathf.Abs(n.Y) < 0.9f ? Vector3.Up : Vector3.Right;
        var u = helper.Cross(n).Normalized();
        return (u, n.Cross(u));
    }

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
