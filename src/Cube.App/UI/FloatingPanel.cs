using Godot;

namespace Cube.App.UI;

/// <summary>
/// 셸 위에 떠 있는 패널(UV Editor, Material Editor 등). Godot 임베디드 Window 대신 쓴다:
/// 임베디드 창은 포커스를 가진 동안 창 밖의 RMB/MMB 입력을 가로채 뷰포트 파이 메뉴가 열리지 않는다.
/// 제목 바 드래그로 이동, 우하단 모서리(내용 위에 떠 있는 별도 그립 컨트롤이라 UV 캔버스처럼 입력을 잡는 내용이 있어도 동작)로 크기 조절, X로 닫기. 누르면 맨 앞으로 온다.
/// </summary>
/// <remarks>
/// 사용법: 파생 클래스(또는 생성자)가 <see cref="Content"/>에 내용을 넣고 <see cref="Open"/>/<see cref="Close"/>로 표시한다.
/// 도킹(v0.0.30)이 켜져 있으면 제목 바 드래그는 직접 이동 대신 DockManager.BeginDrag로 넘어가고,
/// 도크에 붙으면 <see cref="SetDocked"/>가 제목 바·그립·테두리를 숨겨 탭 안의 일반 패널처럼 보이게 한다.
/// 위치는 부모 컨트롤(셸 오버레이) 기준 로컬 좌표다.
/// </remarks>
public partial class FloatingPanel : PanelContainer
{
    /// <summary>패널이 닫힐 때(✕ 버튼 또는 <see cref="Close"/>) 발생.</summary>
    public event Action? Closed;
    /// <summary>제목 바 텍스트. _Ready 전에 설정하면 보류해 두었다가 라벨을 만들 때 적용한다.</summary>
    public string Title { get => _title.Text; set { if (_title != null) _title.Text = value; else _pendingTitle = value; } }
    /// <summary>_Ready 이전에 설정된 제목(라벨이 아직 없을 때 임시 보관).</summary>
    private string _pendingTitle = "";
    /// <summary>내용을 넣는 세로 컨테이너(제목 바 아래 남는 공간을 모두 채움).</summary>
    public VBoxContainer Content { get; private set; } = null!;
    /// <summary>크기 조절 그립으로 줄일 수 있는 최소 크기(px, 파생 클래스가 UI 배율을 곱해 설정).</summary>
    public Vector2 MinPanelSize = new(300, 200);
    /// <summary>도킹 레이아웃 저장용 고유 ID(비어 있으면 레이아웃에 저장하지 않음).</summary>
    public string PanelId = "";
    /// <summary>도크(Outliner/Properties 영역)의 탭으로 붙어 있음.</summary>
    public bool Docked { get; private set; }
    /// <summary>열려 있음(도크에 붙어 있거나 떠서 보임).</summary>
    public bool IsOpen => Docked || Visible;
    /// <summary>떠 있을 때의 크기(도킹했다 떼어 낼 때 복원).</summary>
    public Vector2 FloatSize;
    /// <summary>마지막으로 붙어 있던 자리. 닫았다 다시 열면 그 자리에 다시 붙는다.</summary>
    public Docking.DockSlot? LastDock;
    /// <summary>제목 바 컨테이너(도킹 시 숨김).</summary>
    private PanelContainer _bar = null!;
    /// <summary>떠 있을 때의 테두리 있는 배경 스타일(도킹했다 떼면 다시 적용).</summary>
    private StyleBox _floatStyle = null!;
    /// <summary>제목 라벨.</summary>
    private Label _title = null!;
    /// <summary>우하단 크기 조절 그립(도킹 시 숨김).</summary>
    private ResizeGrip _grip = null!;
    /// <summary>도크 관리자가 없을 때의 직접 제목 바 드래그 진행 여부.</summary>
    private bool _dragging;
    /// <summary>드래그 시작 시 마우스 전역 위치와 패널 위치의 차(패널이 마우스를 따라가게 유지).</summary>
    private Vector2 _dragOffset;

    /// <summary>
    /// UI를 만든다: 테두리 배경 → 앵커용 호스트 Control → (세로: 제목 바 + Content) 와 우하단 그립을 겹쳐 배치.
    /// 처음에는 숨긴 상태로 시작한다(<see cref="Open"/>으로 표시).
    /// </summary>
    public override void _Ready()
    {
        float s = CubeApp.Instance.UiScale;
        MouseFilter = MouseFilterEnum.Stop;
        AddThemeStyleboxOverride("panel", _floatStyle = new StyleBoxFlat { BgColor = MayaTheme.Panel, BorderColor = MayaTheme.Separator, BorderWidthLeft = 1, BorderWidthRight = 1, BorderWidthTop = 1, BorderWidthBottom = 1, ContentMarginLeft = 2 * s, ContentMarginRight = 2 * s, ContentMarginBottom = 2 * s });
        // PanelContainer는 자식을 전부 꽉 채우므로, 앵커가 먹는 평범한 Control을 하나 두고 그 안에 내용(전체)과 그립(우하단)을 겹친다
        var hostCtl = new Control { MouseFilter = MouseFilterEnum.Pass };
        AddChild(hostCtl);
        var outer = new VBoxContainer();
        outer.AddThemeConstantOverride("separation", 0);
        outer.SetAnchorsPreset(LayoutPreset.FullRect);
        hostCtl.AddChild(outer);
        // 그립: 누르면 맨 앞으로, 드래그 델타만큼 크기를 키우되 MinPanelSize 아래로는 줄이지 않는다(UiPerf로 소요 시간 측정).
        _grip = new ResizeGrip();
        hostCtl.AddChild(_grip);
        _grip.SetAnchorsPreset(LayoutPreset.BottomRight);
        _grip.OffsetLeft = -18 * s; _grip.OffsetTop = -18 * s; _grip.OffsetRight = 0; _grip.OffsetBottom = 0;
        _grip.Began += MoveToFront;
        _grip.Dragged += d => { long t0 = UiPerf.Begin(); Size = new Vector2(Mathf.Max(MinPanelSize.X, Size.X + d.X), Mathf.Max(MinPanelSize.Y, Size.Y + d.Y)); UiPerf.End("panelResize", t0); };
        // 제목 바: 제목 라벨(입력 무시 → 바가 드래그를 받음) + 닫기 버튼.
        var bar = _bar = new PanelContainer { CustomMinimumSize = new Vector2(0, 26 * s), MouseFilter = MouseFilterEnum.Stop };
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
        // 내용 영역은 남는 세로 공간을 모두 차지한다.
        Content = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        outer.AddChild(Content);
        Visible = false;
    }

    /// <summary>
    /// 제목 바 입력. 도크 관리자가 있으면 누르는 즉시 드래그를 넘겨(이동·도킹 미리 보기·놓기는 DockManager가 처리) 끝낸다.
    /// 없으면 직접 이동: 누를 때 오프셋 기억, 모션마다 위치를 갱신하되 제목 바 일부(가로 60px, 세로 30px)는 항상 화면에 남게 자른다.
    /// 이벤트의 GlobalPosition을 쓰므로 DebugDriver가 주입한 입력에서도 동작한다.
    /// </summary>
    private void OnBarInput(InputEvent e)
    {
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left } mb)
        {
            if (mb.Pressed && Docking.DockManager.Instance is { } dm) { dm.BeginDrag(this, mb.GlobalPosition); AcceptEvent(); return; }
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

    /// <summary>떠 있는 패널 아무 곳이나 왼쪽 버튼으로 누르면 다른 패널들 앞으로 가져온다.</summary>
    public override void _GuiInput(InputEvent e)
    {
        if (!Docked && e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true }) MoveToFront();
    }

    /// <summary>떠 있는 패널의 전역 좌상단 위치를 정한다(화면 밖으로 완전히 나가지 않게).</summary>
    public void MoveTo(Vector2 topLeft)
    {
        var host = GetParentControl()?.Size ?? GetViewportRect().Size;
        Position = new Vector2(Mathf.Clamp(topLeft.X, -Size.X + 60, host.X - 60), Mathf.Clamp(topLeft.Y, 0, host.Y - 30));
        _placed = true;
    }

    // (SetDocked 설명이 이 자리에 있던 것) 도크에 붙거나 떨어질 때 제목 바·크기 조절 그립·테두리를 바꾼다(DockManager가 호출).
    /// <summary>도크에 붙거나 떨어진 직후(크기가 바뀌므로 내용을 다시 맞출 때).</summary>
    public event Action? DockChanged;

    /// <summary>
    /// 도킹 상태를 바꾼다. 붙을 때는 현재 크기를 <see cref="FloatSize"/>에 기억하고 테두리 없는 배경으로,
    /// 떨어질 때는 떠 있는 스타일·좌상단 앵커·기억한 크기로 복원한 뒤 <see cref="DockChanged"/>를 알린다.
    /// </summary>
    public void SetDocked(bool docked)
    {
        if (docked && !Docked) FloatSize = Size;
        Docked = docked;
        _bar.Visible = !docked;
        _grip.Visible = !docked;
        if (docked) AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = MayaTheme.Panel });
        else
        {
            AddThemeStyleboxOverride("panel", _floatStyle);
            SetAnchorsPreset(LayoutPreset.TopLeft);
            if (FloatSize.X > 0) Size = FloatSize;
        }
        DockChanged?.Invoke();
    }

    /// <remarks>
    /// 도크에 붙어 있으면 그 탭을 앞으로 가져오고, 닫기 전에 붙어 있던 자리(<see cref="LastDock"/>)가 있으면 다시 붙인다.
    /// 떠서 열 때는 처음 한 번만 호스트 중앙에 놓고(<c>_placed</c>), 이후에는 사용자가 옮긴 위치를 유지한다.
    /// </remarks>
    /// <param name="size">지정하면 열기 전에 패널 크기를 바꾼다.</param>
    /// <summary>처음이면 호스트 가운데에, 이후에는 마지막 위치에 연다.</summary>
    public void Open(Vector2? size = null)
    {
        var dm = Docking.DockManager.Instance;
        if (Docked) { dm?.Focus(this); return; }
        if (LastDock != null && dm != null && dm.Redock(this)) return;
        if (size is { } sz) Size = sz;
        var host = GetParentControl()?.Size ?? GetViewportRect().Size;
        if (!_placed) { Position = (host - Size) / 2; _placed = true; }
        Visible = true;
        MoveToFront();
    }
    /// <summary>한 번이라도 위치가 정해졌는지(처음 열 때 가운데 배치를 한 번만 하기 위함).</summary>
    private bool _placed;

    /// <summary>패널을 닫는다. 도크에 붙어 있으면 먼저 떼어 내되 보이지 않게 하고, 숨긴 뒤 <see cref="Closed"/>를 알린다.</summary>
    public void Close()
    {
        if (Docked) Docking.DockManager.Instance?.Undock(this, show: false);
        Visible = false;
        Closed?.Invoke();
    }
}
