using Cube.App.Bridge;
using Godot;

namespace Cube.App.Viewport;

/// <summary>뷰포트 위 2D 오버레이: 좌하단 축 기즈모, 카메라 이름, 마키 사각형.</summary>
public partial class ViewportOverlay : Control
{
    public Camera3D? Camera;
    public string CameraLabel = "persp";
    public Rect2? Marquee;
    /// <summary>페인트 브러시 원(중심 픽셀, 반지름 픽셀).</summary>
    public (Vector2 center, float radiusPx)? Brush;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);
    }

    public override void _Process(double delta) => QueueRedraw();

    public override void _Draw()
    {
        float s = CubeApp.Instance.UiScale;
        var font = GetThemeDefaultFont();
        int fs = (int)(12 * s);

        // 카메라 이름 (하단 중앙)
        var label = CameraLabel;
        var textSize = font.GetStringSize(label, HorizontalAlignment.Left, -1, fs);
        DrawString(font, new Vector2((Size.X - textSize.X) / 2, Size.Y - 8 * s), label, HorizontalAlignment.Left, -1, fs, MathConvert.Rgb(0xdcdcdc));

        // 축 기즈모 (좌하단)
        if (Camera != null)
        {
            float len = 36 * s;
            var origin = new Vector2(44 * s, Size.Y - 44 * s);
            var b = Camera.GlobalTransform.Basis.Inverse(); // 월드 → 카메라
            (Vector3 axis, Color color, string name)[] axes =
            {
                (Vector3.Right, MathConvert.Rgb(0xff2a2a), "x"),
                (Vector3.Up, MathConvert.Rgb(0x5aff2a), "y"),
                (Vector3.Back, MathConvert.Rgb(0x2a6aff), "z"),
            };
            // 깊이(카메라 z) 순으로 뒤→앞
            var order = axes.Select(a => (a, depth: (b * a.axis).Z)).OrderBy(t => t.depth).ToArray();
            foreach (var (a, _) in order)
            {
                var v = b * a.axis;
                var p2 = origin + new Vector2(v.X, -v.Y) * len;
                DrawLine(origin, p2, a.color, 2 * s, true);
                DrawCircle(p2, 5 * s, a.color);
                DrawString(font, p2 + new Vector2(6 * s, 4 * s), a.name, HorizontalAlignment.Left, -1, fs, a.color);
            }
        }

        if (Brush is { } br)
        {
            DrawArc(br.center, Mathf.Max(br.radiusPx, 2f), 0, Mathf.Tau, 48, new Color(1f, 0.35f, 0.35f, 0.9f), 1.5f * s, true);
            DrawCircle(br.center, 2 * s, new Color(1f, 0.35f, 0.35f, 0.9f));
        }
        // 마키
        if (Marquee is { } r)
        {
            DrawRect(r, new Color(1, 1, 1, 0.08f), true);
            DrawRect(r, new Color(1, 1, 1, 0.9f), false, 1 * s);
        }
    }
}
