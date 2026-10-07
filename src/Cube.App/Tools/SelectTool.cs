using Cube.App.Viewport;
using Cube.Core.Selection;
using Godot;

namespace Cube.App.Tools;

/// <summary>
/// Maya Select Tool. 클릭/마키 선택, Shift 토글, Ctrl 제거, Ctrl+Shift 추가, 호버 프리셀렉션,
/// RMB 클릭 시 컴포넌트 모드 팝업. 변형 툴은 이 클래스를 상속해 조작기가 맞지 않으면 선택으로 동작한다.
/// </summary>
public class SelectTool : ToolBase
{
    public override string Id => "select";
    public override string Label => "Select Tool";
    public override string HelpText => "Select Tool: click or drag to select. Shift toggles, Ctrl deselects, Ctrl+Shift adds.";

    protected Picker Picker => Ctx.Viewport.Picker;

    protected override void OnViewportChanged(Viewport.ViewportPanel panel)
    {
        // 다른 패널로 옮겨가면 진행 중 마키/호버를 정리한다
        Cancel();
        SetHover(null);
    }

    private bool _pressed;
    private bool _swallowRelease;
    private Vector2 _pressPos;
    private bool _marquee;
    private SelectModifier _modifier;

    public const float DragThresholdPx = 4f;

    protected static SelectModifier ModifierOf(InputEventWithModifiers e)
        => e.CtrlPressed && e.ShiftPressed ? SelectModifier.Add : e.CtrlPressed ? SelectModifier.Remove : e.ShiftPressed ? SelectModifier.Toggle : SelectModifier.Replace;

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
                    return true;
                }
                if (_swallowRelease) { _swallowRelease = false; _pressed = false; return true; }
                if (_pressed)
                {
                    _pressed = false;
                    if (_marquee) FinishMarquee(mb.Position);
                    else ClickSelect(mb.Position, _modifier);
                    return true;
                }
                return false;

            case InputEventMouseMotion mm:
                if (_pressed)
                {
                    if (!_marquee && (mm.Position - _pressPos).Length() >= DragThresholdPx * CubeApp.Instance.UiScale) _marquee = true;
                    if (_marquee) Ctx.Viewport.Overlay.Marquee = RectFrom(_pressPos, mm.Position);
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
    protected virtual bool OnHoverMotion(InputEventMouseMotion mm) => false;

    public override void Cancel()
    {
        _pressed = false; _marquee = false;
        Ctx.Viewport.Overlay.Marquee = null;
    }

    public override void Deactivate()
    {
        Cancel();
        SetHover(null);
    }

    private static Rect2 RectFrom(Vector2 a, Vector2 b)
    {
        var min = new Vector2(MathF.Min(a.X, b.X), MathF.Min(a.Y, b.Y));
        var max = new Vector2(MathF.Max(a.X, b.X), MathF.Max(a.Y, b.Y));
        return new Rect2(min, max - min);
    }

    /// <summary>Maya 더블클릭: 엣지 모드에서는 엣지 루프(경계면 보더 루프), 면 모드에서는 연결된 셸을 선택한다.</summary>
    private Dictionary<Core.Scene.NodeId, HashSet<int>>? _facesBeforeClick;

    /// <summary>더블클릭한 면 B와 이웃한, 직전에 선택돼 있던 면 A가 있으면 A→B 방향의 면 루프.</summary>
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

    private bool TryDoubleClickSelect(InputEventMouseButton mb)
    {
        var sel = Ctx.Sel;
        if (sel.Mode is not (SelectMode.Edge or SelectMode.Face)) return false;
        var hit = Picker.Pick(mb.Position, sel.Mode, Ctx.CameraBasedSelection);
        if (hit == null) return false;
        var mesh = Ctx.Doc.Find(hit.Value.Node)?.Mesh;
        if (mesh == null) return false;
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
        // 더블클릭의 첫 클릭이 이미 Replace로 선택했으므로, 수식어 없는 더블클릭은 루프로 교체한다
        UI.Shell.Instance.RecordSelection(s => s.Apply(items, modifier == SelectModifier.Replace ? SelectModifier.Replace : SelectModifier.Add));
        if (Hotkeys.ShellInput.Verbose) GD.Print($"[Select] double-click {sel.Mode} -> {items.Length} items");
        return true;
    }

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

    // ------------------------------------------------------------ 호버

    private (Core.Scene.NodeId, SelectMode, int)? _hover;

    protected void UpdateHover(Vector2 px)
    {
        var sel = Ctx.Sel;
        var hit = Picker.Pick(px, sel.Mode, Ctx.CameraBasedSelection);
        (Core.Scene.NodeId, SelectMode, int)? h = hit != null ? (hit.Value.Node, sel.Mode, hit.Value.Component) : null;
        SetHover(h);
    }

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
