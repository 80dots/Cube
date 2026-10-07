using Godot;

namespace Cube.App.UI;

/// <summary>
/// 셸 위에 떠 있는 패널(UV Editor, Material Editor 등). Godot 임베디드 Window 대신 쓴다:
/// 임베디드 창은 포커스를 가진 동안 창 밖의 RMB/MMB 입력을 가로채 뷰포트 파이 메뉴가 열리지 않는다.
/// 제목 바 드래그로 이동, 우하단 모서리로 크기 조절, X로 닫기. 누르면 맨 앞으로 온다.
/// </summary>
public partial class FloatingPanel : PanelContainer
{
    public event Action? Closed;
    public string Title { get => _title.Text; set { if (_title != null) _title.Text = value; else _pendingTitle = value; } }
    private string _pendingTitle = "";
    public VBoxContainer Content { get; private set; } = null!;
    public Vector2 MinPanelSize = new(300, 200);
    private Label _title = null!;
    private bool _dragging, _resizing;
    private Vector2 _dragOffset;

    public override void _Ready()
    {
        float s = CubeApp.Instance.UiScale;
        MouseFilter = MouseFilterEnum.Stop;
        AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = MayaTheme.Panel, BorderColor = MayaTheme.Separator, BorderWidthLeft = 1, BorderWidthRight = 1, BorderWidthTop = 1, BorderWidthBottom = 1, ContentMarginLeft = 2 * s, ContentMarginRight = 2 * s, ContentMarginBottom = 2 * s });
        var outer = new VBoxContainer();
        outer.AddThemeConstantOverride("separation", 0);
        AddChild(outer);
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
            if (mb.Pressed) { _dragging = true; _dragOffset = GetGlobalMousePosition() - GlobalPosition; MoveToFront(); }
            else _dragging = false;
            AcceptEvent();
        }
        else if (e is InputEventMouseMotion && _dragging)
        {
            var host = GetParentControl()?.Size ?? GetViewportRect().Size;
            var p = GetGlobalMousePosition() - _dragOffset;
            Position = new Vector2(Mathf.Clamp(p.X, -Size.X + 60, host.X - 60), Mathf.Clamp(p.Y, 0, host.Y - 30));
            AcceptEvent();
        }
    }

    public override void _GuiInput(InputEvent e)
    {
        float s = CubeApp.Instance.UiScale;
        switch (e)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } mb:
                if (mb.Pressed)
                {
                    MoveToFront();
                    var local = mb.Position;
                    if (local.X > Size.X - 16 * s && local.Y > Size.Y - 16 * s) { _resizing = true; AcceptEvent(); }
                }
                else if (_resizing) { _resizing = false; AcceptEvent(); }
                break;
            case InputEventMouseMotion mm when _resizing:
                Size = new Vector2(Mathf.Max(MinPanelSize.X, mm.Position.X + 8 * s), Mathf.Max(MinPanelSize.Y, mm.Position.Y + 8 * s));
                AcceptEvent();
                break;
        }
    }

    public override void _Draw()
    {
        float s = CubeApp.Instance.UiScale;
        // 우하단 크기 조절 핸들
        var c = Size - new Vector2(3 * s, 3 * s);
        for (int i = 1; i <= 3; i++) DrawLine(c - new Vector2(i * 4 * s, 0), c - new Vector2(0, i * 4 * s), MayaTheme.TextDim, 1 * s);
    }

    public override void _Notification(int what) { if (what == NotificationResized) QueueRedraw(); }

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
