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
    /// <summary>드래그를 시작할 때(왼쪽 버튼 누름) 한 번 발생.</summary>
    public event Action? Began;
    /// <summary>드래그가 끝날 때(왼쪽 버튼 놓음) 한 번 발생. 도크는 여기서 레이아웃을 저장한다.</summary>
    public event Action? Ended;
    /// <summary>true면 좌하단 그립(대각선 방향이 반대).</summary>
    public bool LeftSide;
    /// <summary>왼쪽 버튼이 눌린 채 드래그 중인지.</summary>
    private bool _down;
    /// <summary>직전 이벤트의 전역 마우스 위치(델타 계산 기준).</summary>
    private Vector2 _last;

    /// <summary>UI 배율에 맞춘 18px 정사각 크기, 방향에 맞는 대각 크기 조절 커서, 부모의 아래쪽 모서리에 붙는 크기 플래그를 설정한다.</summary>
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

    /// <summary>왼쪽 버튼 누름/놓음으로 드래그 상태를 바꾸고, 드래그 중 모션마다 전역 델타를 <see cref="Dragged"/>로 보낸다. 이벤트는 소비해 아래 컨트롤로 전달되지 않는다.</summary>
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

    /// <summary>모서리에서 시작하는 길이 4·8·12px(배율 적용)의 대각선 세 줄을 흐린 글자색으로 그린다.</summary>
    public override void _Draw()
    {
        float s = CubeApp.Instance.UiScale;
        // 좌하단: 왼쪽 아래 모서리를 기준으로 오른쪽 위를 향하는 대각선.
        if (LeftSide)
        {
            var c = new Vector2(3 * s, Size.Y - 3 * s);
            for (int i = 1; i <= 3; i++) DrawLine(c + new Vector2(i * 4 * s, 0), c - new Vector2(0, i * 4 * s), MayaTheme.TextDim, 1 * s);
        }
        // 우하단: 오른쪽 아래 모서리를 기준으로 왼쪽 위를 향하는 대각선.
        else
        {
            var c = Size - new Vector2(3 * s, 3 * s);
            for (int i = 1; i <= 3; i++) DrawLine(c - new Vector2(i * 4 * s, 0), c - new Vector2(0, i * 4 * s), MayaTheme.TextDim, 1 * s);
        }
    }

    /// <summary>크기가 바뀌면 모서리 위치가 달라지므로 다시 그린다.</summary>
    public override void _Notification(int what) { if (what == NotificationResized) QueueRedraw(); }

    /// <remarks>창을 크기 조절 가능하게 바꾸고, 그립을 우하단 앵커에 18px 정사각으로 고정한 뒤, 드래그 델타만큼 창 크기를 늘리되 내용 최소 크기 아래로는 줄이지 않는다.</remarks>
    /// <returns>붙인 그립(호출자가 추가 이벤트를 연결할 수 있음).</returns>
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
