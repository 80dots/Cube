using Godot;

namespace Cube.App.UI;

/// <summary>
/// 셸 위에 떠 있는 패널(UV Editor, Material Editor 등). Godot 임베디드 Window 대신 쓴다:
/// 임베디드 창은 포커스를 가진 동안 창 밖의 RMB/MMB 입력을 가로채 뷰포트 파이 메뉴가 열리지 않는다.
/// 제목 바 드래그로 이동, 우하단 모서리(내용 위에 떠 있는 별도 그립 컨트롤이라 UV 캔버스처럼 입력을 잡는 내용이 있어도 동작)로 크기 조절, X로 닫기. 누르면 맨 앞으로 온다.
/// </summary>
public partial class FloatingPanel : PanelContainer
{
    public event Action? Closed;
    public string Title { get => _title.Text; set { if (_title != null) _title.Text = value; else _pendingTitle = value; } }
    private string _pendingTitle = "";
    public VBoxContainer Content { get; private set; } = null!;
    public Vector2 MinPanelSize = new(300, 200);
    private Label _title = null!;
    private ResizeGrip _grip = null!;
    private bool _dragging;
    private Vector2 _dragOffset;

    public override void _Ready()
    {
        float s = CubeApp.Instance.UiScale;
        MouseFilter = MouseFilterEnum.Stop;
        AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = MayaTheme.Panel, BorderColor = MayaTheme.Separator, BorderWidthLeft = 1, BorderWidthRight = 1, BorderWidthTop = 1, BorderWidthBottom = 1, ContentMarginLeft = 2 * s, ContentMarginRight = 2 * s, ContentMarginBottom = 2 * s });
        // PanelContainer는 자식을 전부 꽉 채우므로, 앵커가 먹는 평범한 Control을 하나 두고 그 안에 내용(전체)과 그립(우하단)을 겹친다
        var hostCtl = new Control { MouseFilter = MouseFilterEnum.Pass };
        AddChild(hostCtl);
        var outer = new VBoxContainer();
        outer.AddThemeConstantOverride("separation", 0);
        outer.SetAnchorsPreset(LayoutPreset.FullRect);
        hostCtl.AddChild(outer);
        _grip = new ResizeGrip();
        hostCtl.AddChild(_grip);
        _grip.SetAnchorsPreset(LayoutPreset.BottomRight);
        _grip.OffsetLeft = -18 * s; _grip.OffsetTop = -18 * s; _grip.OffsetRight = 0; _grip.OffsetBottom = 0;
        _grip.Began += MoveToFront;
        _grip.Dragged += d => Size = new Vector2(Mathf.Max(MinPanelSize.X, Size.X + d.X), Mathf.Max(MinPanelSize.Y, Size.Y + d.Y));
        var bar = new PanelContainer { CustomMinimumSize = new Vector2(0, 26 * s), MouseFilter = MouseFilterEnum.Stop };
        bar.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = MayaTheme.PanelDark, ContentMarginLeft = 8 * s });
        var barBox = new HBoxContainer();
        _title = new Label { Text = _pendingTitle, SizeFlagsHorizontal = SizeFlags.ExpandFill, VerticalAlignment = VerticalAlignment.Center, MouseFilter = MouseFilterEnum.Ignore };
        barBox.AddChild(_title);
        var close = new Button { Text = "✕", Flat = true, FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(26 * s, 0) };
        close.Pressed += Close;
        barBox.AddChild(close);
        bar.AddChild(barBox);
        bar.GuiInput += OnBarInput;
        outer.AddChild(bar);
        Content = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        outer.AddChild(Content);
        Visible = false;
    }

    private void OnBarInput(InputEvent e)
    {
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left } mb)
        {
            if (mb.Pressed) { _dragging = true; _dragOffset = mb.GlobalPosition - GlobalPosition; MoveToFront(); }
            else _dragging = false;
            AcceptEvent();
        }
        else if (e is InputEventMouseMotion mm && _dragging)
        {
            var host = GetParentControl()?.Size ?? GetViewportRect().Size;
            var p = mm.GlobalPosition - _dragOffset;
            Position = new Vector2(Mathf.Clamp(p.X, -Size.X + 60, host.X - 60), Mathf.Clamp(p.Y, 0, host.Y - 30));
            AcceptEvent();
        }
    }

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true }) MoveToFront();
    }

    /// <summary>처음이면 호스트 가운데에, 이후에는 마지막 위치에 연다.</summary>
    public void Open(Vector2? size = null)
    {
        if (size is { } sz) Size = sz;
        var host = GetParentControl()?.Size ?? GetViewportRect().Size;
        if (!_placed) { Position = (host - Size) / 2; _placed = true; }
        Visible = true;
        MoveToFront();
    }
    private bool _placed;

    public void Close() { Visible = false; Closed?.Invoke(); }
}
