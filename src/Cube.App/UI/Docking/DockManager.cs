using Godot;

namespace Cube.App.UI.Docking;

/// <summary>도크 쪽: 왼쪽(툴박스 옆) 또는 오른쪽(뷰포트 옆).</summary>
public enum DockSideKind { Left, Right }

/// <summary>도크 자리: 좌/우 도크와 그 안의 그룹 번호(위에서부터).</summary>
/// <remarks>패널의 <c>LastDock</c>으로 저장되어 닫았다 다시 열 때(<see cref="DockManager.Redock"/>) 같은 자리에 붙이는 데 쓴다. Group은 평탄화한 그룹 목록(<see cref="DockSide.Groups"/>)의 인덱스.</remarks>
public sealed record DockSlot(DockSideKind Side, int Group);

/// <summary>
/// 좌/우 도크 영역: 위아래로 쌓인 항목들. 항목은 탭 그룹(DockGroup) 하나이거나, 그룹 여러 개를 나란히 둔 행(DockRow)이다.
/// 항목 사이 경계를 끌어 높이를, 행 안 경계를 끌어 나란한 그룹의 폭을, 도크와 뷰포트 사이 경계를 끌어 도크 폭을 조절한다.
/// 자식 순서 = 위→아래 표시 순서이며 VSplitContainer의 SplitOffsets가 항목 사이 경계 위치다(저장 시 배율로 나눠 기록).
/// </summary>
public partial class DockSide : VSplitContainer
{
    /// <summary>이 도크가 왼쪽인지 오른쪽인지(DockManager.Setup에서 설정). DockRow가 뷰포트에 붙은 그룹을 정할 때 쓴다.</summary>
    public DockSideKind Kind;
    /// <summary>위에서 아래 순서의 항목(DockGroup 또는 DockRow).</summary>
    public List<Control> Items => GetChildren().OfType<Control>().Where(c => c is DockGroup or DockRow).ToList();
    /// <summary>모든 그룹(행 안의 그룹 포함, 위→아래, 왼→오).</summary>
    public List<DockGroup> Groups => Items.SelectMany(i => i is DockRow r ? r.Groups : new List<DockGroup> { (DockGroup)i }).ToList();

    // 그룹 사이 간격·빈 도크도 불투명하게(뒤가 비쳐 보이지 않게)
    /// <summary>초기화: 내용을 자르고 크기가 바뀌면 배경을 다시 그린다.</summary>
    public override void _Ready() { ClipContents = true; Resized += QueueRedraw; }
    /// <summary>배경을 어두운 패널색으로 칠한다(그룹 사이 간격과 빈 도크가 비치지 않게).</summary>
    public override void _Draw() => DrawRect(new Rect2(Vector2.Zero, Size), MayaTheme.PanelDark);
}

/// <summary>
/// 도크 안에서 탭 그룹 여러 개를 나란히(가로로) 놓는 행.
/// 도크 폭이 바뀌면(도크와 뷰포트 사이 경계를 끌거나 창 크기 변경) 뷰포트에 붙은 그룹만 폭이 바뀌고 나머지 그룹은 폭을 유지한다
/// (오른쪽 도크 = 맨 왼쪽 그룹, 왼쪽 도크 = 맨 오른쪽 그룹). SplitContainer는 모든 자식이 늘어나면 바뀐 폭을 나눠 주므로,
/// 행 폭이 바뀌는 순간(NotificationResized, 정렬 전)에 기억한 폭이 되도록 경계 오프셋을 미리 계산해 둔다(기본 경계 = 남는 폭의 균등 분배, 최소 폭은 클램프에만).
/// 그래서 같은 프레임에 맞고 한 프레임 늦게 튀지 않는다(v0.0.46 전에는 정렬 뒤 실측 보정을 지연 적용해 안쪽 경계가 한 프레임 늦게 따라왔다).
/// 정렬 뒤(SortChildren)에는 실측해 어긋났으면(최소 폭 클램프 등) 지연 보정하고, 맞았으면 지금 폭을 기억한다. 행 안 경계를 끌면 행 폭은 그대로라 새 폭이 기억된다.
/// <see cref="PinWidth"/>는 옆에 붙일 때 원래 그룹 폭을 고정한다(두 그룹짜리 행; 행 안 경계를 끌면 풀림).
/// </summary>
public partial class DockRow : HSplitContainer
{
    /// <summary>행 안의 그룹들(왼→오).</summary>
    public List<DockGroup> Groups => GetChildren().OfType<DockGroup>().ToList();

    /// <summary>마지막으로 기억한 행 전체 폭(-1 = 기억 없음 → 다음 정렬에서 지금 폭을 기억만 하고 보정하지 않음).</summary>
    private float _lastTotal = -1;
    /// <summary>뷰포트에 붙지 않은 그룹들의 기억한 폭(px). 행 폭이 바뀌어도 이 폭을 유지한다.</summary>
    private readonly Dictionary<DockGroup, float> _keep = new();
    /// <summary>고정 폭 핀(그룹, 폭). 옆에 붙인 직후 원래 그룹 폭을 유지하기 위해 쓴다. null = 핀 없음.</summary>
    private (DockGroup g, float w)? _pin;
    /// <summary>연속 지연 보정 횟수(최대 4회; 보정이 수렴하지 않을 때 무한 반복 방지). Learn에서 0으로 초기화.</summary>
    private int _fixes;
    /// <summary>정렬에서 실측한 그룹 사이 간격(드래거 두께).</summary>
    private float _sepReal;

    /// <summary>뷰포트에 붙은(폭이 바뀌는) 그룹 번호.</summary>
    private int AdjacentIndex(int count) => GetParent() is DockSide { Kind: DockSideKind.Left } ? count - 1 : 0;

    /// <summary>초기화: 정렬 완료 시그널 연결, 내용 자르기, 배경 다시 그리기, 행 안 경계 드래그 시 핀·기억 초기화.</summary>
    public override void _Ready()
    {
        SortChildren += OnSorted;
        ClipContents = true;
        Resized += QueueRedraw;
        // 행 안 경계를 끌면 그 폭이 새 기준: 핀을 풀고 다음 정렬에서 기억한다
        Dragged += _ => { _pin = null; _lastTotal = -1; };
    }
    /// <summary>배경을 어두운 패널색으로 칠한다.</summary>
    public override void _Draw() => DrawRect(new Rect2(Vector2.Zero, Size), MayaTheme.PanelDark);

    /// <summary>현재 폭을 기억한다(그룹 추가·제거·레이아웃 복원 직후 등).</summary>
    public void RememberWidths() { _lastTotal = -1; _pin = null; }

    /// <summary>그룹 하나의 폭을 고정한다(옆에 붙일 때 원래 그룹 폭 유지; 나머지 그룹이 남는 폭을 쓴다). 두 그룹짜리 행에서만 쓰며 행 안 경계를 끌면 풀린다.</summary>
    public void PinWidth(DockGroup g, float w) { _pin = (g, w); _lastTotal = -1; _keep.Clear(); }

    /// <remarks>
    /// 규칙: 핀이 있으면(두 그룹 행만) 핀 그룹 = 핀 폭(양쪽 최소 폭으로 클램프), 다른 그룹 = 나머지.
    /// 아니면 뷰포트에 붙은 그룹(adj)을 뺀 모든 그룹이 기억 폭을 가져야 하며, adj는 남는 폭을 쓴다.
    /// 남는 폭이 adj 최소 폭보다 작으면 기억 폭 그룹들을 같은 비율로 줄인다(각자 최소 폭 이상).
    /// </remarks>
    /// <summary>행 폭 avail(경계 제외)에서 원하는 그룹 폭. 규칙이 없으면(기억한 폭 없음) null.</summary>
    private float[]? WantedWidths(List<DockGroup> gs, float avail)
    {
        int n = gs.Count, adj = AdjacentIndex(n);
        var want = new float[n];
        if (_pin is { } pin)
        {
            int pi = gs.IndexOf(pin.g);
            if (pi < 0 || n != 2) return null;
            float minPin = gs[pi].GetCombinedMinimumSize().X, minOther = gs[1 - pi].GetCombinedMinimumSize().X;
            want[pi] = Math.Clamp(pin.w, minPin, Math.Max(minPin, avail - minOther));
            want[1 - pi] = avail - want[pi];
            return want;
        }
        // 기억 폭이 없는 그룹이 하나라도 있으면 규칙을 적용할 수 없다.
        for (int i = 0; i < n; i++) if (i != adj && !_keep.ContainsKey(gs[i])) return null;
        float fixedSum = 0;
        for (int i = 0; i < n; i++) if (i != adj) { want[i] = _keep[gs[i]]; fixedSum += want[i]; }
        float adjMin = gs[adj].GetCombinedMinimumSize().X;
        if (avail - fixedSum < adjMin)
        {
            // 공간이 모자라면 나머지 그룹을 비율대로 줄인다
            float scale = Math.Max(0, avail - adjMin) / Math.Max(1, fixedSum);
            fixedSum = 0;
            for (int i = 0; i < n; i++) if (i != adj) { want[i] = Math.Max(gs[i].GetCombinedMinimumSize().X, want[i] * scale); fixedSum += want[i]; }
        }
        want[adj] = Math.Max(adjMin, avail - fixedSum);
        return want;
    }

    /// <summary>행 폭이 바뀌었을 때 보정이 필요한가(복원 중·기억 없음·폭 그대로면 아니오).</summary>
    private bool NeedsFix(float total) => _pin != null || (DockManager.Settled && _lastTotal >= 0 && Math.Abs(total - _lastTotal) >= 0.5f);

    /// <summary>행 폭이 바뀌는 순간(정렬 전): 원하는 그룹 폭이 되도록 경계 오프셋을 예측해 둔다. 정렬 시그널 안에서는 재정렬 요청이 버려지지만 여기서는 정렬이 아직 대기 중이라 그대로 반영된다.</summary>
    public override void _Notification(int what)
    {
        if (what != NotificationResized || !IsInsideTree()) return;
        // 그룹이 2개 미만이거나 너무 좁거나 보정할 필요가 없으면 SplitContainer 기본 동작에 맡긴다.
        var gs = Groups; int n = gs.Count;
        float total = Size.X;
        if (n < 2 || total < 10 || !NeedsFix(total)) return;
        // 그룹 사이 간격 = 드래거 두께(정렬에서 실측; 테마 separation보다 두껍다)
        float sep = _sepReal > 0 ? _sepReal : Math.Max(GetThemeConstant("separation"), GetThemeConstant("minimum_grab_thickness"));
        float avail = total - sep * (n - 1);
        var want = WantedWidths(gs, avail);
        if (want == null) return;
        // SplitContainer 기본 경계(모든 그룹이 확장): 남는 폭을 균등 분배(최소 폭은 클램프에만 쓰임). 오프셋 = 원하는 경계 − 기본 경계
        var offs = new int[n - 1]; float cumWant = 0;
        for (int i = 0; i < n - 1; i++) { cumWant += want[i]; offs[i] = (int)MathF.Round(cumWant - MathF.Floor((i + 1) * avail / n)); }
        SplitOffsets = offs;
        UiPerf.Count("rowPredict");
    }

    /// <summary>
    /// 자식 정렬 완료 시그널. 간격(드래거 두께)을 실측해 두고, 보정이 필요하면 원하는 폭과 실제 그룹 경계를 비교해
    /// 차이만큼 오프셋을 고쳐 지연 적용한다(최대 4번). 필요 없거나 이미 맞으면 지금 폭을 기억한다.
    /// </summary>
    private void OnSorted()
    {
        UiPerf.Count("rowSorted");
        var gs = Groups; int n = gs.Count;
        float total = Size.X;
        // 그룹이 하나뿐이면 나눌 경계가 없다.
        if (n < 2) { _lastTotal = total; _pin = null; return; }
        float sep = _sepReal = gs[1].Position.X - (gs[0].Position.X + gs[0].Size.X);
        var want = NeedsFix(total) ? WantedWidths(gs, total - sep * (n - 1)) : null;
        if (want == null) { Learn(gs, total); return; }
        // 실측 확인: 예측이 빗나간 경우(최소 폭 클램프 등)만 지연 보정한다(정렬 시그널 안에서 오프셋을 바꾸면 Godot이 재정렬 요청을 버림). 최대 몇 번
        var offs = SplitOffsets.Length == n - 1 ? (int[])SplitOffsets.Clone() : new int[n - 1];
        bool changed = false; float end = 0;
        // 원하는 누적 경계와 실제 그룹 끝 위치의 차이를 오프셋에 더한다.
        for (int i = 0; i < n - 1; i++)
        {
            end += want[i];
            int d = (int)MathF.Round(end - (gs[i].Position.X + gs[i].Size.X));
            if (d != 0) { offs[i] += d; changed = true; }
            end += sep;
        }
        // 지연 보정은 그 사이 행 폭이 또 바뀌지 않았을 때만(바뀌었으면 크기 변경 예측이 이미 새 오프셋을 넣었다)
        if (changed && _fixes++ < 4) { UiPerf.Count("rowFix"); Callable.From(() => { if (IsInstanceValid(this) && Math.Abs(Size.X - total) < 0.5f) SplitOffsets = offs; }).CallDeferred(); }
        else Learn(gs, total, keepPin: _pin != null);
    }

    /// <summary>지금 폭을 기억한다(행 폭이 그대로이거나 보정이 끝났을 때). 핀은 붙이기 뒤 도크 폭 적용이 끝날 때까지 유지한다(행 안 경계 드래그가 푼다).</summary>
    private void Learn(List<DockGroup> gs, float total, bool keepPin = false)
    {
        int adj = AdjacentIndex(gs.Count);
        _keep.Clear();
        for (int i = 0; i < gs.Count; i++) if (i != adj) _keep[gs[i]] = gs[i].Size.X;
        _lastTotal = total; _fixes = 0;
        if (!keepPin) _pin = null;
    }
}

/// <summary>
/// 도크 안의 탭 묶음. 탭을 도크 밖으로 끌면 떠 있는 패널로 떨어지고(곧바로 다른 자리로 끌어 붙일 수 있음),
/// 탭 줄 오른쪽 메뉴(▼)에서 Float / Close를 고를 수 있다.
/// </summary>
public partial class DockGroup : TabContainer
{
    /// <summary>도킹 관리자(Float/TearOff 호출 대상).</summary>
    private DockManager _dm = null!;
    /// <summary>좌버튼을 누른 탭 인덱스(-1 = 없음). 이 탭을 일정 거리 이상 끌면 떼어 낸다.</summary>
    private int _pressTab = -1;
    /// <summary>탭을 누른 전역 위치(끈 거리 계산용).</summary>
    private Vector2 _pressPos;

    /// <summary>
    /// 그룹을 초기화한다: 확장 크기, 탭 자르기, 탭 줄 배경, 탭 줄 오른쪽 팝업 메뉴(Float Panel / Close Panel), 탭 드래그 감지.
    /// </summary>
    /// <param name="dm">도킹 관리자.</param>
    public void Setup(DockManager dm)
    {
        _dm = dm;
        SizeFlagsVertical = SizeFlags.ExpandFill;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        ClipTabs = true;
        // 패널 내용이 그룹보다 커도 그룹 밖(이웃 그룹·뷰포트)으로 넘쳐 그리지 않게 자르고, 탭 줄 빈 곳도 칠한다
        ClipContents = true;
        AddThemeStyleboxOverride("tabbar_background", new StyleBoxFlat { BgColor = MayaTheme.PanelDark });
        var menu = new PopupMenu { Name = "DockMenu" };
        menu.AddItem("Float Panel", 0);
        menu.AddItem("Close Panel", 1);
        // 메뉴는 현재 탭의 패널에 적용된다: 0 = 떼어 띄우기, 1 = 닫기(FloatingPanel.Close가 도크에서 떼고 숨김).
        menu.IdPressed += id =>
        {
            if (GetCurrentTabControl() is not FloatingPanel p) return;
            if (id == 0) _dm.Float(p);
            else p.Close();
        };
        AddChild(menu);
        SetPopup(menu);
        GetTabBar().GuiInput += OnTabInput;
    }

    /// <summary>그룹에 들어 있는 패널들(탭 순서).</summary>
    public IEnumerable<FloatingPanel> Panels => GetChildren().OfType<FloatingPanel>();

    /// <summary>
    /// 탭 줄 입력: 좌버튼 누름 = 누른 탭과 위치 기억, 뗌 = 해제. 누른 채 24px(배율 적용) 이상 움직이면 그 탭 패널을 TearOff로 떼어
    /// 커서를 따라 바로 드래그를 이어 간다.
    /// </summary>
    private void OnTabInput(InputEvent e)
    {
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left } mb)
        {
            _pressTab = mb.Pressed ? GetTabBar().GetTabIdxAtPoint(mb.Position) : -1;
            _pressPos = mb.GlobalPosition;
        }
        else if (e is InputEventMouseMotion mm && _pressTab >= 0 && (mm.ButtonMask & MouseButtonMask.Left) != 0)
        {
            // 탭을 탭 줄 밖으로 일정 거리 이상 끌면 떼어 낸다
            if ((mm.GlobalPosition - _pressPos).Length() < 24 * CubeApp.Instance.UiScale) return;
            int tab = _pressTab; _pressTab = -1;
            if (GetTabControl(tab) is FloatingPanel p) _dm.TearOff(p, mm.GlobalPosition);
        }
    }
}

/// <summary>
/// Maya식 도킹: 떠 있는 패널(UV Editor, Material Editor, Outliner, Properties …)의 제목 바를 끌어 좌/우 도크에 놓으면 탭으로 붙는다.
/// 그룹을 세로로 3등분해 위쪽에 놓으면 위로 분할(새 창이 위), 가운데면 탭으로 추가, 아래쪽이면 아래로 분할(새 창이 아래)하고,
/// 그룹의 왼쪽/오른쪽 가장자리에 놓으면 그 그룹은 그대로 두고 옆에 나란히 붙인다(DockRow). 도크가 비어 있을 때만 뷰포트 가장자리가 그 도크의 자리다.
/// 레이아웃(그룹·탭 순서, 도크 폭, 떠 있는 패널 위치)은 Settings.Dock에 저장되어 다음 실행과 셸 재생성(UI 배율 변경) 때 복원된다.
/// </summary>
/// <remarks>
/// 구조: 셸 레이아웃에서 <c>_mainSplit</c>(왼쪽 행 | 나머지)과 <c>_rightSplit</c>(뷰포트 | 오른쪽 도크) 두 HSplitContainer가 도크 폭을 정한다.
/// 패널 드래그는 제목 바 누름에서 <see cref="BeginDrag"/>로 넘겨받아 <see cref="_Input"/>에서 전역 마우스 이벤트로 이동·놓을 자리 표시·놓기를 처리한다.
/// 도크 폭은 _leftWidth/_rightWidth(px)로 기억하고 <see cref="ApplyWidths"/>가 스플리터 오프셋으로 반영한다. 저장 값은 배율로 나눈 논리 px.
/// </remarks>
public partial class DockManager : Node
{
    /// <summary>현재 도킹 관리자(셸마다 하나). DockRow 등 정적 접근용, 트리에서 나가면 null.</summary>
    public static DockManager? Instance { get; private set; }

    /// <summary>소유 셸(레이아웃 영역, 설정, 패널 부모).</summary>
    private Shell _shell = null!;
    /// <summary>왼쪽 도크(툴박스 옆).</summary>
    public DockSide Left { get; private set; } = null!;
    /// <summary>오른쪽 도크(뷰포트 옆).</summary>
    public DockSide Right { get; private set; } = null!;
    /// <summary>_mainSplit: 왼쪽 행(툴박스+왼쪽 도크)과 나머지 사이 스플리터, _rightSplit: 뷰포트와 오른쪽 도크 사이 스플리터.</summary>
    private HSplitContainer _mainSplit = null!, _rightSplit = null!;
    /// <summary>드래그 중 놓을 자리 강조 오버레이.</summary>
    private DockOverlay _overlay = null!;
    /// <summary>PanelId → 등록된 패널(떠 있는 패널 위치 저장·복원용).</summary>
    private readonly Dictionary<string, FloatingPanel> _panels = new();
    /// <summary>뷰포트 최대화(Ctrl+Space) 중이면 true — 도크를 숨긴다.</summary>
    private bool _maximized;
    /// <summary>패널을 붙이며 도크를 넓힌 기록: 그 패널을 뗄 때 폭이 그대로면 원래 폭으로 되돌린다.</summary>
    private readonly Dictionary<DockSideKind, (FloatingPanel panel, float before, float after)> _widened = new();
    /// <summary>경계(폭/그룹 높이)를 끌었음 → 마우스를 놓을 때 레이아웃 저장.</summary>
    private bool _splitDirty;
    /// <summary>기억한 왼쪽/오른쪽 도크 폭(px, 배율 적용된 실제 값).</summary>
    private float _leftWidth, _rightWidth;

    /// <summary>UI 배율.</summary>
    private static float S => CubeApp.Instance.UiScale;
    /// <summary>도크 최소 폭(px).</summary>
    private float SideMin => 120 * S;

    /// <summary>
    /// 놓기 방식: Tab = 그룹에 탭 추가, Before/After = 그룹(또는 행) 위/아래에 새 그룹, LeftOf/RightOf = 그룹 옆에 나란히(DockRow),
    /// NewGroup = 빈 도크(또는 그룹 없음)에 새 그룹.
    /// </summary>
    public enum DropMode { Tab, Before, After, LeftOf, RightOf, NewGroup }
    /// <summary>놓을 자리: 도크 쪽, 대상 그룹(NewGroup이면 null), 방식, 오버레이로 보여 줄 전역 사각형.</summary>
    public sealed record DropTarget(DockSideKind Side, DockGroup? Group, DropMode Mode, Rect2 Highlight);

    /// <summary>
    /// 관리자를 초기화한다: 도크 종류·최소 폭 설정, 오버레이 생성, 스플리터/도크/행 경계 드래그를 감지해 마우스를 놓을 때 레이아웃을 저장하도록 연결.
    /// </summary>
    public void Setup(Shell shell, DockSide left, DockSide right, HSplitContainer mainSplit, HSplitContainer rightSplit)
    {
        Instance = this;
        _shell = shell; Left = left; Right = right; _mainSplit = mainSplit; _rightSplit = rightSplit;
        left.Kind = DockSideKind.Left; right.Kind = DockSideKind.Right;
        left.CustomMinimumSize = new Vector2(SideMin, 0);
        right.CustomMinimumSize = new Vector2(SideMin, 0);
        _overlay = new DockOverlay { Name = "DockOverlay", Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore, TopLevel = true };
        shell.AddChild(_overlay);
        // 사용자가 경계를 끌어 바꾼 폭을 기억한다
        // 사용자가 경계를 끌어 바꾼 폭은 레이아웃이 갱신된 다음 프레임에 기록한다
        _mainSplit.Dragged += _ => { _splitDirty = true; Callable.From(CaptureWidths).CallDeferred(); };
        _rightSplit.Dragged += _ => { _splitDirty = true; Callable.From(CaptureWidths).CallDeferred(); };
        left.Dragged += _ => _splitDirty = true;
        right.Dragged += _ => _splitDirty = true;
        // 행(나란한 그룹) 경계 드래그도 저장: 행은 나중에 생기므로 트리에 들어올 때 연결
        shell.GetTree().NodeAdded += n => { if (n is DockRow row && !row.HasMeta("dockHooked")) { row.SetMeta("dockHooked", true); row.Dragged += _ => _splitDirty = true; } };
    }

    /// <summary>트리에서 나갈 때 정적 인스턴스를 비운다(셸 재생성 시 이전 관리자 참조 방지).</summary>
    public override void _ExitTree() { if (Instance == this) Instance = null; }

    /// <summary>패널을 PanelId로 등록한다(ID가 빈 패널은 등록하지 않음).</summary>
    public void Register(FloatingPanel p) { if (p.PanelId.Length > 0) _panels[p.PanelId] = p; }

    /// <summary>도크 종류 → 도크 컨트롤.</summary>
    public DockSide SideOf(DockSideKind k) => k == DockSideKind.Left ? Left : Right;

    // ---------------------------------------------------------------- 붙이기 / 떼기

    /// <summary>패널을 대상 자리에 붙인다(이미 붙어 있으면 먼저 뗀다).</summary>
    /// <param name="p">붙일 패널.</param>
    /// <param name="t">놓을 자리.</param>
    /// <param name="widen">true면 필요할 때 도크를 넓힌다(Redock처럼 사용자 폭을 유지할 때는 false).</param>
    public void Dock(FloatingPanel p, DropTarget t, bool widen = true)
    {
        if (p.Docked) Undock(p, show: false);
        var side = SideOf(t.Side);
        // 대상 그룹이 이미 사라졌으면(떼면서 빈 그룹이 지워진 경우 등) 대상 없음으로 본다.
        var target = t.Group != null && IsInstanceValid(t.Group) && t.Group.IsInsideTree() ? t.Group : null;
        DockGroup g;
        // 탭으로 추가.
        if (t.Mode == DropMode.Tab && target != null) g = target;
        else if (target != null && t.Mode is DropMode.LeftOf or DropMode.RightOf)
        {
            // 옆에 나란히: 대상 그룹이 행 안이면 그 행에 끼우고, 아니면 대상을 행으로 감싼다
            g = NewGroup();
            float keepW = target.Size.X; // 원래 그룹은 지금 폭을 유지하고 새 그룹이 늘어난 폭을 쓴다
            if (target.GetParent() is DockRow row)
            {
                row.AddChild(g);
                row.MoveChild(g, row.Groups.IndexOf(target) + (t.Mode == DropMode.RightOf ? 1 : 0));
                row.SplitOffsets = new int[Math.Max(0, row.Groups.Count - 1)];
                row.RememberWidths();
            }
            else
            {
                int idx = side.Items.IndexOf(target);
                var newRow = new DockRow { Name = "DockRow", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
                side.AddChild(newRow);
                side.MoveChild(newRow, Math.Max(0, idx));
                target.Reparent(newRow, keepGlobalTransform: false);
                newRow.AddChild(g);
                if (t.Mode == DropMode.LeftOf) newRow.MoveChild(g, 0);
                newRow.SplitOffsets = new int[1];
            }
            Attach(p, g);
            if (widen) WidenBy(side, p);
            // 원래 그룹은 지금 폭을 유지: 행이 처음 정렬되는 순간(같은 프레임)과 도크가 넓어질 때 DockRow가 경계를 미리 맞춘다
            if (g.GetParent() is DockRow r3 && r3.Groups.Count == 2) r3.PinWidth(target, keepW);
            return;
        }
        else
        {
            // 위/아래 분할 또는 새 그룹.
            g = NewGroup();
            // 위/아래 분할: 대상이 행 안이면 그 행 전체의 위/아래에 넣는다
            Control anchor = target?.GetParent() is DockRow r2 ? r2 : target!;
            int idx = target == null ? side.Items.Count : side.Items.IndexOf(anchor) + (t.Mode == DropMode.After ? 1 : 0);
            side.AddChild(g);
            side.MoveChild(g, Math.Clamp(idx, 0, side.GetChildCount() - 1));
            ResetGroupHeights(side);
        }
        Attach(p, g);
        if (widen) EnsureWidth(side, p);
    }

    /// <summary>관리자에 연결된 새 탭 그룹을 만든다(트리에는 호출자가 넣는다).</summary>
    private DockGroup NewGroup()
    {
        var g = new DockGroup { Name = "DockGroup" };
        g.Setup(this);
        return g;
    }

    /// <summary>
    /// 패널을 그룹의 탭으로 옮긴다: 도킹 표시(제목 바·그립 숨김) → 기존 부모에서 빼고 그룹에 추가 → 탭 제목·현재 탭 설정 → LastDock 기록 → 도크 표시 갱신.
    /// </summary>
    private void Attach(FloatingPanel p, DockGroup g)
    {
        p.SetDocked(true);
        p.GetParent()?.RemoveChild(p);
        g.AddChild(p);
        int idx = g.GetTabIdxFromControl(p);
        g.SetTabTitle(idx, p.Title);
        g.CurrentTab = idx;
        p.Visible = true;
        if (SideOfGroup(g) is { } side) p.LastDock = new DockSlot(side.Kind, side.Groups.IndexOf(g));
        UpdateSides();
    }

    /// <summary>도크에서 떼어 셸 위의 떠 있는 패널로 되돌린다. show=false면 숨긴다(닫기).</summary>
    public void Undock(FloatingPanel p, bool show)
    {
        if (!p.Docked) return;
        // 다시 열 때 같은 자리로 붙이도록 마지막 자리를 기록하고 빈 그룹은 지운다.
        var g = p.GetParent() as DockGroup;
        var side = g == null ? null : SideOfGroup(g);
        if (g != null && side != null) p.LastDock = new DockSlot(side.Kind, side.Groups.IndexOf(g));
        g?.RemoveChild(p);
        if (g != null && side != null && !g.Panels.Any()) RemoveGroup(side, g);
        // 이 패널 때문에 넓혔던 도크는, 사용자가 그 뒤 폭을 바꾸지 않았다면 원래 폭으로 되돌린다.
        if (side != null && _widened.TryGetValue(side.Kind, out var w) && w.panel == p)
        {
            _widened.Remove(side.Kind);
            float cur = side.Kind == DockSideKind.Left ? _leftWidth : _rightWidth;
            if (Math.Abs(cur - w.after) < 1f) SetSideWidth(side.Kind, w.before);
        }
        // 셸 위의 떠 있는 패널로 되돌린다.
        _shell.AddChild(p);
        p.SetDocked(false);
        p.Visible = show;
        if (show) p.MoveToFront();
        UpdateSides();
    }

    /// <summary>탭 메뉴 Float: 떼어 내 화면 가운데쯤에 띄운다.</summary>
    public void Float(FloatingPanel p)
    {
        var at = p.GetGlobalRect().Position + new Vector2(40, 40) * S;
        Undock(p, show: true);
        p.MoveTo(at);
        SaveLayout();
    }

    /// <summary>탭을 끌어 떼어 낸 직후: 커서에 제목 바가 오도록 띄우고 바로 드래그를 이어 간다.</summary>
    public void TearOff(FloatingPanel p, Vector2 mouse)
    {
        Undock(p, show: true);
        if (p.Size.X < 200 * S) p.Size = new Vector2(360, 300) * S;
        p.MoveTo(mouse - new Vector2(60, 12) * S);
        BeginDrag(p, mouse);
    }

    /// <summary>붙어 있는 패널의 탭을 앞으로.</summary>
    public void Focus(FloatingPanel p)
    {
        if (p.GetParent() is DockGroup g) g.CurrentTab = g.GetTabIdxFromControl(p);
    }

    /// <summary>닫았던 패널을 마지막 자리에 다시 붙인다(그 그룹이 없으면 그 쪽 도크 맨 아래 새 그룹).</summary>
    public bool Redock(FloatingPanel p)
    {
        if (p.LastDock is not { } slot) return false;
        var groups = SideOf(slot.Side).Groups;
        var g = slot.Group >= 0 && slot.Group < groups.Count ? groups[slot.Group] : null;
        Dock(p, new DropTarget(slot.Side, g, g != null ? DropMode.Tab : DropMode.NewGroup, default), widen: false); // 다시 열 때는 사용자가 정한 폭 유지
        SaveLayout();
        return true;
    }

    // ---------------------------------------------------------------- 크기

    /// <summary>뷰포트 최대화(Ctrl+Space) 동안 도크를 숨긴다.</summary>
    public void SetMaximized(bool on) { if (on) CaptureWidths(); _maximized = on; UpdateSides(); }

    /// <summary>도크 표시 여부(최대화 중이 아니고 그룹이 있을 때)를 갱신하고 다음 프레임에 폭을 적용한다.</summary>
    private void UpdateSides()
    {
        Left.Visible = !_maximized && Left.Groups.Count > 0;
        Right.Visible = !_maximized && Right.Groups.Count > 0;
        Callable.From(ApplyWidths).CallDeferred();
    }

    /// <summary>현재 보이는 도크의 실제 폭을 기억한다(경계 드래그 후·최대화 전).</summary>
    private void CaptureWidths()
    {
        if (Left.Visible && Left.Size.X > 1) _leftWidth = Left.Size.X;
        if (Right.Visible && Right.Size.X > 1) _rightWidth = Right.Size.X;
    }

    /// <summary>
    /// 저장된 폭을 스플리터 오프셋으로 적용한다. 왼쪽 행(툴박스+도크)은 확장하지 않으므로 오프셋 = 왼쪽 행 전체 폭,
    /// 오른쪽 도크도 확장하지 않으므로 오프셋 = −도크 폭(기본 위치가 컨테이너 끝).
    /// </summary>
    private void ApplyWidths()
    {
        if (!IsInstanceValid(Left)) return;
        var leftRow = Left.GetParent<Control>();
        float extra = leftRow.GetCombinedMinimumSize().X - Left.GetCombinedMinimumSize().X; // 툴박스 + 간격
        _mainSplit.SplitOffsets = new[] { Left.Visible ? (int)(Math.Max(SideMin, _leftWidth) + extra) : 0 };
        _rightSplit.SplitOffsets = new[] { Right.Visible ? -(int)Math.Max(SideMin, _rightWidth) : 0 };
    }

    /// <summary>도크 폭을 설정하고 다음 프레임에 스플리터에 적용한다.</summary>
    public void SetSideWidth(DockSideKind k, float w)
    {
        if (k == DockSideKind.Left) _leftWidth = w; else _rightWidth = w;
        Callable.From(ApplyWidths).CallDeferred();
    }

    /// <summary>큰 패널(UV Editor 등)을 붙이면 도크를 그 패널의 떠 있을 때 폭까지(창의 45% 이내) 넓힌다.</summary>
    private void EnsureWidth(DockSide side, FloatingPanel p)
    {
        float want = Math.Max(p.FloatSize.X, p.MinPanelSize.X);
        float max = _shell.GetViewport().GetVisibleRect().Size.X * 0.45f;
        want = Math.Min(want, max);
        float cur = side.Kind == DockSideKind.Left ? _leftWidth : _rightWidth;
        if (want > cur)
        {
            SetSideWidth(side.Kind, want);
            _widened[side.Kind] = (p, cur, want);
        }
    }

    /// <summary>도크 항목 사이 경계를 기본(균등)으로 되돌린다.</summary>
    private static void ResetGroupHeights(DockSide side) => side.SplitOffsets = new int[Math.Max(0, side.Items.Count - 1)];

    /// <summary>그룹이 속한 도크(직접 자식이거나 행 안). 트리에 없으면 null.</summary>
    private static DockSide? SideOfGroup(DockGroup g) => g.GetParent() switch { DockSide s => s, DockRow r => r.GetParent() as DockSide, _ => null };

    /// <summary>빈 그룹을 지운다. 행에 그룹이 하나만 남으면 행을 풀어 그 그룹을 도크 항목으로 되돌린다.</summary>
    private static void RemoveGroup(DockSide side, DockGroup g)
    {
        var parent = g.GetParent();
        parent.RemoveChild(g);
        g.QueueFree();
        if (parent is DockRow row)
        {
            var rest = row.Groups;
            if (rest.Count <= 1)
            {
                int idx = side.Items.IndexOf(row);
                foreach (var left in rest) { left.Reparent(side, keepGlobalTransform: false); side.MoveChild(left, Math.Max(0, idx)); }
                side.RemoveChild(row);
                row.QueueFree();
            }
            else { row.SplitOffsets = new int[rest.Count - 1]; row.RememberWidths(); }
        }
        ResetGroupHeights(side);
    }

    /// <summary>옆에 나란히 붙이면 도크를 새 패널 폭만큼 넓힌다(창의 60% 이내).</summary>
    private void WidenBy(DockSide side, FloatingPanel p)
    {
        float cur = side.Kind == DockSideKind.Left ? _leftWidth : _rightWidth;
        float add = Math.Max(Math.Max(p.FloatSize.X, p.MinPanelSize.X), SideMin);
        float want = Math.Min(cur + add, _shell.GetViewport().GetVisibleRect().Size.X * 0.6f);
        if (want > cur) { SetSideWidth(side.Kind, want); _widened[side.Kind] = (p, cur, want); }
    }

    // ---------------------------------------------------------------- 드래그

    /// <summary>드래그 중인 패널(null = 드래그 아님).</summary>
    private FloatingPanel? _drag;
    /// <summary>커서와 패널 좌상단의 차이(패널이 커서를 같은 상대 위치로 따라가게).</summary>
    private Vector2 _dragOffset;
    /// <summary>현재 커서 아래의 놓을 자리(없으면 null = 그냥 떠 있는 위치로 이동).</summary>
    private DropTarget? _target;

    /// <summary>패널 드래그 중인지.</summary>
    public bool IsDragging => _drag != null;

    /// <summary>떠 있는 패널 제목 바를 누르면 시작. 이후 이동/놓기는 _Input에서 전역으로 처리한다(누른 컨트롤이 사라져도 계속).</summary>
    public void BeginDrag(FloatingPanel p, Vector2 mouse)
    {
        _drag = p;
        _dragOffset = mouse - p.GlobalPosition;
        p.MoveToFront();
        _overlay.MoveToFront();
    }

    /// <summary>
    /// 전역 입력: 경계 드래그 후 좌버튼을 놓으면 폭 기억 + 레이아웃 저장(지연). 패널 드래그 중이면 이동 = 패널 이동 + 놓을 자리 탐색/표시,
    /// 좌버튼 뗌 = 자리가 있으면 붙이고 저장, Esc = 드래그 취소(패널은 지금 위치에 떠 있음).
    /// </summary>
    public override void _Input(InputEvent e)
    {
        if (_splitDirty && e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false })
        {
            _splitDirty = false;
            Callable.From(() => { CaptureWidths(); SaveLayout(); }).CallDeferred();
        }
        if (_drag == null) return;
        if (e is InputEventMouseMotion mm)
        {
            long t0 = UiPerf.Begin();
            _drag.MoveTo(mm.GlobalPosition - _dragOffset);
            UiPerf.End("dockMove", t0); t0 = UiPerf.Begin();
            _target = FindTarget(mm.GlobalPosition);
            _overlay.ShowRect(_target?.Highlight);
            UiPerf.End("dockTarget", t0);
        }
        else if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false })
        {
            var p = _drag; var t = _target;
            _drag = null; _target = null;
            _overlay.ShowRect(null);
            if (t != null) Dock(p, t);
            SaveLayout();
        }
        else if (e is InputEventKey { Keycode: Key.Escape, Pressed: true })
        {
            _drag = null; _target = null; _overlay.ShowRect(null);
        }
    }

    /// <summary>
    /// 커서 아래의 놓을 자리. 그룹 위: 왼쪽/오른쪽 가장자리 띠(폭의 22%, 최대 70px) = 옆에 나란히,
    /// 나머지는 세로 3등분 — 위 = 위로 분할(새 창이 위), 가운데 = 탭 추가, 아래 = 아래로 분할(새 창이 아래).
    /// 도크 바로 옆 뷰포트 가장자리(48px)도 그 높이의 그룹 옆(나란히)으로 본다. 빈 도크는 뷰포트 가장자리가 그 도크 자리.
    /// </summary>
    public DropTarget? FindTarget(Vector2 m)
    {
        // 1) 보이는 도크의 그룹 위: 좌우 가장자리 띠 → 옆에, 위/가운데/아래 3등분 → 위 분할/탭/아래 분할.
        foreach (var side in new[] { Left, Right })
        {
            if (!side.IsVisibleInTree()) continue;
            foreach (var g in side.Groups)
            {
                var r = g.GetGlobalRect();
                if (!r.HasPoint(m)) continue;
                float sideBand = Math.Min(r.Size.X * 0.22f, 70 * S);
                var half = new Vector2(r.Size.X * 0.5f, r.Size.Y);
                if (m.X < r.Position.X + sideBand) return new DropTarget(side.Kind, g, DropMode.LeftOf, new Rect2(r.Position, half));
                if (m.X > r.End.X - sideBand) return new DropTarget(side.Kind, g, DropMode.RightOf, new Rect2(r.Position + new Vector2(half.X, 0), half));
                float third = r.Size.Y / 3f;
                var halfH = new Vector2(r.Size.X, r.Size.Y * 0.5f);
                if (m.Y < r.Position.Y + third) return new DropTarget(side.Kind, g, DropMode.Before, new Rect2(r.Position, halfH));
                if (m.Y > r.End.Y - third) return new DropTarget(side.Kind, g, DropMode.After, new Rect2(r.Position + new Vector2(0, halfH.Y), halfH));
                return new DropTarget(side.Kind, g, DropMode.Tab, r);
            }
        }
        // 2) 뷰포트(레이아웃 영역) 좌우 가장자리 48px.
        var lr = _shell.Layout.GetGlobalRect();
        float edge = 48 * S;
        if (lr.HasPoint(m))
        {
            float w = Math.Min(260 * S, lr.Size.X / 3);
            // 도크 쪽 가장자리에서 커서 높이와 겹치는 그룹 중 뷰포트에 가장 가까운 것 옆에 놓는 자리.
            DropTarget? Beside(DockSide side, DropMode mode)
            {
                // 도크가 보이면 그 높이의 그룹(도크와 맞닿은 열) 옆에 나란히
                var g = side.Groups.Where(x => { var rr = x.GetGlobalRect(); return m.Y >= rr.Position.Y && m.Y <= rr.End.Y; })
                    .OrderBy(x => mode == DropMode.LeftOf ? x.GetGlobalRect().Position.X : -x.GetGlobalRect().End.X).FirstOrDefault();
                if (g == null) return null;
                var r = g.GetGlobalRect(); var half = new Vector2(r.Size.X * 0.5f, r.Size.Y);
                return new DropTarget(side.Kind, g, mode, mode == DropMode.LeftOf ? new Rect2(r.Position, half) : new Rect2(r.Position + new Vector2(half.X, 0), half));
            }
            if (m.X < lr.Position.X + edge)
                return Left.Groups.Count == 0 ? new DropTarget(DockSideKind.Left, null, DropMode.NewGroup, new Rect2(lr.Position, new Vector2(w, lr.Size.Y))) : Beside(Left, DropMode.RightOf);
            if (m.X > lr.End.X - edge)
                return Right.Groups.Count == 0 ? new DropTarget(DockSideKind.Right, null, DropMode.NewGroup, new Rect2(new Vector2(lr.End.X - w, lr.Position.Y), new Vector2(w, lr.Size.Y))) : Beside(Right, DropMode.LeftOf);
        }
        return null;
    }

    // ---------------------------------------------------------------- 저장 / 복원

    /// <summary>
    /// 현재 레이아웃을 Settings.Dock에 기록하고 저장한다: 도크별 그룹/행 구조(패널 ID), 행 안 경계, 항목 경계, 활성 탭, 도크 폭, 떠 있는 패널 위치·크기.
    /// 크기 값은 UI 배율로 나눠 저장(배율이 바뀌어도 같은 비율로 복원). 옛 형식(Left/Right = 그룹 목록)도 호환용으로 함께 쓴다.
    /// </summary>
    public void SaveLayout()
    {
        if (!IsInstanceValid(Left)) return;
        var d = _shell.Settings.Dock;
        // 직렬화 헬퍼: 그룹 목록(옛 형식), 그룹의 패널 ID, 항목 → 나란한 그룹 → 탭 ID, 항목별 행 안 경계.
        static List<List<string>> Ids(DockSide side) => side.Groups.Select(g => g.Panels.Where(p => p.PanelId.Length > 0).Select(p => p.PanelId).ToList()).Where(l => l.Count > 0).ToList();
        static List<string> GroupIds(DockGroup g) => g.Panels.Where(p => p.PanelId.Length > 0).Select(p => p.PanelId).ToList();
        static List<List<List<string>>> Rows(DockSide side) => side.Items
            .Select(i => (i is DockRow r ? r.Groups : new List<DockGroup> { (DockGroup)i }).Select(GroupIds).Where(l => l.Count > 0).ToList())
            .Where(l => l.Count > 0).ToList();
        static List<List<float>> RowSplits(DockSide side) => side.Items.Select(i => i is DockRow r ? r.SplitOffsets.Select(o => o / S).ToList() : new List<float>()).ToList();
        d.Left = Ids(Left);
        d.Right = Ids(Right);
        d.LeftRows = Rows(Left); d.RightRows = Rows(Right);
        d.LeftRowSplits = RowSplits(Left); d.RightRowSplits = RowSplits(Right);
        d.LeftSplits = Left.SplitOffsets.Select(o => o / S).ToList();
        d.RightSplits = Right.SplitOffsets.Select(o => o / S).ToList();
        d.Active = Left.Groups.Concat(Right.Groups).Select(g => (g.GetCurrentTabControl() as FloatingPanel)?.PanelId ?? "").Where(id => id.Length > 0).ToList();
        if (_leftWidth > 0) d.LeftWidth = _leftWidth / S;
        if (_rightWidth > 0) d.RightWidth = _rightWidth / S;
        d.Floating = _panels.Values.Where(p => !p.Docked && p.Visible && IsInstanceValid(p))
            .Select(p => new FloatingPanelState { Id = p.PanelId, X = p.Position.X, Y = p.Position.Y, W = p.Size.X, H = p.Size.Y }).ToList();
        _shell.Settings.Save();
    }

    /// <summary>저장된 레이아웃을 복원한다. ensure(id)는 패널을 만들어(아직 없으면) 돌려준다(null = 복원하지 않는 패널).</summary>
    /// <summary>레이아웃 복원이 끝나 도크 폭이 자리 잡았는지. 그 전에는 DockRow가 그룹 폭을 보정하지 않고 기억만 한다.</summary>
    public static bool Settled { get; private set; }

    /// <summary>
    /// 저장된 레이아웃을 복원한다. ensure(id)는 패널을 만들어(아직 없으면) 돌려준다(null = 복원하지 않는 패널).
    /// 순서: 0.5초 뒤 Settled 표시 예약 → 도크 폭 → 도크별 항목(행이면 그룹들을 만든 뒤 DockRow로 묶고 행 안 경계 적용) → 항목 경계 → 떠 있는 패널 위치 → 표시 갱신.
    /// LeftRows/RightRows가 없으면(옛 설정) 그룹마다 한 항목으로 본다.
    /// </summary>
    public void RestoreLayout(Func<string, FloatingPanel?> ensure)
    {
        Settled = false;
        GetTree().CreateTimer(0.5).Timeout += () => Settled = true;
        var d = _shell.Settings.Dock;
        // 저장된 폭이 없으면 기본 왼쪽 220, 오른쪽 260(논리 px).
        _leftWidth = (d.LeftWidth > 0 ? d.LeftWidth : 220) * S;
        _rightWidth = (d.RightWidth > 0 ? d.RightWidth : 260) * S;
        foreach (var (kind, rows, rowSplits) in new[] { (DockSideKind.Left, d.LeftRows ?? d.Left.Select(g => new List<List<string>> { g }).ToList(), d.LeftRowSplits), (DockSideKind.Right, d.RightRows ?? d.Right.Select(g => new List<List<string>> { g }).ToList(), d.RightRowSplits) })
        {
            var side = SideOf(kind);
            for (int ri = 0; ri < rows.Count; ri++)
            {
                var made = new List<DockGroup>();
                // 그룹 하나 = 탭 ID 목록. 이미 붙어 있거나 만들 수 없는 패널은 건너뛴다.
                foreach (var ids in rows[ri])
                {
                    DockGroup? g = null;
                    foreach (var id in ids)
                    {
                        var p = ensure(id);
                        if (p == null || p.Docked) continue;
                        if (g == null) { g = NewGroup(); side.AddChild(g); }
                        Attach(p, g);
                    }
                    if (g == null) continue;
                    // 저장된 활성 탭을 앞으로.
                    var active = g.Panels.FirstOrDefault(pp => d.Active.Contains(pp.PanelId));
                    g.CurrentTab = active != null ? g.GetTabIdxFromControl(active) : 0;
                    made.Add(g);
                }
                if (made.Count > 1)
                {
                    // 나란한 그룹들을 행으로 묶는다
                    var row = new DockRow { Name = "DockRow", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
                    int idx = made[0].GetIndex();
                    side.AddChild(row); side.MoveChild(row, idx);
                    foreach (var g in made) g.Reparent(row, keepGlobalTransform: false);
                    var rs = rowSplits != null && ri < rowSplits.Count ? rowSplits[ri] : null;
                    row.SplitOffsets = rs != null && rs.Count == made.Count - 1 ? rs.Select(v => (int)(v * S)).ToArray() : new int[made.Count - 1];
                }
            }
            // 항목 수가 저장 당시와 같을 때만 항목 경계를 복원한다.
            ResetGroupHeights(side);
            var splits = kind == DockSideKind.Left ? d.LeftSplits : d.RightSplits;
            if (splits.Count == side.Items.Count - 1 && splits.Count > 0) side.SplitOffsets = splits.Select(v => (int)(v * S)).ToArray();
        }
        // 떠 있던 패널들을 같은 위치·크기로 다시 띄운다.
        foreach (var f in d.Floating)
        {
            var p = ensure(f.Id);
            if (p == null || p.Docked) continue;
            if (f.W > 0 && f.H > 0) p.Size = new Vector2(f.W, f.H);
            p.Visible = true;
            p.MoveTo(new Vector2(f.X, f.Y));
        }
        UpdateSides();
    }

    /// <summary>디버그/표시용 레이아웃 요약: "L[outliner|uvEditor] R[properties]".</summary>
    public string Summary()
    {
        static string G(DockGroup g) => "[" + string.Join("|", g.Panels.Select(p => p.PanelId + (g.GetCurrentTabControl() == p ? "*" : ""))) + "]";
        static string Side(DockSide s) => string.Join(" / ", s.Items.Select(i => i is DockRow r ? "(" + string.Join(" ", r.Groups.Select(G)) + ")" : G((DockGroup)i)));
        string groupsY = string.Join(",", Right.Groups.Select(g => $"{g.GlobalPosition.X:0},{g.GlobalPosition.Y:0}-{g.GlobalPosition.X + g.Size.X:0},{g.GlobalPosition.Y + g.Size.Y:0}"));
        string leftX = string.Join(",", Left.Groups.Select(g => $"{g.GlobalPosition.X:0}-{g.GlobalPosition.X + g.Size.X:0}"));
        return $"leftGroupsX={leftX} rightX={Right.GlobalPosition.X:0} rightGroupsY={groupsY} offsets={_mainSplit.SplitOffsets[0]},{_rightSplit.SplitOffsets[0]} want={_leftWidth:0},{_rightWidth:0} L{(Left.Visible ? Left.Size.X.ToString("0") : "-")} {Side(Left)}  R{(Right.Visible ? Right.Size.X.ToString("0") : "-")} {Side(Right)}  floating={string.Join(",", _panels.Values.Where(p => !p.Docked && p.Visible).Select(p => p.PanelId))}";
    }
}

/// <summary>드래그 중 놓을 자리를 반투명 사각형으로 보여 준다.</summary>
public partial class DockOverlay : Control
{
    /// <summary>표시할 전역 사각형(null = 숨김).</summary>
    private Rect2? _rect;

    /// <summary>강조 사각형을 설정한다. null이면 숨기고, 아니면 오버레이를 그 전역 위치·크기로 옮긴다(TopLevel이라 전역 좌표 그대로).</summary>
    public void ShowRect(Rect2? r)
    {
        _rect = r;
        Visible = r != null;
        if (r is { } rr) { GlobalPosition = rr.Position; Size = rr.Size; }
        QueueRedraw();
    }

    /// <summary>강조색 반투명 채우기 + 테두리.</summary>
    public override void _Draw()
    {
        if (_rect == null) return;
        var c = MayaTheme.Accent;
        DrawRect(new Rect2(Vector2.Zero, Size), new Color(c.R, c.G, c.B, 0.28f));
        DrawRect(new Rect2(Vector2.Zero, Size), new Color(c.R, c.G, c.B, 0.9f), false, 2 * CubeApp.Instance.UiScale);
    }
}
