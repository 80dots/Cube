using Godot;

namespace Cube.App.UI.Docking;

public enum DockSideKind { Left, Right }

/// <summary>도크 자리: 좌/우 도크와 그 안의 그룹 번호(위에서부터).</summary>
public sealed record DockSlot(DockSideKind Side, int Group);

/// <summary>좌/우 도크 영역: 위아래로 쌓인 탭 그룹(DockGroup)들. 그룹 사이 경계를 끌어 높이를, 도크와 뷰포트 사이 경계를 끌어 폭을 조절한다.</summary>
public partial class DockSide : VSplitContainer
{
    public DockSideKind Kind;
    public List<DockGroup> Groups => GetChildren().OfType<DockGroup>().ToList();
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
/// 그룹 위/아래 가장자리에 놓으면 그 위/아래에 새 그룹을 만들고, 뷰포트 왼쪽/오른쪽 가장자리에 놓으면 그 쪽 도크 맨 아래에 새 그룹을 만든다.
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

    public enum DropMode { Tab, Before, After, NewGroup }
    public sealed record DropTarget(DockSideKind Side, int Group, DropMode Mode, Rect2 Highlight);

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
        var groups = side.Groups;
        DockGroup g;
        if (t.Mode == DropMode.Tab && t.Group >= 0 && t.Group < groups.Count) g = groups[t.Group];
        else
        {
            g = NewGroup();
            int idx = t.Mode switch { DropMode.Before => t.Group, DropMode.After => t.Group + 1, _ => groups.Count };
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
        var side = (DockSide)g.GetParent();
        p.LastDock = new DockSlot(side.Kind, side.Groups.IndexOf(g));
        UpdateSides();
    }

    /// <summary>도크에서 떼어 셸 위의 떠 있는 패널로 되돌린다. show=false면 숨긴다(닫기).</summary>
    public void Undock(FloatingPanel p, bool show)
    {
        if (!p.Docked) return;
        var g = p.GetParent() as DockGroup;
        var side = g?.GetParent() as DockSide;
        if (g != null && side != null) p.LastDock = new DockSlot(side.Kind, side.Groups.IndexOf(g));
        g?.RemoveChild(p);
        if (g != null && side != null && !g.Panels.Any())
        {
            side.RemoveChild(g);
            g.QueueFree();
            ResetGroupHeights(side);
        }
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
        int n = SideOf(slot.Side).Groups.Count;
        Dock(p, new DropTarget(slot.Side, slot.Group, slot.Group < n ? DropMode.Tab : DropMode.NewGroup, default), widen: false); // 다시 열 때는 사용자가 정한 폭 유지
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

    private static void ResetGroupHeights(DockSide side) => side.SplitOffsets = new int[Math.Max(0, side.Groups.Count - 1)];

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

    /// <summary>커서 아래의 놓을 자리: 그룹 가운데 = 탭, 위/아래 띠 = 그 위/아래 새 그룹, 뷰포트 좌/우 가장자리 = 그 쪽 도크 새 그룹.</summary>
    public DropTarget? FindTarget(Vector2 m)
    {
        foreach (var side in new[] { Left, Right })
        {
            if (!side.IsVisibleInTree()) continue;
            var groups = side.Groups;
            for (int i = 0; i < groups.Count; i++)
            {
                var r = groups[i].GetGlobalRect();
                if (!r.HasPoint(m)) continue;
                float band = Math.Min(r.Size.Y * 0.25f, 60 * S);
                if (m.Y < r.Position.Y + band) return new DropTarget(side.Kind, i, DropMode.Before, new Rect2(r.Position, new Vector2(r.Size.X, r.Size.Y * 0.5f)));
                if (m.Y > r.End.Y - band) return new DropTarget(side.Kind, i, DropMode.After, new Rect2(r.Position + new Vector2(0, r.Size.Y * 0.5f), new Vector2(r.Size.X, r.Size.Y * 0.5f)));
                return new DropTarget(side.Kind, i, DropMode.Tab, r);
            }
        }
        var lr = _shell.Layout.GetGlobalRect();
        float edge = 48 * S;
        if (lr.HasPoint(m))
        {
            float w = Math.Min(260 * S, lr.Size.X / 3);
            if (m.X < lr.Position.X + edge) return new DropTarget(DockSideKind.Left, Left.Groups.Count, DropMode.NewGroup, new Rect2(lr.Position, new Vector2(w, lr.Size.Y)));
            if (m.X > lr.End.X - edge) return new DropTarget(DockSideKind.Right, Right.Groups.Count, DropMode.NewGroup, new Rect2(new Vector2(lr.End.X - w, lr.Position.Y), new Vector2(w, lr.Size.Y)));
        }
        return null;
    }

    // ---------------------------------------------------------------- 저장 / 복원

    public void SaveLayout()
    {
        if (!IsInstanceValid(Left)) return;
        var d = _shell.Settings.Dock;
        static List<List<string>> Ids(DockSide side) => side.Groups.Select(g => g.Panels.Where(p => p.PanelId.Length > 0).Select(p => p.PanelId).ToList()).Where(l => l.Count > 0).ToList();
        d.Left = Ids(Left);
        d.Right = Ids(Right);
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
    public void RestoreLayout(Func<string, FloatingPanel?> ensure)
    {
        var d = _shell.Settings.Dock;
        _leftWidth = (d.LeftWidth > 0 ? d.LeftWidth : 220) * S;
        _rightWidth = (d.RightWidth > 0 ? d.RightWidth : 260) * S;
        foreach (var (kind, groups) in new[] { (DockSideKind.Left, d.Left), (DockSideKind.Right, d.Right) })
        {
            var side = SideOf(kind);
            foreach (var ids in groups)
            {
                DockGroup? g = null;
                foreach (var id in ids)
                {
                    var p = ensure(id);
                    if (p == null || p.Docked) continue;
                    if (g == null) { g = NewGroup(); side.AddChild(g); }
                    Attach(p, g);
                }
                if (g != null && g.GetTabCount() > 0)
                {
                    var active = g.Panels.FirstOrDefault(pp => d.Active.Contains(pp.PanelId));
                    g.CurrentTab = active != null ? g.GetTabIdxFromControl(active) : 0;
                }
            }
            ResetGroupHeights(side);
            var splits = kind == DockSideKind.Left ? d.LeftSplits : d.RightSplits;
            if (splits.Count == side.Groups.Count - 1 && splits.Count > 0) side.SplitOffsets = splits.Select(v => (int)(v * S)).ToArray();
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
        static string Side(DockSide s) => string.Join(" / ", s.Groups.Select(g => "[" + string.Join("|", g.Panels.Select(p => p.PanelId + (g.GetCurrentTabControl() == p ? "*" : ""))) + "]"));
        string groupsY = string.Join(",", Right.Groups.Select(g => $"{g.GlobalPosition.Y:0}-{g.GlobalPosition.Y + g.Size.Y:0}"));
        return $"rightX={Right.GlobalPosition.X:0} rightGroupsY={groupsY} offsets={_mainSplit.SplitOffsets[0]},{_rightSplit.SplitOffsets[0]} want={_leftWidth:0},{_rightWidth:0} L{(Left.Visible ? Left.Size.X.ToString("0") : "-")} {Side(Left)}  R{(Right.Visible ? Right.Size.X.ToString("0") : "-")} {Side(Right)}  floating={string.Join(",", _panels.Values.Where(p => !p.Docked && p.Visible).Select(p => p.PanelId))}";
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
