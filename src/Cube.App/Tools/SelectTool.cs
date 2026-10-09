using Cube.App.Viewport;
using Cube.Core.Selection;
using Godot;

namespace Cube.App.Tools;

/// <summary>
/// Maya Select Tool. 클릭/마키 선택, Shift 토글, Ctrl 제거, Ctrl+Shift 추가, 호버 프리셀렉션,
/// RMB 클릭 시 컴포넌트 모드 팝업. 변형 툴은 이 클래스를 상속해 조작기가 맞지 않으면 선택으로 동작한다.
/// </summary>
/// <remarks>
/// 선택 변경은 모두 <c>Shell.RecordSelection</c>을 거쳐 SelectionCommand로 Undo 가능하게 기록된다.
/// 피킹은 활성 패널의 <see cref="Picker"/>(면 = 레이, 엣지/정점 = 화면 거리 임계)를 쓴다.
/// 상태 머신: 누름(_pressed) → 임계 거리 이상 이동하면 마키(_marquee) → 뗄 때 마키면 박스 선택, 아니면 클릭 선택.
/// </remarks>
public class SelectTool : ToolBase
{
    /// <summary>툴 ID("select", Q 키 액션).</summary>
    public override string Id => "select";
    /// <summary>표시 이름.</summary>
    public override string Label => "Select Tool";
    /// <summary>헬프 라인 안내(수식어별 동작).</summary>
    public override string HelpText => "Select Tool: click or drag to select. Shift toggles, Ctrl deselects, Ctrl+Shift adds.";

    /// <summary>활성 뷰포트 패널의 피커(패널이 바뀌면 자동으로 따라감).</summary>
    protected Picker Picker => Ctx.Viewport.Picker;

    /// <summary>활성 패널 변경: 이전 패널에 남은 마키 사각형과 호버 강조를 지운다.</summary>
    protected override void OnViewportChanged(Viewport.ViewportPanel panel)
    {
        // 다른 패널로 옮겨가면 진행 중 마키/호버를 정리한다
        Cancel();
        SetHover(null);
    }

    /// <summary>선택용 왼쪽 버튼이 눌려 있는지(조작기가 가로챈 누름은 제외).</summary>
    private bool _pressed;
    /// <summary>더블클릭 처리 후 뒤따르는 뗌 이벤트를 무시하기 위한 플래그(뗌에서 다시 클릭 선택하지 않도록).</summary>
    private bool _swallowRelease;
    /// <summary>버튼을 누른 위치(뷰포트 로컬 픽셀). 마키 사각형의 한 꼭짓점.</summary>
    private Vector2 _pressPos;
    /// <summary>드래그가 임계 거리를 넘어 마키 선택 중인지.</summary>
    private bool _marquee;
    /// <summary>누를 때의 수식어로 정한 선택 방식(Replace/Toggle/Remove/Add).</summary>
    private SelectModifier _modifier;
    /// <summary>Lasso 드래그 경로(뷰포트 로컬 픽셀). <see cref="UseLasso"/>일 때만 쓴다.</summary>
    private readonly List<Vector2> _lasso = new();

    /// <summary>true면 드래그 선택이 사각형 마키 대신 자유 곡선(Lasso)이 된다(<see cref="LassoTool"/>).</summary>
    protected virtual bool UseLasso => false;

    /// <summary>클릭과 마키를 구분하는 최소 드래그 거리(px, UI 배율을 곱해 씀).</summary>
    public const float DragThresholdPx = 4f;

    /// <summary>Maya 수식어 규칙: Ctrl+Shift = 추가, Ctrl = 제거, Shift = 토글, 없음 = 교체.</summary>
    protected static SelectModifier ModifierOf(InputEventWithModifiers e)
        => e.CtrlPressed && e.ShiftPressed ? SelectModifier.Add : e.CtrlPressed ? SelectModifier.Remove : e.ShiftPressed ? SelectModifier.Toggle : SelectModifier.Replace;

    /// <summary>
    /// 왼쪽 버튼과 마우스 이동을 처리한다.
    /// 누름: 더블클릭 루프/셸 선택 → 파생 툴 가로채기(<see cref="OnPrimaryPress"/>) → 선택 누름 상태 기록.
    /// 뗌: 더블클릭 뒤 뗌은 무시, 마키면 박스 선택 확정, 아니면 클릭 선택.
    /// 이동: 누른 채면 임계 거리 이후 마키 사각형 갱신, 아니면 파생 툴 호버(<see cref="OnHoverMotion"/>) 또는 프리셀렉션 호버.
    /// </summary>
    public override bool HandleInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseButton mb when mb.ButtonIndex == MouseButton.Left:
                if (mb.Pressed)
                {
                    if (mb.DoubleClick && TryDoubleClickSelect(mb)) { _swallowRelease = true; return true; }
                    if (OnPrimaryPress(mb)) return true;
                    _pressed = true; _marquee = false; _pressPos = mb.Position; _modifier = ModifierOf(mb);
                    _lasso.Clear(); _lasso.Add(mb.Position);
                    return true;
                }
                if (_swallowRelease) { _swallowRelease = false; _pressed = false; return true; }
                if (_pressed)
                {
                    _pressed = false;
                    if (_marquee && UseLasso) FinishLasso(mb.Position);
                    else if (_marquee) FinishMarquee(mb.Position);
                    else ClickSelect(mb.Position, _modifier);
                    return true;
                }
                return false;

            case InputEventMouseMotion mm:
                if (_pressed)
                {
                    // 누른 위치에서 임계 거리 이상 움직이면 마키로 전환하고 오버레이에 사각형을 그린다
                    if (!_marquee && (mm.Position - _pressPos).Length() >= DragThresholdPx * CubeApp.Instance.UiScale) _marquee = true;
                    if (_marquee && UseLasso)
                    {
                        // 직전 점에서 일정 거리 이상 움직였을 때만 경로에 점을 더한다(점 수 제한)
                        if ((mm.Position - _lasso[^1]).Length() >= 3f * CubeApp.Instance.UiScale) _lasso.Add(mm.Position);
                        Ctx.Viewport.Overlay.Lasso = _lasso;
                    }
                    else if (_marquee) Ctx.Viewport.Overlay.Marquee = RectFrom(_pressPos, mm.Position);
                    return true;
                }
                if (OnHoverMotion(mm)) return true;
                UpdateHover(mm.Position);
                return false;

        }
        return false;
    }

    /// <summary>파생 툴(조작기)이 프레스를 가로챌 기회. true면 선택 처리 안 함.</summary>
    protected virtual bool OnPrimaryPress(InputEventMouseButton mb) => false;
    /// <summary>파생 툴이 버튼 없는 마우스 이동(또는 드래그 중 이동)을 가로챌 기회. true면 기본 호버를 하지 않는다.</summary>
    protected virtual bool OnHoverMotion(InputEventMouseMotion mm) => false;

    /// <summary>진행 중인 선택 누름/마키를 취소하고 마키 사각형을 지운다.</summary>
    public override void Cancel()
    {
        _pressed = false; _marquee = false;
        Ctx.Viewport.Overlay.Marquee = null;
        Ctx.Viewport.Overlay.Lasso = null;
        _lasso.Clear();
    }

    /// <summary>
    /// 비활성화: 마키·호버를 정리하고 <see cref="ToolBase.Deactivate"/>로 ViewportChanged 구독을 푼다.
    /// v0.0.52 전에는 base를 부르지 않아 툴을 바꿀 때마다 구독이 쌓였다(Select/Move/Rotate/Scale 등 SelectTool 파생 전부;
    /// 4분할 뷰에서 활성 패널이 바뀌면 쌓인 수만큼 OnViewportChanged가 반복 실행됨).
    /// </summary>
    public override void Deactivate()
    {
        Cancel();
        SetHover(null);
        base.Deactivate();
    }

    /// <summary>두 점으로 정규화된(음수 크기 없는) 사각형을 만든다.</summary>
    private static Rect2 RectFrom(Vector2 a, Vector2 b)
    {
        var min = new Vector2(MathF.Min(a.X, b.X), MathF.Min(a.Y, b.Y));
        var max = new Vector2(MathF.Max(a.X, b.X), MathF.Max(a.Y, b.Y));
        return new Rect2(min, max - min);
    }

    // [참고: TryDoubleClickSelect 설명] Maya 더블클릭: 엣지 모드에서는 엣지 루프(경계면 보더 루프), 면 모드에서는 연결된 셸을 선택한다.
    /// <summary>
    /// 면 모드에서 마지막 클릭 직전의 노드별 선택 면 스냅샷. 더블클릭의 첫 클릭이 선택을 바꾸기 전 상태를 보존해
    /// "면 A를 고른 뒤 이웃 면 B를 더블클릭 = A→B 방향 면 루프"를 판정하는 데 쓴다.
    /// </summary>
    private Dictionary<Core.Scene.NodeId, HashSet<int>>? _facesBeforeClick;

    /// <summary>더블클릭한 면 B와 이웃한, 직전에 선택돼 있던 면 A가 있으면 A→B 방향의 면 루프.</summary>
    /// <remarks>
    /// 후보 A = 첫 클릭 전 선택 면 ∪ 현재 선택 면(B 제외). A마다 MeshOps.FaceLoop(A, B)를 시도해 처음 성공한 루프를 돌려준다.
    /// 이웃이 아니면 FaceLoop가 빈 목록을 돌려준다.
    /// </remarks>
    private List<int>? FaceLoopFromPrevious(Core.Mesh.PolyMesh mesh, Core.Scene.NodeId node, int faceB)
    {
        var prev = new HashSet<int>();
        if (_facesBeforeClick != null && _facesBeforeClick.TryGetValue(node, out var p)) prev.UnionWith(p);
        if (Ctx.Sel.Components.TryGetValue(node, out var cur)) prev.UnionWith(cur.Faces);
        prev.Remove(faceB);
        foreach (int a in prev)
        {
            var loop = Core.Mesh.MeshOps.FaceLoop(mesh, a, faceB);
            if (loop.Count > 0) return loop;
        }
        return null;
    }

    /// <summary>
    /// Maya 더블클릭: 엣지 모드에서는 엣지 루프(경계면 보더 루프), 면 모드에서는 면 루프(직전 선택 면이 이웃일 때) 또는 연결된 셸을 선택한다.
    /// </summary>
    /// <returns>처리했으면 true(뒤따르는 뗌 이벤트는 무시됨).</returns>
    private bool TryDoubleClickSelect(InputEventMouseButton mb)
    {
        var sel = Ctx.Sel;
        if (sel.Mode == SelectMode.Uv) return TryDoubleClickUvShell(mb);
        if (sel.Mode is not (SelectMode.Edge or SelectMode.Face)) return false;
        // 커서 아래 컴포넌트를 집는다(보이는 요소 우선 옵션 반영)
        var hit = Picker.Pick(mb.Position, sel.Mode, Ctx.CameraBasedSelection);
        if (hit == null) return false;
        var mesh = Ctx.Doc.Find(hit.Value.Node)?.Mesh;
        if (mesh == null) return false;
        // 모드별 선택할 ID 목록: 엣지 = 루프, 면 = 면 루프 또는 연결 요소(셸)
        IEnumerable<int> ids;
        if (sel.Mode == SelectMode.Edge) ids = Core.Mesh.MeshOps.EdgeLoop(mesh, hit.Value.Component);
        else if (FaceLoopFromPrevious(mesh, hit.Value.Node, hit.Value.Component) is { Count: > 0 } faceLoop)
        {
            // Maya: 면을 고른 뒤 이웃 면을 더블클릭하면 그 방향으로 면 루프(링처럼 한 바퀴)
            ids = faceLoop;
        }
        else
        {
            var comp = Core.Mesh.MeshOps.ConnectedComponents(mesh).FirstOrDefault(c => c.Contains(hit.Value.Component));
            if (comp == null) return false;
            ids = comp;
        }
        var node = hit.Value.Node;
        var items = ids.Select(i => new SelItem(node, i)).ToArray();
        var modifier = ModifierOf(mb);
        // 더블클릭의 첫 클릭이 이미 Replace로 선택했으므로, 수식어 없는 더블클릭은 루프로 교체한다.
        // Ctrl 더블클릭은 루프를 빼고(예전에는 Ctrl도 추가가 되었다), Shift/Ctrl+Shift는 더한다(Shift 토글이면 첫 클릭이 뺀 항목이 되살아남)
        var mod = modifier switch { SelectModifier.Replace => SelectModifier.Replace, SelectModifier.Remove => SelectModifier.Remove, _ => SelectModifier.Add };
        UI.Shell.Instance.RecordSelection(s => s.Apply(items, mod));
        if (Hotkeys.ShellInput.Verbose) GD.Print($"[Select] double-click {sel.Mode} -> {items.Length} items");
        return true;
    }

    /// <summary>
    /// 뷰포트 UV 모드 더블클릭(Maya): 커서 아래 정점의 UV 점들이 속한 UV 셸 전체를 선택한다. 수식어 규칙은 엣지 루프 더블클릭과 같다.
    /// </summary>
    private bool TryDoubleClickUvShell(InputEventMouseButton mb)
    {
        var hit = Picker.Pick(mb.Position, SelectMode.Uv, Ctx.CameraBasedSelection);
        if (hit == null) return false;
        var mv = Ctx.Viewport.Scene.GetMeshView(hit.Value.Node);
        if (mv == null) return false;
        var topo = mv.UvTopo;
        var shells = new HashSet<int>(Picker.ExpandUv(new[] { hit.Value.ToSelItem() }).Select(it => topo.Points[it.Component].Shell));
        if (shells.Count == 0) return false;
        var node = hit.Value.Node;
        var items = shells.SelectMany(topo.PointsInShell).Distinct().Select(p => new SelItem(node, p)).ToArray();
        var modifier = ModifierOf(mb);
        var mod = modifier switch { SelectModifier.Replace => SelectModifier.Replace, SelectModifier.Remove => SelectModifier.Remove, _ => SelectModifier.Add };
        UI.Shell.Instance.RecordSelection(s => s.Apply(items, mod));
        if (Hotkeys.ShellInput.Verbose) GD.Print($"[Select] double-click Uv -> shells {string.Join(",", shells)}: {items.Length} points");
        return true;
    }

    /// <summary>
    /// 클릭 선택: 커서 아래 컴포넌트 하나를 수식어 규칙으로 적용한다. UV 모드는 정점을 집은 뒤 그 정점의 모든 UV 점으로 확장.
    /// 빈 곳 클릭은 Replace일 때만 선택 해제(수식어가 있으면 아무것도 안 함).
    /// </summary>
    private void ClickSelect(Vector2 px, SelectModifier modifier)
    {
        var sel = Ctx.Sel;
        var hit = Picker.Pick(px, sel.Mode, Ctx.CameraBasedSelection);
        var items = hit != null ? new[] { hit.Value.ToSelItem() } : Array.Empty<SelItem>();
        if (sel.Mode == SelectMode.Uv && items.Length > 0) items = Picker.ExpandUv(items).ToArray();
        if (items.Length == 0 && modifier != SelectModifier.Replace) return;
        // 면 루프 더블클릭용: 이 클릭 직전에 선택돼 있던 면(더블클릭의 첫 클릭이 선택을 바꾸기 전 상태)
        _facesBeforeClick = sel.Mode == SelectMode.Face ? sel.Components.ToDictionary(kv => kv.Key, kv => new HashSet<int>(kv.Value.Faces)) : null;
        UI.Shell.Instance.RecordSelection(s => s.Apply(items, modifier));
    }

    /// <summary>마키 선택 확정: 사각형 안의 요소를 집어 누를 때의 수식어로 적용한다.</summary>
    private void FinishMarquee(Vector2 px)
    {
        var rect = RectFrom(_pressPos, px);
        Ctx.Viewport.Overlay.Marquee = null;
        // 박스 선택은 옵션(기본 on)에 따라 가려진 요소도 포함한다; 클릭 선택은 항상 보이는 것 우선
        var items = Picker.Marquee(rect, Ctx.Sel.Mode, cameraBased: !Ctx.Settings.MarqueeSelectThrough);
        if (Hotkeys.ShellInput.Verbose) GD.Print($"[Select] marquee {rect} mode={Ctx.Sel.Mode} through={Ctx.Settings.MarqueeSelectThrough} -> {items.Count} items: {string.Join(",", items.Select(i => i.Component))}");
        if (items.Count == 0 && _modifier != SelectModifier.Replace) return;
        var mod = _modifier;
        UI.Shell.Instance.RecordSelection(s => s.Apply(items, mod));
    }

    /// <summary>Lasso 선택 확정: 경로(마지막 → 처음으로 닫음) 안의 요소를 집어 누를 때의 수식어로 적용한다. 규칙은 마키와 같다.</summary>
    private void FinishLasso(Vector2 px)
    {
        if ((px - _lasso[^1]).Length() > 0.5f) _lasso.Add(px);
        Ctx.Viewport.Overlay.Lasso = null;
        var items = Picker.Lasso(_lasso, Ctx.Sel.Mode, cameraBased: !Ctx.Settings.MarqueeSelectThrough);
        if (Hotkeys.ShellInput.Verbose) GD.Print($"[Select] lasso {_lasso.Count} pts mode={Ctx.Sel.Mode} through={Ctx.Settings.MarqueeSelectThrough} -> {items.Count} items: {string.Join(",", items.Select(i => i.Component))}");
        _lasso.Clear();
        if (items.Count == 0 && _modifier != SelectModifier.Replace) return;
        var mod = _modifier;
        UI.Shell.Instance.RecordSelection(s => s.Apply(items, mod));
    }

    // ------------------------------------------------------------ 호버

    /// <summary>현재 호버(프리셀렉션) 대상: (노드, 모드, 컴포넌트 ID). 없으면 null.</summary>
    private (Core.Scene.NodeId, SelectMode, int)? _hover;

    /// <summary>커서 위치에서 피킹해 호버 대상을 갱신한다.</summary>
    protected void UpdateHover(Vector2 px)
    {
        var sel = Ctx.Sel;
        var hit = Picker.Pick(px, sel.Mode, Ctx.CameraBasedSelection);
        (Core.Scene.NodeId, SelectMode, int)? h = hit != null ? (hit.Value.Node, sel.Mode, hit.Value.Component) : null;
        SetHover(h);
    }

    /// <summary>
    /// 호버 대상을 설정하고 표시(ViewportDisplay.Hover)에 반영한다. 같은 값이면 무시하며,
    /// 스타일 재적용은 이전 호버 노드와(다르면) 새 노드에만 한다(전체 갱신 비용 회피).
    /// </summary>
    protected void SetHover((Core.Scene.NodeId, SelectMode, int)? h)
    {
        if (Nullable.Equals(h, _hover)) return;
        var display = Ctx.Viewport.Display;
        var prev = _hover;
        _hover = h;
        display.Hover = h;
        // 바뀐 노드만 갱신
        if (prev is { } p && Ctx.Viewport.Scene.GetMeshView(p.Item1) is { } pv) display.ApplyStyle(pv);
        if (h is { } n && (prev == null || prev.Value.Item1 != n.Item1) && Ctx.Viewport.Scene.GetMeshView(n.Item1) is { } nv) display.ApplyStyle(nv);
    }
}
