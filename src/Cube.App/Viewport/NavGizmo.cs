using Cube.App.Bridge;
using Cube.Core.Camera;
using Godot;

namespace Cube.App.Viewport;

/// <summary>
/// 뷰포트 우상단 내비게이션 기즈모(v0.0.72, Blender 2.8+ 스타일). 카메라 기저로 월드 축 6방향 끝점을 정사영해 깊이 순(먼 것부터)으로 그린다:
/// + 축은 축 색 원 + 검은 글자(X/Y/Z)와 중심에서 이어지는 선, − 축은 반투명 원 + 축 색 테두리(호버하면 −X 글자).
/// 기즈모 위에 마우스가 오면 뒤에 둥근 배경이 나타나고, 축 원을 클릭하면 그 축에서 보는 직교 뷰(+X Right/−X Left/+Y Top/−Y Bottom/+Z Front/−Z Back),
/// 이미 그 뷰이면 반대편으로 전환(Blender와 같음). 기즈모를 LMB로 끌면 뷰가 돈다(Tumble).
/// </summary>
public partial class NavGizmo : Control
{
    private ViewportPanel _panel = null!;
    /// <summary>Blender 테마 기본 축 색(X #ff3352, Y #8bdc00, Z #2890ff).</summary>
    public static readonly Color AxisX = MathConvert.Rgb(0xff3352), AxisY = MathConvert.Rgb(0x8bdc00), AxisZ = MathConvert.Rgb(0x2890ff);

    /// <summary>축 끝점 정의: 방향, 색, 글자, 클릭 시 뷰.</summary>
    private static readonly (Vector3 dir, Color color, string label, ViewKind kind, bool positive)[] Axes =
    {
        (Vector3.Right, AxisX, "X", ViewKind.Side, true), (Vector3.Left, AxisX, "-X", ViewKind.Left, false),
        (Vector3.Up, AxisY, "Y", ViewKind.Top, true), (Vector3.Down, AxisY, "-Y", ViewKind.Bottom, false),
        (Vector3.Back, AxisZ, "Z", ViewKind.Front, true), (Vector3.Forward, AxisZ, "-Z", ViewKind.Back, false),
    };

    /// <summary>마지막으로 그린 축 원 중심(화면)·반지름(히트 판정; 가까운 것이 뒤에 있음).</summary>
    private readonly List<(Vector2 c, int axis)> _hit = new();
    private int _hoverAxis = -1;
    private bool _hoverGizmo;
    private bool _pressed, _dragging;
    private Vector2 _pressPos;
    private float _circleR;

    public void Setup(ViewportPanel panel)
    {
        _panel = panel;
        MouseFilter = MouseFilterEnum.Stop;
        TooltipText = "Click an axis to look from that side (again to flip). Drag to orbit.";
        MouseEntered += () => { _hoverGizmo = true; QueueRedraw(); };
        MouseExited += () => { _hoverGizmo = false; _hoverAxis = -1; QueueRedraw(); };
    }

    public override void _Process(double delta) => QueueRedraw();

    public override void _Draw()
    {
        if (_panel?.Camera == null) return;
        float s = CubeApp.Instance.UiScale;
        var b = _panel.Camera.GlobalTransform.Basis;
        var right = b.X; var up = b.Y; var fwd = -b.Z;
        var center = Size / 2;
        float radius = Size.X * 0.36f;
        _circleR = 8.5f * s;
        Vector2 P(Vector3 v) => center + new Vector2(v.Dot(right), -v.Dot(up)) * radius;
        // 호버 배경 원
        if (_hoverGizmo || _dragging) DrawCircle(center, Size.X * 0.48f, new Color(0.55f, 0.55f, 0.55f, 0.32f));
        _hit.Clear();
        var font = GetThemeDefaultFont();
        int fs = (int)(10 * s);
        // 먼 축부터(깊이 = 방향·forward, 클수록 멀다)
        var order = Enumerable.Range(0, Axes.Length).OrderByDescending(i => Axes[i].dir.Dot(fwd)).ToArray();
        foreach (int i in order)
        {
            var (dir, color, label, _, positive) = Axes[i];
            var p = P(dir);
            float depth = dir.Dot(fwd);
            // 뒤쪽일수록 살짝 어둡게(Blender처럼 깊이감)
            float dim = 1f - 0.25f * Mathf.Clamp(depth, 0f, 1f);
            var col = new Color(color.R * dim, color.G * dim, color.B * dim, 1f);
            bool hover = _hoverAxis == i;
            if (hover) col = col.Lerp(Colors.White, 0.35f);
            if (positive)
            {
                DrawLine(center, p, col, 2f * s, true);
                DrawCircle(p, _circleR, col);
                var size = font.GetStringSize(label, HorizontalAlignment.Left, -1, fs);
                DrawString(font, p - size / 2 + new Vector2(0, size.Y * 0.72f), label, HorizontalAlignment.Left, -1, fs, new Color(0.05f, 0.05f, 0.05f));
            }
            else
            {
                DrawCircle(p, _circleR, new Color(col.R, col.G, col.B, hover ? 0.8f : 0.35f));
                DrawArc(p, _circleR, 0, Mathf.Tau, 32, col, 1.5f * s, true);
                if (hover)
                {
                    var size = font.GetStringSize(label, HorizontalAlignment.Left, -1, fs);
                    DrawString(font, p - size / 2 + new Vector2(0, size.Y * 0.72f), label, HorizontalAlignment.Left, -1, fs, new Color(0.05f, 0.05f, 0.05f));
                }
            }
            _hit.Add((p, i));
        }
    }

    /// <summary>화면 점에서 가장 앞(마지막에 그린)에 있는 축 원.</summary>
    private int AxisAt(Vector2 px)
    {
        for (int k = _hit.Count - 1; k >= 0; k--) if (_hit[k].c.DistanceTo(px) <= _circleR * 1.3f) return _hit[k].axis;
        return -1;
    }

    public override void _GuiInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseMotion mm:
                if (_pressed)
                {
                    if (!_dragging && (mm.Position - _pressPos).Length() > 3f) _dragging = true;
                    if (_dragging) { _panel.CameraController.Tumble(mm.Relative.X, mm.Relative.Y); AcceptEvent(); }
                }
                else
                {
                    int h = AxisAt(mm.Position);
                    if (h != _hoverAxis) { _hoverAxis = h; QueueRedraw(); }
                }
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } mb:
                if (mb.Pressed) { _pressed = true; _dragging = false; _pressPos = mb.Position; AcceptEvent(); }
                else if (_pressed)
                {
                    _pressed = false;
                    if (!_dragging)
                    {
                        int h = AxisAt(mb.Position);
                        if (h >= 0)
                        {
                            var kind = Axes[h].kind;
                            // 이미 그 뷰(직교)면 반대편으로
                            if (_panel.CameraController.Kind == kind && _panel.CameraController.IsOrtho) kind = Opposite(kind);
                            _panel.SetView(kind);
                        }
                    }
                    _dragging = false;
                    AcceptEvent();
                }
                break;
        }
    }

    private static ViewKind Opposite(ViewKind k) => k switch
    {
        ViewKind.Front => ViewKind.Back, ViewKind.Back => ViewKind.Front,
        ViewKind.Side => ViewKind.Left, ViewKind.Left => ViewKind.Side,
        ViewKind.Top => ViewKind.Bottom, ViewKind.Bottom => ViewKind.Top,
        _ => k,
    };
}

/// <summary>
/// Blender 뷰포트 오른쪽의 끌기 버튼(돋보기 = 줌, 손 = 팬): 아이콘 위에서 LMB를 누른 채 끌면 뷰가 줌/팬된다. 호버하면 둥근 배경.
/// </summary>
public partial class NavDragButton : Control
{
    private Texture2D? _icon; private bool _hover, _pressed; private Action<Vector2>? _drag;
    public void Setup(string icon, string tooltip, Action<Vector2> onDrag)
    {
        float s = CubeApp.Instance.UiScale;
        _icon = UI.Icons.Get(icon, (int)(18 * s)); _drag = onDrag; TooltipText = tooltip;
        CustomMinimumSize = new Vector2(24 * s, 24 * s);
        MouseFilter = MouseFilterEnum.Stop; MouseDefaultCursorShape = CursorShape.Drag;
        MouseEntered += () => { _hover = true; QueueRedraw(); };
        MouseExited += () => { _hover = false; QueueRedraw(); };
    }
    public override void _Draw()
    {
        if (_hover || _pressed) DrawCircle(Size / 2, Size.X * 0.5f, new Color(0.55f, 0.55f, 0.55f, _pressed ? 0.5f : 0.32f));
        if (_icon != null) DrawTexture(_icon, (Size - _icon.GetSize()) / 2);
    }
    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left } mb) { _pressed = mb.Pressed; QueueRedraw(); AcceptEvent(); }
        else if (e is InputEventMouseMotion mm && _pressed) { _drag?.Invoke(mm.Relative); AcceptEvent(); }
    }
}
