using Godot;

namespace Cube.App.UI;

/// <summary>
/// 우하단(또는 좌하단) 크기 조절 핸들. 세 줄 대각선을 그리고 드래그 델타(전역 px)를 <see cref="Dragged"/>로 알린다.
/// 플로팅 패널·도크·다이얼로그가 공용으로 쓴다. 이벤트의 GlobalPosition을 쓰므로 주입된 입력(DebugDriver)에서도 동작한다.
/// </summary>
public partial class ResizeGrip : Control
{
    /// <summary>드래그 중 마지막 모션 이후의 전역 픽셀 델타.</summary>
    public event Action<Vector2>? Dragged;
    public event Action? Began;
    public event Action? Ended;
    /// <summary>true면 좌하단 그립(대각선 방향이 반대).</summary>
    public bool LeftSide;
    private bool _down;
    private Vector2 _last;

    public override void _Ready()
    {
        float s = CubeApp.Instance.UiScale;
        MouseFilter = MouseFilterEnum.Stop;
        MouseDefaultCursorShape = LeftSide ? CursorShape.Bdiagsize : CursorShape.Fdiagsize;
        TooltipText = "Resize";
        CustomMinimumSize = new Vector2(18 * s, 18 * s);
        SizeFlagsHorizontal = LeftSide ? SizeFlags.ShrinkBegin : SizeFlags.ShrinkEnd;
        SizeFlagsVertical = SizeFlags.ShrinkEnd;
    }

    public override void _GuiInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } mb:
                if (mb.Pressed) { _down = true; _last = mb.GlobalPosition; Began?.Invoke(); }
                else if (_down) { _down = false; Ended?.Invoke(); }
                AcceptEvent();
                break;
            case InputEventMouseMotion mm when _down:
                Dragged?.Invoke(mm.GlobalPosition - _last);
                _last = mm.GlobalPosition;
                AcceptEvent();
                break;
        }
    }

    public override void _Draw()
    {
        float s = CubeApp.Instance.UiScale;
        if (LeftSide)
        {
            var c = new Vector2(3 * s, Size.Y - 3 * s);
            for (int i = 1; i <= 3; i++) DrawLine(c + new Vector2(i * 4 * s, 0), c - new Vector2(0, i * 4 * s), MayaTheme.TextDim, 1 * s);
        }
        else
        {
            var c = Size - new Vector2(3 * s, 3 * s);
            for (int i = 1; i <= 3; i++) DrawLine(c - new Vector2(i * 4 * s, 0), c - new Vector2(0, i * 4 * s), MayaTheme.TextDim, 1 * s);
        }
    }

    public override void _Notification(int what) { if (what == NotificationResized) QueueRedraw(); }

    /// <summary>Godot Window(다이얼로그)의 우하단에 그립을 붙여 창 크기를 바꾼다.</summary>
    public static ResizeGrip AttachToWindow(Window window)
    {
        window.Unresizable = false;
        var grip = new ResizeGrip();
        window.AddChild(grip);
        grip.SetAnchorsPreset(LayoutPreset.BottomRight);
        float s = CubeApp.Instance.UiScale;
        grip.OffsetLeft = -18 * s; grip.OffsetTop = -18 * s; grip.OffsetRight = 0; grip.OffsetBottom = 0;
        grip.Dragged += d =>
        {
            var min = window.GetContentsMinimumSize();
            window.Size = new Vector2I((int)Mathf.Max(min.X, window.Size.X + d.X), (int)Mathf.Max(min.Y, window.Size.Y + d.Y));
        };
        return grip;
    }
}
