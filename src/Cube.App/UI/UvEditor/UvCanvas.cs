using System.Numerics;
using Cube.App.Bridge;
using Cube.App.Viewport;
using Cube.Core.Commands;
using Cube.Core.Mesh;
using Cube.Core.Scene;
using Cube.Core.Selection;
using Cube.Core.Uv;
using Godot;
using GVec2 = Godot.Vector2;
using NVec2 = System.Numerics.Vector2;

namespace Cube.App.UI.UvEditor;

/// <summary>
/// UV 편집 캔버스. 선택된 오브젝트들의 UV를 0..1 그리드 위에 그리고, 선택(UV점/엣지/면)과 W/E/R 드래그 변형을 처리한다.
/// 내비게이션: Alt+MMB 팬, Alt+RMB·휠 줌, F 프레임.
/// </summary>
public partial class UvCanvas : Control
{
    private Shell _shell = null!;
    private float _zoom = 400f;          // UV 1단위 = 픽셀
    private GVec2 _origin;               // uv (0,0)의 캔버스 픽셀
    private Texture2D? _gridTex;

    private readonly Dictionary<NodeId, UvTopology> _topos = new();
    private bool _dragging;              // 변형 드래그 중 위상 재빌드 금지

    // 입력 상태
    private MouseButton _navButton = MouseButton.None;
    private GVec2 _last;
    private bool _pressed, _marquee;
    private GVec2 _pressPos;
    private SelectModifier _modifier;
    private GVec2? _marqueeEnd;

    // 변형 드래그
    private readonly List<(NodeId node, UvTopology topo, int[] points, NVec2[] initial, UvEditCommand cmd)> _xform = new();
    private NVec2 _xformCenter;
    private string _xformTool = "";

    public void Setup(Shell shell)
    {
        _shell = shell;
        MouseFilter = MouseFilterEnum.Stop;
        FocusMode = FocusModeEnum.All;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;
        ClipContents = true;
        _gridTex = Icons.LoadPng("res://assets/textures/uv_grid.bin");
        shell.Document.Changed += OnDocChanged;
        shell.Document.Selection.Changed += QueueRedraw;
        shell.Document.Selection.ModeChanged += QueueRedraw;
        Resized += () => { if (_origin == GVec2.Zero) FrameAll(); };
        CallDeferred(nameof(FrameAll));
    }

    public override void _ExitTree()
    {
        if (_shell != null) { _shell.Document.Changed -= OnDocChanged; _shell.Document.Selection.Changed -= QueueRedraw; _shell.Document.Selection.ModeChanged -= QueueRedraw; }
    }

    private void OnDocChanged(DocChange c)
    {
        if (_dragging) return;
        if (c.Kind is ChangeKind.MeshTopology or ChangeKind.MeshAttributes or ChangeKind.NodeRemoved or ChangeKind.Reset) _topos.Remove(c.Node);
        if (c.Kind == ChangeKind.Reset) _topos.Clear();
        QueueRedraw();
    }

    public void Invalidate() { _topos.Clear(); QueueRedraw(); }

    // ---------------------------------------------------------------- 데이터

    /// <summary>UV 편집 대상 노드: 선택된 오브젝트 + 컴포넌트가 선택된 노드.</summary>
    public IEnumerable<SceneNode> TargetNodes()
    {
        var sel = _shell.Document.Selection;
        var ids = new HashSet<NodeId>(sel.Objects);
        foreach (var (id, c) in sel.Components) if (!c.IsEmpty) ids.Add(id);
        foreach (var id in ids) { var n = _shell.Document.Find(id); if (n?.Mesh != null) yield return n; }
    }

    public UvTopology Topo(SceneNode n)
    {
        if (!_topos.TryGetValue(n.Id, out var t)) { t = UvTopology.Build(n.Mesh!); _topos[n.Id] = t; }
        return t;
    }

    // ---------------------------------------------------------------- 좌표

    public GVec2 UvToPx(NVec2 uv) => _origin + new GVec2(uv.X, -uv.Y) * _zoom;
    public NVec2 PxToUv(GVec2 px) => new((px.X - _origin.X) / _zoom, -(px.Y - _origin.Y) / _zoom);

    public void FrameAll()
    {
        if (Size.X < 10 || Size.Y < 10) return;
        var targets = TargetNodes().ToList();
        var min = new NVec2(0, 0); var max = new NVec2(1, 1);
        foreach (var n in targets)
            foreach (var p in Topo(n).Points) { min = NVec2.Min(min, p.Uv); max = NVec2.Max(max, p.Uv); }
        FrameRect(min, max);
    }

    public void FrameSelected()
    {
        var sel = _shell.Document.Selection;
        var min = new NVec2(float.MaxValue); var max = new NVec2(float.MinValue); int n = 0;
        foreach (var node in TargetNodes())
            foreach (int p in SelectedPoints(node)) { var uv = Topo(node).Points[p].Uv; min = NVec2.Min(min, uv); max = NVec2.Max(max, uv); n++; }
        if (n == 0) { FrameAll(); return; }
        if (max.X - min.X < 0.01f) { min.X -= 0.05f; max.X += 0.05f; }
        if (max.Y - min.Y < 0.01f) { min.Y -= 0.05f; max.Y += 0.05f; }
        FrameRect(min, max);
    }

    private void FrameRect(NVec2 min, NVec2 max)
    {
        float margin = 40 * CubeApp.Instance.UiScale;
        float w = MathF.Max(max.X - min.X, 1e-3f), h = MathF.Max(max.Y - min.Y, 1e-3f);
        _zoom = MathF.Min((Size.X - 2 * margin) / w, (Size.Y - 2 * margin) / h);
        var center = (min + max) * 0.5f;
        _origin = Size / 2 - new GVec2(center.X, -center.Y) * _zoom;
        QueueRedraw();
    }

    // ---------------------------------------------------------------- 선택 도우미

    /// <summary>현재 모드의 선택을 UV 점 집합으로 변환(UV 모드: 그대로, 엣지/면: 코너의 UV 점).</summary>
    public HashSet<int> SelectedPoints(SceneNode node)
    {
        var sel = _shell.Document.Selection;
        var set = new HashSet<int>();
        if (!sel.Components.TryGetValue(node.Id, out var comps))
        {
            // 오브젝트 모드에서 오브젝트가 선택되면 전체
            if (sel.Mode == SelectMode.Object && sel.IsObjectSelected(node.Id)) for (int i = 0; i < Topo(node).Points.Count; i++) set.Add(i);
            return set;
        }
        var topo = Topo(node); var m = node.Mesh!;
        switch (sel.Mode)
        {
            case SelectMode.Uv: set.UnionWith(comps.Uvs.Where(i => i < topo.Points.Count)); break;
            case SelectMode.Edge:
                foreach (int e in comps.Edges)
                {
                    if (e >= m.EdgeCount || !m.Edges[e].Alive) continue;
                    var ed = m.Edges[e];
                    foreach (int he in new[] { ed.He0, ed.He1 }) { if (he < 0) continue; set.Add(topo.HeToPoint[he]); set.Add(topo.HeToPoint[m.Hes[he].Next]); }
                }
                break;
            case SelectMode.Face:
                foreach (int f in comps.Faces)
                {
                    if (f >= m.FaceCount || !m.Faces[f].Alive) continue;
                    int start = m.Faces[f].HalfEdge, he = start;
                    do { set.Add(topo.HeToPoint[he]); he = m.Hes[he].Next; } while (he != start);
                }
                break;
            case SelectMode.Object:
                if (sel.IsObjectSelected(node.Id)) for (int i = 0; i < topo.Points.Count; i++) set.Add(i);
                break;
        }
        return set;
    }

    private bool IsEdgeSelected(SceneNode node, int e) => _shell.Document.Selection.IsComponentSelected(node.Id, SelectMode.Edge, e);
    private bool IsFaceSelected(SceneNode node, int f) => _shell.Document.Selection.IsComponentSelected(node.Id, SelectMode.Face, f);

    // ---------------------------------------------------------------- 그리기

    public override void _Draw()
    {
        float s = CubeApp.Instance.UiScale;
        DrawRect(new Rect2(GVec2.Zero, Size), MathConvert.Rgb(0x2b2b2b));
        var p0 = UvToPx(new NVec2(0, 1)); var p1 = UvToPx(new NVec2(1, 0));
        var unit = new Rect2(p0, p1 - p0);
        if (_gridTex != null) DrawTextureRect(_gridTex, unit, false, new Color(1, 1, 1, 0.28f));
        else DrawRect(unit, MathConvert.Rgb(0x3a3a3a));
        for (int i = 0; i <= 10; i++)
        {
            float t = i / 10f;
            var col = i % 5 == 0 ? MathConvert.Rgb(0x6a6a6a) : MathConvert.Rgb(0x4a4a4a);
            DrawLine(UvToPx(new NVec2(t, 0)), UvToPx(new NVec2(t, 1)), col, 1 * s);
            DrawLine(UvToPx(new NVec2(0, t)), UvToPx(new NVec2(1, t)), col, 1 * s);
        }
        DrawRect(unit, MathConvert.Rgb(0x9a9a9a), false, 1 * s);

        var sel = _shell.Document.Selection;
        var font = GetThemeDefaultFont(); int fs = (int)(11 * s);
        var targets = TargetNodes().ToList();
        if (targets.Count == 0)
        {
            DrawString(font, new GVec2(12 * s, 20 * s), "Select an object or components to edit its UVs.", HorizontalAlignment.Left, -1, fs, MayaTheme.TextDim);
            return;
        }
        foreach (var node in targets)
        {
            var m = node.Mesh!; var topo = Topo(node);
            var selPts = SelectedPoints(node);
            // 면 틴트
            for (int f = 0; f < m.FaceCount; f++)
            {
                if (!m.Faces[f].Alive) continue;
                bool fsel = sel.Mode == SelectMode.Face && IsFaceSelected(node, f);
                var poly = new List<GVec2>();
                int start = m.Faces[f].HalfEdge, he = start;
                do { poly.Add(UvToPx(m.Hes[he].Uv0)); he = m.Hes[he].Next; } while (he != start);
                if (poly.Count < 3) continue;
                var arr = poly.ToArray();
                if (MathF.Abs(PolygonArea(arr)) < 0.5f) continue; // 퇴화 면(원통 캡 등)은 채우지 않는다
                var tris = Geometry2D.TriangulatePolygon(arr);
                if (tris.Length == 0) continue;
                var col = fsel ? new Color(1f, 0.55f, 0f, 0.35f) : new Color(0.6f, 0.75f, 1f, 0.08f);
                for (int t = 0; t + 2 < tris.Length; t += 3)
                    DrawColoredPolygon(new[] { arr[tris[t]], arr[tris[t + 1]], arr[tris[t + 2]] }, col);
            }
            // 엣지
            for (int e = 0; e < m.EdgeCount; e++)
            {
                var ed = m.Edges[e];
                if (!ed.Alive) continue;
                foreach (int he in new[] { ed.He0, ed.He1 })
                {
                    if (he < 0) continue;
                    var a = UvToPx(m.Hes[he].Uv0); var b = UvToPx(m.Hes[m.Hes[he].Next].Uv0);
                    bool esel = sel.Mode == SelectMode.Edge && IsEdgeSelected(node, e);
                    var col = esel ? MeshView.EdgeSelected : ed.Seam ? MathConvert.Rgb(0xff5aa0) : MathConvert.Rgb(0xdddddd);
                    DrawLine(a, b, col, (esel ? 2f : 1f) * s, true);
                }
            }
            // UV 점
            if (sel.Mode is SelectMode.Uv or SelectMode.Edge or SelectMode.Face || true)
            {
                for (int i = 0; i < topo.Points.Count; i++)
                {
                    var p = UvToPx(topo.Points[i].Uv);
                    bool ps = selPts.Contains(i);
                    float r = (ps ? 3.5f : 2.5f) * s;
                    DrawRect(new Rect2(p - new GVec2(r, r), new GVec2(2 * r, 2 * r)), ps ? MeshView.VertexSelected : MeshView.VertexNormal);
                }
            }
        }
        if (_marquee && _marqueeEnd is { } me)
        {
            var r = RectFrom(_pressPos, me);
            DrawRect(r, new Color(1, 1, 1, 0.08f));
            DrawRect(r, new Color(1, 1, 1, 0.9f), false, 1 * s);
        }
        string modeText = sel.Mode switch { SelectMode.Uv => "UV", SelectMode.Edge => "Edge", SelectMode.Face => "Face", SelectMode.Vertex => "Vertex (use UV mode: F12)", _ => "Object" };
        DrawString(font, new GVec2(12 * s, Size.Y - 10 * s), $"{modeText} mode   tool: {_shell.Tools.Current?.Label}   zoom {(_zoom / 400f):P0}", HorizontalAlignment.Left, -1, fs, MayaTheme.TextDim);
    }

    private static float PolygonArea(GVec2[] p)
    {
        float a = 0;
        for (int i = 0; i < p.Length; i++) { var q = p[(i + 1) % p.Length]; a += p[i].X * q.Y - q.X * p[i].Y; }
        return a * 0.5f;
    }

    private static Rect2 RectFrom(GVec2 a, GVec2 b)
    {
        var min = new GVec2(MathF.Min(a.X, b.X), MathF.Min(a.Y, b.Y));
        var max = new GVec2(MathF.Max(a.X, b.X), MathF.Max(a.Y, b.Y));
        return new Rect2(min, max - min);
    }

    // ---------------------------------------------------------------- 입력

    public override void _GuiInput(InputEvent e)
    {
        float s = CubeApp.Instance.UiScale;
        switch (e)
        {
            case InputEventMouseButton mb:
                if (mb.Pressed) GrabFocus();
                if (mb.Pressed && mb.ButtonIndex == MouseButton.WheelUp) { ZoomAt(mb.Position, 1.1f); AcceptEvent(); return; }
                if (mb.Pressed && mb.ButtonIndex == MouseButton.WheelDown) { ZoomAt(mb.Position, 1 / 1.1f); AcceptEvent(); return; }
                if (mb.Pressed && mb.AltPressed && mb.ButtonIndex is MouseButton.Middle or MouseButton.Right or MouseButton.Left && _navButton == MouseButton.None)
                {
                    _navButton = mb.ButtonIndex; _last = mb.Position; AcceptEvent(); return;
                }
                if (!mb.Pressed && mb.ButtonIndex == _navButton) { _navButton = MouseButton.None; AcceptEvent(); return; }
                if (mb.ButtonIndex == MouseButton.Left)
                {
                    if (mb.Pressed)
                    {
                        if (TryBeginTransform(mb.Position)) { AcceptEvent(); return; }
                        _pressed = true; _marquee = false; _pressPos = mb.Position; _modifier = ModifierOf(mb);
                        AcceptEvent(); return;
                    }
                    if (_dragging) { EndTransform(commit: true); AcceptEvent(); return; }
                    if (_pressed)
                    {
                        _pressed = false;
                        if (_marquee) { _marquee = false; FinishMarquee(mb.Position); } else ClickSelect(mb.Position);
                        _marqueeEnd = null; QueueRedraw();
                        AcceptEvent(); return;
                    }
                }
                break;
            case InputEventMouseMotion mm:
                if (_navButton != MouseButton.None)
                {
                    var d = mm.Position - _last; _last = mm.Position;
                    if (_navButton == MouseButton.Middle) _origin += d;
                    else if (_navButton == MouseButton.Right) ZoomAt(_pressPos == GVec2.Zero ? Size / 2 : _pressPos, MathF.Exp((d.X - d.Y) * 0.004f));
                    QueueRedraw(); AcceptEvent(); return;
                }
                if (_dragging) { UpdateTransform(mm.Position); AcceptEvent(); return; }
                if (_pressed)
                {
                    if (!_marquee && (mm.Position - _pressPos).Length() >= 4 * s) _marquee = true;
                    if (_marquee) { _marqueeEnd = mm.Position; QueueRedraw(); }
                    AcceptEvent(); return;
                }
                break;
            case InputEventKey { Pressed: true, Echo: false } k:
                if (k.Keycode == Key.F && !k.CtrlPressed && !k.AltPressed) { FrameSelected(); AcceptEvent(); return; }
                if (k.Keycode == Key.A && !k.CtrlPressed && !k.AltPressed) { FrameAll(); AcceptEvent(); return; }
                if (k.Keycode == Key.Escape && _dragging) { EndTransform(commit: false); AcceptEvent(); return; }
                break;
        }
    }

    /// <summary>임베디드 창은 루트의 _Input을 받지 못하므로 캔버스가 처리하지 않은 키는 셸 핫키 라우터로 넘긴다.</summary>
    public override void _UnhandledKeyInput(InputEvent e)
    {
        if (e is not InputEventKey) return;
        _shell.Hotkeys._Input(e);
        if (GetViewport().IsInputHandled() || _shell.GetViewport().IsInputHandled()) GetViewport().SetInputAsHandled();
    }

    private void ZoomAt(GVec2 px, float factor)
    {
        var uv = PxToUv(px);
        _zoom = Math.Clamp(_zoom * factor, 20f, 20000f);
        _origin = px - new GVec2(uv.X, -uv.Y) * _zoom;
        QueueRedraw();
    }

    private static SelectModifier ModifierOf(InputEventWithModifiers e)
        => e.CtrlPressed && e.ShiftPressed ? SelectModifier.Add : e.CtrlPressed ? SelectModifier.Remove : e.ShiftPressed ? SelectModifier.Toggle : SelectModifier.Replace;

    // ---------------------------------------------------------------- 선택

    private void ClickSelect(GVec2 px)
    {
        var sel = _shell.Document.Selection;
        float s = CubeApp.Instance.UiScale;
        SelItem? hit = null; float best = float.MaxValue;
        foreach (var node in TargetNodes())
        {
            var m = node.Mesh!; var topo = Topo(node);
            switch (sel.Mode)
            {
                case SelectMode.Uv:
                    for (int i = 0; i < topo.Points.Count; i++)
                    {
                        float d = UvToPx(topo.Points[i].Uv).DistanceTo(px);
                        if (d <= 8 * s && d < best) { best = d; hit = new SelItem(node.Id, i); }
                    }
                    break;
                case SelectMode.Edge:
                    for (int e = 0; e < m.EdgeCount; e++)
                    {
                        var ed = m.Edges[e]; if (!ed.Alive) continue;
                        foreach (int he in new[] { ed.He0, ed.He1 })
                        {
                            if (he < 0) continue;
                            var a = UvToPx(m.Hes[he].Uv0); var b = UvToPx(m.Hes[m.Hes[he].Next].Uv0);
                            float d = Geometry2D.GetClosestPointToSegment(px, a, b).DistanceTo(px);
                            if (d <= 6 * s && d < best) { best = d; hit = new SelItem(node.Id, e); }
                        }
                    }
                    break;
                case SelectMode.Face:
                    for (int f = 0; f < m.FaceCount; f++)
                    {
                        if (!m.Faces[f].Alive) continue;
                        var poly = new List<GVec2>();
                        int start = m.Faces[f].HalfEdge, he = start;
                        do { poly.Add(UvToPx(m.Hes[he].Uv0)); he = m.Hes[he].Next; } while (he != start);
                        if (Geometry2D.IsPointInPolygon(px, poly.ToArray())) { hit = new SelItem(node.Id, f); best = 0; }
                    }
                    break;
                case SelectMode.Object:
                    // 오브젝트 모드에서는 면 안을 클릭하면 그 오브젝트 선택
                    for (int f = 0; f < m.FaceCount && hit == null; f++)
                    {
                        if (!m.Faces[f].Alive) continue;
                        var poly = new List<GVec2>();
                        int start = m.Faces[f].HalfEdge, he = start;
                        do { poly.Add(UvToPx(m.Hes[he].Uv0)); he = m.Hes[he].Next; } while (he != start);
                        if (Geometry2D.IsPointInPolygon(px, poly.ToArray())) hit = new SelItem(node.Id, -1);
                    }
                    break;
            }
        }
        var items = hit != null ? new[] { hit.Value } : Array.Empty<SelItem>();
        if (items.Length == 0 && _modifier != SelectModifier.Replace) return;
        if (sel.Mode == SelectMode.Object && items.Length == 0) return; // UV 편집기에서 빈 곳 클릭으로 오브젝트 선택을 지우지 않는다
        var mod = _modifier;
        _shell.RecordSelection(ss => ss.Apply(items, mod));
    }

    private void FinishMarquee(GVec2 px)
    {
        var sel = _shell.Document.Selection;
        var r = RectFrom(_pressPos, px);
        var items = new List<SelItem>();
        foreach (var node in TargetNodes())
        {
            var m = node.Mesh!; var topo = Topo(node);
            switch (sel.Mode)
            {
                case SelectMode.Uv:
                    for (int i = 0; i < topo.Points.Count; i++) if (r.HasPoint(UvToPx(topo.Points[i].Uv))) items.Add(new SelItem(node.Id, i));
                    break;
                case SelectMode.Edge:
                    for (int e = 0; e < m.EdgeCount; e++)
                    {
                        var ed = m.Edges[e]; if (!ed.Alive) continue;
                        bool inside = false;
                        foreach (int he in new[] { ed.He0, ed.He1 })
                        {
                            if (he < 0) continue;
                            var a = UvToPx(m.Hes[he].Uv0); var b = UvToPx(m.Hes[m.Hes[he].Next].Uv0);
                            if (r.HasPoint(a) || r.HasPoint(b)) inside = true;
                        }
                        if (inside) items.Add(new SelItem(node.Id, e));
                    }
                    break;
                case SelectMode.Face:
                    for (int f = 0; f < m.FaceCount; f++)
                    {
                        if (!m.Faces[f].Alive) continue;
                        var c = GVec2.Zero; int n = 0;
                        int start = m.Faces[f].HalfEdge, he = start;
                        do { c += UvToPx(m.Hes[he].Uv0); n++; he = m.Hes[he].Next; } while (he != start);
                        if (r.HasPoint(c / n)) items.Add(new SelItem(node.Id, f));
                    }
                    break;
            }
        }
        if (items.Count == 0 && _modifier != SelectModifier.Replace) return;
        if (sel.Mode == SelectMode.Object) return;
        var mod = _modifier;
        _shell.RecordSelection(ss => ss.Apply(items, mod));
    }

    // ---------------------------------------------------------------- 변형 (W/E/R)

    private bool TryBeginTransform(GVec2 px)
    {
        var tool = _shell.Tools.Current?.Id ?? "select";
        if (tool is not ("move" or "rotate" or "scale")) return false;
        _xform.Clear();
        var center = NVec2.Zero; int count = 0;
        foreach (var node in TargetNodes())
        {
            var pts = SelectedPoints(node);
            if (pts.Count == 0) continue;
            var topo = Topo(node);
            var ids = pts.ToArray();
            var init = ids.Select(i => topo.Points[i].Uv).ToArray();
            foreach (var uv in init) { center += uv; count++; }
            var cmd = new UvEditCommand(tool switch { "move" => "Move UVs", "rotate" => "Rotate UVs", _ => "Scale UVs" }, node.Id);
            cmd.Capture(_shell.Document);
            _xform.Add((node.Id, topo, ids, init, cmd));
        }
        if (count == 0) return false;
        _xformCenter = center / count;
        _xformTool = tool; _pressPos = px; _dragging = true;
        return true;
    }

    private void UpdateTransform(GVec2 px)
    {
        var d = px - _pressPos;
        Matrix3x2 xf;
        switch (_xformTool)
        {
            case "move": xf = Matrix3x2.CreateTranslation(d.X / _zoom, -d.Y / _zoom); break;
            case "rotate": xf = Matrix3x2.CreateRotation(-d.X * 0.01f, _xformCenter); break;
            default:
                {
                    float f = MathF.Max(1f + (d.X - d.Y) * 0.005f, 0.01f);
                    xf = Matrix3x2.CreateScale(f, f, _xformCenter); break;
                }
        }
        foreach (var (id, topo, ids, init, _) in _xform)
        {
            var mesh = _shell.Document.Get(id).Mesh!;
            for (int i = 0; i < ids.Length; i++) UvOps.SetPointUv(mesh, topo, ids[i], NVec2.Transform(init[i], xf));
            _shell.Document.Notify(new DocChange(ChangeKind.MeshAttributes, id));
        }
        QueueRedraw();
    }

    private void EndTransform(bool commit)
    {
        _dragging = false;
        var doc = _shell.Document;
        using (doc.Undo.BeginGroup(_xform.Count > 0 ? _xform[0].cmd.Name : "UV"))
        {
            foreach (var (id, topo, ids, init, cmd) in _xform)
            {
                var mesh = doc.Get(id).Mesh!;
                if (!commit)
                {
                    for (int i = 0; i < ids.Length; i++) UvOps.SetPointUv(mesh, topo, ids[i], init[i]);
                    doc.Notify(new DocChange(ChangeKind.MeshAttributes, id));
                    continue;
                }
                cmd.Commit(doc);
                if (cmd.Changed) doc.Undo.Push(cmd, alreadyApplied: true);
            }
        }
        _xform.Clear();
        QueueRedraw();
    }
}
