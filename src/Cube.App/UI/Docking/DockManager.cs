using Godot;

namespace Cube.App.UI.Docking;

public enum DockSideKind { Left, Right }

/// <summary>도크 자리: 좌/우 도크와 그 안의 그룹 번호(위에서부터).</summary>
public sealed record DockSlot(DockSideKind Side, int Group);

/// <summary>
/// 좌/우 도크 영역: 위아래로 쌓인 항목들. 항목은 탭 그룹(DockGroup) 하나이거나, 그룹 여러 개를 나란히 둔 행(DockRow)이다.
/// 항목 사이 경계를 끌어 높이를, 행 안 경계를 끌어 나란한 그룹의 폭을, 도크와 뷰포트 사이 경계를 끌어 도크 폭을 조절한다.
/// </summary>
public partial class DockSide : VSplitContainer
{
    public DockSideKind Kind;
    /// <summary>위에서 아래 순서의 항목(DockGroup 또는 DockRow).</summary>
    public List<Control> Items => GetChildren().OfType<Control>().Where(c => c is DockGroup or DockRow).ToList();
    /// <summary>모든 그룹(행 안의 그룹 포함, 위→아래, 왼→오).</summary>
    public List<DockGroup> Groups => Items.SelectMany(i => i is DockRow r ? r.Groups : new List<DockGroup> { (DockGroup)i }).ToList();
}

/// <summary>
/// 도크 안에서 탭 그룹 여러 개를 나란히(가로로) 놓는 행.
/// 도크 폭이 바뀌면(도크와 뷰포트 사이 경계를 끌거나 창 크기 변경) 뷰포트에 붙은 그룹만 폭이 바뀌고 나머지 그룹은 폭을 유지한다
/// (오른쪽 도크 = 맨 왼쪽 그룹, 왼쪽 도크 = 맨 오른쪽 그룹). SplitContainer는 모든 자식이 늘어나면 바뀐 폭을 나눠 주므로,
/// 정렬이 끝날 때마다(SortChildren) 행 폭이 그대로면 나머지 그룹 폭을 기억하고, 행 폭이 바뀌었으면 기억한 폭이 되도록 경계 오프셋을 실측 보정한다.
/// 행 안 경계를 끌면 행 폭은 그대로라 새 폭이 기억된다.
/// </summary>
public partial class DockRow : HSplitContainer
{
    public List<DockGroup> Groups => GetChildren().OfType<DockGroup>().ToList();

    private float _lastTotal = -1;
    private readonly Dictionary<DockGroup, float> _keep = new();
    private int _fixes;

    /// <summary>뷰포트에 붙은(폭이 바뀌는) 그룹 번호.</summary>
    private int AdjacentIndex(int count) => GetParent() is DockSide { Kind: DockSideKind.Left } ? count - 1 : 0;

    public override void _Ready() => SortChildren += OnSorted;

    /// <summary>현재 폭을 기억한다(그룹 추가·제거·레이아웃 복원 직후 등).</summary>
    public void RememberWidths() { _lastTotal = -1; }

    private void OnSorted()
    {
        var gs = Groups;
        if (gs.Count < 2) { _lastTotal = Size.X; return; }
        float total = Size.X;
        int adj = AdjacentIndex(gs.Count);
        bool known = gs.Where((g, i) => i != adj).All(g => _keep.ContainsKey(g));
        // 레이아웃 복원 직후(폭이 아직 적용되는 중)에는 보정하지 않고 기억만 한다
        if (!DockManager.Settled || _lastTotal < 0 || !known || Math.Abs(total - _lastTotal) < 0.5f || _dragging)
        {
            // 행 폭이 그대로(행 안 경계 드래그·그룹 변경): 지금 폭을 기억
            _keep.Clear();
            for (int i = 0; i < gs.Count; i++) if (i != adj) _keep[gs[i]] = gs[i].Size.X;
            _lastTotal = total; _fixes = 0;
            return;
        }
        // 행 폭이 바뀜: 뷰포트 쪽이 아닌 그룹은 기억한 폭으로 되돌린다(오프셋에 대해 위치가 선형이라 한 번에 맞음)
        float sep = gs.Count > 1 ? gs[1].Position.X - (gs[0].Position.X + gs[0].Size.X) : 0;
        float avail = total - sep * (gs.Count - 1);
        var want = new float[gs.Count];
        float fixedSum = 0;
        for (int i = 0; i < gs.Count; i++) if (i != adj) { want[i] = _keep[gs[i]]; fixedSum += want[i]; }
        float adjMin = gs[adj].GetCombinedMinimumSize().X;
        if (avail - fixedSum < adjMin)
        {
            // 공간이 모자라면 나머지 그룹을 비율대로 줄인다
            float scale = Math.Max(0, avail - adjMin) / Math.Max(1, fixedSum);
            fixedSum = 0;
            for (int i = 0; i < gs.Count; i++) if (i != adj) { want[i] = Math.Max(gs[i].GetCombinedMinimumSize().X, want[i] * scale); fixedSum += want[i]; }
        }
        want[adj] = Math.Max(adjMin, avail - fixedSum);
        var offs = SplitOffsets.Length == gs.Count - 1 ? (int[])SplitOffsets.Clone() : new int[gs.Count - 1];
        bool changed = false;
        float end = 0;
        for (int i = 0; i < gs.Count - 1; i++)
        {
            end += want[i];
            float actualEnd = gs[i].Position.X + gs[i].Size.X;
            int d = (int)MathF.Round(end - actualEnd);
            if (d != 0) { offs[i] += d; changed = true; }
            end += sep;
        }
        // 정렬 시그널 안에서 오프셋을 바꾸면 Godot이 재정렬 요청을 버리므로(정렬이 끝날 때 대기 플래그를 지움) 지연 적용한다.
        // 적용 뒤 정렬에서 다시 확인한다(최대 몇 번)
        if (changed && _fixes++ < 4) Callable.From(() => { if (IsInstanceValid(this)) SplitOffsets = offs; }).CallDeferred();
        else { _lastTotal = total; _fixes = 0; }
    }

    private bool _dragging;
    public override void _GuiInput(InputEvent e)
    {
        // 행 안 경계를 끄는 동안은 폭 보정을 하지 않는다(드래그가 곧 새 폭)
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left } mb) _dragging = mb.Pressed;
    }
}

/// <summary>
/// 도크 안의 탭 묶음. 탭을 도크 밖으로 끌면 떠 있는 패널로 떨어지고(곧바로 다른 자리로 끌어 붙일 수 있음),
/// 탭 줄 오른쪽 메뉴(▼)에서 Float / Close를 고를 수 있다.
/// </summary>
public partial class DockGroup : TabContainer
{
    private DockManager _dm = null!;
    private int _pressTab = -1;
    private Vector2 _pressPos;

    public void Setup(DockManager dm)
    {
        _dm = dm;
        SizeFlagsVertical = SizeFlags.ExpandFill;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        ClipTabs = true;
        var menu = new PopupMenu { Name = "DockMenu" };
        menu.AddItem("Float Panel", 0);
        menu.AddItem("Close Panel", 1);
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

    public IEnumerable<FloatingPanel> Panels => GetChildren().OfType<FloatingPanel>();

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
public partial class DockManager : Node
{
    public static DockManager? Instance { get; private set; }

    private Shell _shell = null!;
    public DockSide Left { get; private set; } = null!;
    public DockSide Right { get; private set; } = null!;
    private HSplitContainer _mainSplit = null!, _rightSplit = null!;
    private DockOverlay _overlay = null!;
    private readonly Dictionary<string, FloatingPanel> _panels = new();
    private bool _maximized;
    /// <summary>패널을 붙이며 도크를 넓힌 기록: 그 패널을 뗄 때 폭이 그대로면 원래 폭으로 되돌린다.</summary>
    private readonly Dictionary<DockSideKind, (FloatingPanel panel, float before, float after)> _widened = new();
    /// <summary>경계(폭/그룹 높이)를 끌었음 → 마우스를 놓을 때 레이아웃 저장.</summary>
    private bool _splitDirty;
    private float _leftWidth, _rightWidth;

    private static float S => CubeApp.Instance.UiScale;
    private float SideMin => 120 * S;

    public enum DropMode { Tab, Before, After, LeftOf, RightOf, NewGroup }
    public sealed record DropTarget(DockSideKind Side, DockGroup? Group, DropMode Mode, Rect2 Highlight);

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

    public override void _ExitTree() { if (Instance == this) Instance = null; }

    public void Register(FloatingPanel p) { if (p.PanelId.Length > 0) _panels[p.PanelId] = p; }

    public DockSide SideOf(DockSideKind k) => k == DockSideKind.Left ? Left : Right;

    // ---------------------------------------------------------------- 붙이기 / 떼기

    /// <summary>패널을 대상 자리에 붙인다(이미 붙어 있으면 먼저 뗀다).</summary>
    public void Dock(FloatingPanel p, DropTarget t, bool widen = true)
    {
        if (p.Docked) Undock(p, show: false);
        var side = SideOf(t.Side);
        var target = t.Group != null && IsInstanceValid(t.Group) && t.Group.IsInsideTree() ? t.Group : null;
        DockGroup g;
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
            if (g.GetParent() is DockRow r3 && r3.Groups.Count == 2) KeepWidthDeferred(r3, target, keepW, 3);
            return;
        }
        else
        {
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

    private DockGroup NewGroup()
    {
        var g = new DockGroup { Name = "DockGroup" };
        g.Setup(this);
        return g;
    }

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
        var g = p.GetParent() as DockGroup;
        var side = g == null ? null : SideOfGroup(g);
        if (g != null && side != null) p.LastDock = new DockSlot(side.Kind, side.Groups.IndexOf(g));
        g?.RemoveChild(p);
        if (g != null && side != null && !g.Panels.Any()) RemoveGroup(side, g);
        if (side != null && _widened.TryGetValue(side.Kind, out var w) && w.panel == p)
        {
            _widened.Remove(side.Kind);
            float cur = side.Kind == DockSideKind.Left ? _leftWidth : _rightWidth;
            if (Math.Abs(cur - w.after) < 1f) SetSideWidth(side.Kind, w.before);
        }
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

    private void UpdateSides()
    {
        Left.Visible = !_maximized && Left.Groups.Count > 0;
        Right.Visible = !_maximized && Right.Groups.Count > 0;
        Callable.From(ApplyWidths).CallDeferred();
    }

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

    private static void ResetGroupHeights(DockSide side) => side.SplitOffsets = new int[Math.Max(0, side.Items.Count - 1)];

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
            else row.SplitOffsets = new int[rest.Count - 1];
        }
        ResetGroupHeights(side);
    }

    /// <summary>두 그룹짜리 행에서 keep 그룹의 폭이 w가 되도록 경계 오프셋을 맞춘다(폭 적용이 끝난 뒤 몇 프레임에 걸쳐).</summary>
    private void KeepWidthDeferred(DockRow row, DockGroup keep, float w, int frames)
    {
        GetTree().CreateTimer(0.05).Timeout += () =>
        {
            if (!IsInstanceValid(row) || !IsInstanceValid(keep) || keep.GetParent() != row) return;
            float total = row.Size.X; if (total < 10) return;
            float sep = row.GetThemeConstant("separation");
            float half = (total - sep) / 2f;
            bool keepFirst = row.Groups.IndexOf(keep) == 0;
            float target = Math.Clamp(w, SideMin, total - SideMin);
            row.SplitOffsets = new[] { (int)(keepFirst ? target - half : half - target) };
            if (frames > 1) KeepWidthDeferred(row, keep, w, frames - 1);
        };
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

    private FloatingPanel? _drag;
    private Vector2 _dragOffset;
    private DropTarget? _target;

    public bool IsDragging => _drag != null;

    /// <summary>떠 있는 패널 제목 바를 누르면 시작. 이후 이동/놓기는 _Input에서 전역으로 처리한다(누른 컨트롤이 사라져도 계속).</summary>
    public void BeginDrag(FloatingPanel p, Vector2 mouse)
    {
        _drag = p;
        _dragOffset = mouse - p.GlobalPosition;
        p.MoveToFront();
        _overlay.MoveToFront();
    }

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
            _drag.MoveTo(mm.GlobalPosition - _dragOffset);
            _target = FindTarget(mm.GlobalPosition);
            _overlay.ShowRect(_target?.Highlight);
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
        var lr = _shell.Layout.GetGlobalRect();
        float edge = 48 * S;
        if (lr.HasPoint(m))
        {
            float w = Math.Min(260 * S, lr.Size.X / 3);
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

    public void SaveLayout()
    {
        if (!IsInstanceValid(Left)) return;
        var d = _shell.Settings.Dock;
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

    public void RestoreLayout(Func<string, FloatingPanel?> ensure)
    {
        Settled = false;
        GetTree().CreateTimer(0.5).Timeout += () => Settled = true;
        var d = _shell.Settings.Dock;
        _leftWidth = (d.LeftWidth > 0 ? d.LeftWidth : 220) * S;
        _rightWidth = (d.RightWidth > 0 ? d.RightWidth : 260) * S;
        foreach (var (kind, rows, rowSplits) in new[] { (DockSideKind.Left, d.LeftRows ?? d.Left.Select(g => new List<List<string>> { g }).ToList(), d.LeftRowSplits), (DockSideKind.Right, d.RightRows ?? d.Right.Select(g => new List<List<string>> { g }).ToList(), d.RightRowSplits) })
        {
            var side = SideOf(kind);
            for (int ri = 0; ri < rows.Count; ri++)
            {
                var made = new List<DockGroup>();
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
            ResetGroupHeights(side);
            var splits = kind == DockSideKind.Left ? d.LeftSplits : d.RightSplits;
            if (splits.Count == side.Items.Count - 1 && splits.Count > 0) side.SplitOffsets = splits.Select(v => (int)(v * S)).ToArray();
        }
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
    private Rect2? _rect;

    public void ShowRect(Rect2? r)
    {
        _rect = r;
        Visible = r != null;
        if (r is { } rr) { GlobalPosition = rr.Position; Size = rr.Size; }
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (_rect == null) return;
        var c = MayaTheme.Accent;
        DrawRect(new Rect2(Vector2.Zero, Size), new Color(c.R, c.G, c.B, 0.28f));
        DrawRect(new Rect2(Vector2.Zero, Size), new Color(c.R, c.G, c.B, 0.9f), false, 2 * CubeApp.Instance.UiScale);
    }
}
