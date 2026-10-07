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

public enum UvBackground { None, Grid, UvTexture, Mapped }

/// <summary>
/// UV 편집 캔버스. 선택된 오브젝트들의 UV를 0..1 그리드 위에 그리고, 뷰포트와 같은 선택 UI(클릭/마키/Shift·Ctrl 수식어/호버)와
/// 2D 조작기(W 이동 / E 회전 / R 스케일)를 제공한다. Island 모드는 심으로 완전히 분리된 UV 섬 단위로 선택한다.
/// 파이 메뉴 정책은 뷰포트와 같다: RMB = 모드, Shift+RMB = Edit(UV 기능), Ctrl+RMB = Select(변환).
/// 내비게이션: Alt+MMB 팬, Alt+RMB·휠 줌, F 프레임, A 전체.
/// </summary>
public partial class UvCanvas : Control
{
    private Shell _shell = null!;
    private UvBackground _background = UvBackground.UvTexture;
    public UvBackground Background { get => _background; set { _background = value; QueueRedraw(); } }
    /// <summary>Island 선택 모드(UV 모드의 변형: 점 하나를 집으면 같은 섬 전체).</summary>
    public bool IslandMode { get; private set; }
    public event Action? IslandModeChanged;
    private PieMenu _pie = null!;
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
    private SelItem? _hover;

    // 조작기
    private enum Part { None, X, Y, Center, Ring }
    private Part _hoverPart = Part.None, _dragPart = Part.None;
    private NVec2 _pivotUv;
    private bool _hasPivot;

    // 변형 드래그
    private readonly List<(NodeId node, UvTopology topo, int[] points, NVec2[] initial, UvEditCommand cmd)> _xform = new();
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
        _pie = new PieMenu { Name = "UvPie" };
        AddChild(_pie);
        shell.Document.Changed += OnDocChanged;
        shell.Document.Selection.Changed += QueueRedraw;
        shell.Document.Selection.ModeChanged += () => { _hover = null; QueueRedraw(); };
        shell.Tools.ToolChanged += _ => QueueRedraw();
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

    public void SetIslandMode(bool on)
    {
        IslandMode = on;
        if (on && _shell.Document.Selection.Mode != SelectMode.Uv) _shell.Actions.Invoke("mode.uv");
        IslandModeChanged?.Invoke();
        QueueRedraw();
    }

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

    /// <summary>현재 모드의 선택을 UV 점 집합으로 변환(UV 모드: 그대로, 엣지/면: 코너의 UV 점, 오브젝트: 전체).</summary>
    public HashSet<int> SelectedPoints(SceneNode node)
    {
        var sel = _shell.Document.Selection;
        var set = new HashSet<int>();
        if (!sel.Components.TryGetValue(node.Id, out var comps))
        {
            if (sel.Mode == SelectMode.Object && sel.IsObjectSelected(node.Id)) for (int i = 0; i < Topo(node).Points.Count; i++) set.Add(i);
            return set;
        }
        var topo = Topo(node); var m = node.Mesh!;
        switch (sel.Mode)
        {
            case SelectMode.Uv: set.UnionWith(comps.Uvs.Where(i => i < topo.Points.Count)); break;
            case SelectMode.Vertex:
                for (int i = 0; i < topo.Points.Count; i++) if (comps.Verts.Contains(topo.Points[i].Vertex)) set.Add(i);
                break;
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

    /// <summary>선택 UV 점들의 중심(UV 공간). 없으면 false.</summary>
    private bool ComputePivot(out NVec2 pivot)
    {
        var sum = NVec2.Zero; int n = 0;
        foreach (var node in TargetNodes())
        {
            var topo = Topo(node);
            foreach (int p in SelectedPoints(node)) { sum += topo.Points[p].Uv; n++; }
        }
        pivot = n > 0 ? sum / n : NVec2.Zero;
        return n > 0;
    }

    private bool GizmoActive => _shell.Tools.Current?.Id is "move" or "rotate" or "scale";
    private float GizmoLen => 70f * CubeApp.Instance.UiScale;

    // ---------------------------------------------------------------- 그리기

    public override void _Draw()
    {
        float s = CubeApp.Instance.UiScale;
        DrawRect(new Rect2(GVec2.Zero, Size), MathConvert.Rgb(0x2b2b2b));
        var p0 = UvToPx(new NVec2(0, 1)); var p1 = UvToPx(new NVec2(1, 0));
        var unit = new Rect2(p0, p1 - p0);
        switch (_background)
        {
            case UvBackground.UvTexture:
                if (_gridTex != null) DrawTextureRect(_gridTex, unit, false, new Color(1, 1, 1, 0.28f));
                else DrawRect(unit, MathConvert.Rgb(0x3a3a3a));
                break;
            case UvBackground.Mapped:
                {
                    var tex = MappedTexture();
                    if (tex != null) DrawTextureRect(tex, unit, false, Colors.White);
                    else DrawRect(unit, MathConvert.Rgb(0x3a3a3a));
                    break;
                }
            case UvBackground.Grid:
                DrawRect(unit, MathConvert.Rgb(0x333333));
                for (int i = 0; i <= 10; i++)
                {
                    float t = i / 10f;
                    var col = i % 5 == 0 ? MathConvert.Rgb(0x6a6a6a) : MathConvert.Rgb(0x4a4a4a);
                    DrawLine(UvToPx(new NVec2(t, 0)), UvToPx(new NVec2(t, 1)), col, 1 * s);
                    DrawLine(UvToPx(new NVec2(0, t)), UvToPx(new NVec2(1, t)), col, 1 * s);
                }
                break;
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
                bool fhov = sel.Mode == SelectMode.Face && _hover is { } hf && hf.Node == node.Id && hf.Component == f;
                var poly = new List<GVec2>();
                int start = m.Faces[f].HalfEdge, he = start;
                do { poly.Add(UvToPx(m.Hes[he].Uv0)); he = m.Hes[he].Next; } while (he != start);
                if (poly.Count < 3) continue;
                var arr = poly.ToArray();
                if (MathF.Abs(PolygonArea(arr)) < 0.5f) continue; // 퇴화 면(원통 캡 등)은 채우지 않는다
                var tris = Geometry2D.TriangulatePolygon(arr);
                if (tris.Length == 0) continue;
                var col = fsel ? new Color(1f, 0.55f, 0f, 0.35f) : fhov ? new Color(1f, 1f, 1f, 0.18f) : new Color(0.6f, 0.75f, 1f, 0.08f);
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
                    bool ehov = sel.Mode == SelectMode.Edge && _hover is { } hv && hv.Node == node.Id && hv.Component == e;
                    // 심(Cut된 엣지)은 굵은 노란색, 선택 엣지는 주황색, 호버는 흰색
                    var col = ehov ? MeshView.Hover : esel ? MeshView.EdgeSelected : ed.Seam ? MathConvert.Rgb(0xffe034) : MathConvert.Rgb(0xdddddd);
                    DrawLine(a, b, col, (esel || ehov ? 2.5f : ed.Seam ? 2.5f : 1f) * s, true);
                }
            }
            // UV 점(파랑, 선택 빨강, 호버 흰색)
            for (int i = 0; i < topo.Points.Count; i++)
            {
                var p = UvToPx(topo.Points[i].Uv);
                bool ps = selPts.Contains(i);
                bool ph = sel.Mode == SelectMode.Uv && _hover is { } hp && hp.Node == node.Id && hp.Component >= 0 && hp.Component < topo.Points.Count
                          && (IslandMode ? topo.Points[hp.Component].Shell == topo.Points[i].Shell : hp.Component == i);
                float r = (ps || ph ? 3.5f : 2.5f) * s;
                DrawRect(new Rect2(p - new GVec2(r, r), new GVec2(2 * r, 2 * r)), ph ? MeshView.Hover : ps ? MeshView.UvSelected : MeshView.UvNormal);
            }
        }
        if (_marquee && _marqueeEnd is { } me)
        {
            var r = RectFrom(_pressPos, me);
            DrawRect(r, new Color(1, 1, 1, 0.08f));
            DrawRect(r, new Color(1, 1, 1, 0.9f), false, 1 * s);
        }
        DrawGizmo(s);
        string modeText = IslandMode && sel.Mode == SelectMode.Uv ? "Island" : sel.Mode switch { SelectMode.Uv => "UV", SelectMode.Edge => "Edge", SelectMode.Face => "Face", SelectMode.Vertex => "Vertex", _ => "Object" };
        DrawString(font, new GVec2(12 * s, Size.Y - 10 * s), $"{modeText} mode   tool: {_shell.Tools.Current?.Label}   zoom {(_zoom / 400f):P0}", HorizontalAlignment.Left, -1, fs, MayaTheme.TextDim);
    }

    private static readonly Color AxisX = MathConvert.Rgb(0xff2a2a), AxisY = MathConvert.Rgb(0x5aff2a), AxisC = MathConvert.Rgb(0x6ad0ff), Active = MathConvert.Rgb(0xffff00);

    /// <summary>2D 조작기: 이동(X/Y 화살표 + 중앙 사각), 회전(링), 스케일(X/Y 상자 + 중앙).</summary>
    private void DrawGizmo(float s)
    {
        if (!GizmoActive) { _hasPivot = false; return; }
        if (!_dragging) _hasPivot = ComputePivot(out _pivotUv);
        if (!_hasPivot) return;
        var c = UvToPx(_pivotUv);
        float len = GizmoLen;
        string tool = _shell.Tools.Current!.Id;
        Color Col(Part p, Color normal) => (_dragging ? _dragPart : _hoverPart) == p ? Active : normal;
        if (tool == "rotate")
        {
            DrawArc(c, len, 0, Mathf.Tau, 64, Col(Part.Ring, AxisC), 2 * s, true);
            DrawCircle(c, 3 * s, Col(Part.Ring, AxisC));
            return;
        }
        var ex = c + new GVec2(len, 0); var ey = c - new GVec2(0, len);
        DrawLine(c, ex, Col(Part.X, AxisX), 2 * s, true);
        DrawLine(c, ey, Col(Part.Y, AxisY), 2 * s, true);
        float h = 7 * s;
        if (tool == "move")
        {
            DrawColoredPolygon(new[] { ex + new GVec2(h, 0), ex + new GVec2(-h, -h * 0.7f), ex + new GVec2(-h, h * 0.7f) }, Col(Part.X, AxisX));
            DrawColoredPolygon(new[] { ey - new GVec2(0, h), ey + new GVec2(-h * 0.7f, h), ey + new GVec2(h * 0.7f, h) }, Col(Part.Y, AxisY));
        }
        else
        {
            DrawRect(new Rect2(ex - new GVec2(h, h) * 0.7f, new GVec2(h, h) * 1.4f), Col(Part.X, AxisX));
            DrawRect(new Rect2(ey - new GVec2(h, h) * 0.7f, new GVec2(h, h) * 1.4f), Col(Part.Y, AxisY));
        }
        float cs = 6 * s;
        DrawRect(new Rect2(c - new GVec2(cs, cs), new GVec2(2 * cs, 2 * cs)), Col(Part.Center, AxisC));
    }

    private Part HitGizmo(GVec2 px)
    {
        if (!GizmoActive || !_hasPivot) return Part.None;
        float s = CubeApp.Instance.UiScale;
        var c = UvToPx(_pivotUv);
        float len = GizmoLen, th = 8 * s;
        string tool = _shell.Tools.Current!.Id;
        if (tool == "rotate") return MathF.Abs(px.DistanceTo(c) - len) <= th ? Part.Ring : Part.None;
        if (px.DistanceTo(c) <= 9 * s) return Part.Center;
        if (Hotkeys.ShellInput.Verbose) GD.Print($"[Uv] hit px={px} c={c} len={len} dX={Geometry2D.GetClosestPointToSegment(px, c, c + new GVec2(len + 6 * s, 0)).DistanceTo(px):F1} th={th:F1}");
        if (Geometry2D.GetClosestPointToSegment(px, c, c + new GVec2(len + 6 * s, 0)).DistanceTo(px) <= th) return Part.X;
        if (Geometry2D.GetClosestPointToSegment(px, c, c - new GVec2(0, len + 6 * s)).DistanceTo(px) <= th) return Part.Y;
        return Part.None;
    }

    private static float PolygonArea(GVec2[] p)
    {
        float a = 0;
        for (int i = 0; i < p.Length; i++) { var q = p[(i + 1) % p.Length]; a += p[i].X * q.Y - q.X * p[i].Y; }
        return a * 0.5f;
    }

    /// <summary>Mapped Texture 배경: 대상 노드의 머티리얼 텍스처. 아직 머티리얼 텍스처가 없으면 null.</summary>
    private Texture2D? MappedTexture()
    {
        foreach (var node in TargetNodes())
        {
            var mv = _shell.Viewport.Scene.GetMeshView(node.Id);
            if (mv?.MappedTexture != null) return mv.MappedTexture;
        }
        return null;
    }

    private static bool RectIntersectsSegment(Rect2 r, GVec2 a, GVec2 b)
    {
        if (r.HasPoint(a) || r.HasPoint(b)) return true;
        var p0 = r.Position; var p1 = r.End;
        var corners = new[] { p0, new GVec2(p1.X, p0.Y), p1, new GVec2(p0.X, p1.Y) };
        for (int i = 0; i < 4; i++) if (Geometry2D.SegmentIntersectsSegment(a, b, corners[i], corners[(i + 1) % 4]).VariantType != Variant.Type.Nil) return true;
        return false;
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
                if (HandlePie(mb)) { AcceptEvent(); return; }
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
                        var part = HitGizmo(mb.Position);
                        if (Hotkeys.ShellInput.Verbose) GD.Print($"[Uv] press {mb.Position} part={part} hasPivot={_hasPivot} gizmo={GizmoActive}");
                        if (part != Part.None && TryBeginTransform(mb.Position, part)) { AcceptEvent(); return; }
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
                if (_pie.IsOpen) { _pie.UpdatePointer(mm.Position); AcceptEvent(); return; }
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
                UpdateHover(mm.Position);
                break;
            case InputEventKey { Pressed: true, Echo: false } k:
                if (k.Keycode == Key.F && !k.CtrlPressed && !k.AltPressed) { FrameSelected(); AcceptEvent(); return; }
                if (k.Keycode == Key.A && !k.CtrlPressed && !k.AltPressed) { FrameAll(); AcceptEvent(); return; }
                if (k.Keycode == Key.Escape && _dragging) { EndTransform(commit: false); AcceptEvent(); return; }
                break;
        }
    }

    public override void _Notification(int what)
    {
        if (what == NotificationMouseExit) { if (_hover != null || _hoverPart != Part.None) { _hover = null; _hoverPart = Part.None; QueueRedraw(); } }
    }

    private void UpdateHover(GVec2 px)
    {
        var part = HitGizmo(px);
        var hit = part == Part.None ? Pick(px) : null;
        if (part != _hoverPart || !Nullable.Equals(hit, _hover)) { _hoverPart = part; _hover = hit; QueueRedraw(); }
    }

    /// <summary>임베디드 창은 루트의 _Input을 받지 못하므로 캔버스가 처리하지 않은 키는 셸 핫키 라우터로 넘긴다.</summary>
    public override void _UnhandledKeyInput(InputEvent e)
    {
        if (e is not InputEventKey) return;
        _shell.Hotkeys._Input(e);
        if (GetViewport().IsInputHandled() || _shell.GetViewport().IsInputHandled()) GetViewport().SetInputAsHandled();
    }

    /// <summary>RMB 홀드 파이: 기본 = 모드, Shift = Edit(UV 기능), Ctrl = Select(변환). 떼면 하이라이트 항목 실행.</summary>
    private bool HandlePie(InputEventMouseButton mb)
    {
        if (mb.ButtonIndex == MouseButton.Right)
        {
            if (mb.Pressed)
            {
                if (mb.AltPressed || _pie.IsOpen || _dragging) return _pie.IsOpen;
                var items = mb.CtrlPressed ? PieMenus.UvSelectMenu(_shell) : mb.ShiftPressed ? PieMenus.UvMenu(_shell) : PieMenus.UvModeMenu(_shell, IslandMode);
                _pie.Open(items, mb.Position);
                return _pie.IsOpen;
            }
            if (_pie.IsOpen)
            {
                var chosen = _pie.Release();
                if (chosen != null && chosen.Enabled) _shell.Actions.Invoke(chosen.ActionId);
                QueueRedraw();
                return true;
            }
            return false;
        }
        return _pie.IsOpen;
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

    private static List<GVec2> FacePoly(PolyMesh m, int f, Func<NVec2, GVec2> toPx)
    {
        var poly = new List<GVec2>();
        int start = m.Faces[f].HalfEdge, he = start;
        do { poly.Add(toPx(m.Hes[he].Uv0)); he = m.Hes[he].Next; } while (he != start);
        return poly;
    }

    /// <summary>Island 모드: 점(8px) → 엣지(6px) → 면 안 순으로 집어 그 요소가 속한 섬의 대표 UV 점을 돌려준다.</summary>
    private SelItem? PickIsland(GVec2 px, SceneNode node, ref float best)
    {
        float s = CubeApp.Instance.UiScale;
        var m = node.Mesh!; var topo = Topo(node);
        SelItem? hit = null;
        for (int i = 0; i < topo.Points.Count; i++)
        {
            float d = UvToPx(topo.Points[i].Uv).DistanceTo(px);
            if (d <= 8 * s && d < best) { best = d; hit = new SelItem(node.Id, i); }
        }
        if (hit != null) return hit;
        for (int e = 0; e < m.EdgeCount; e++)
        {
            var ed = m.Edges[e]; if (!ed.Alive) continue;
            foreach (int he in new[] { ed.He0, ed.He1 })
            {
                if (he < 0) continue;
                var a = UvToPx(m.Hes[he].Uv0); var b = UvToPx(m.Hes[m.Hes[he].Next].Uv0);
                float d = Geometry2D.GetClosestPointToSegment(px, a, b).DistanceTo(px);
                if (d <= 6 * s && d < best) { best = d; hit = new SelItem(node.Id, topo.HeToPoint[he]); }
            }
        }
        if (hit != null) return hit;
        for (int f = 0; f < m.FaceCount; f++)
        {
            if (!m.Faces[f].Alive) continue;
            if (Geometry2D.IsPointInPolygon(px, FacePoly(m, f, UvToPx).ToArray())) { best = 0; return new SelItem(node.Id, topo.HeToPoint[m.Faces[f].HalfEdge]); }
        }
        return null;
    }

    /// <summary>현재 모드에서 커서 아래 항목(UV 모드: 점 id, 엣지/면: id, 오브젝트: 면 안의 오브젝트 → Component -1).</summary>
    private SelItem? Pick(GVec2 px)
    {
        var sel = _shell.Document.Selection;
        float s = CubeApp.Instance.UiScale;
        SelItem? hit = null; float best = float.MaxValue;
        foreach (var node in TargetNodes())
        {
            var m = node.Mesh!; var topo = Topo(node);
            if (sel.Mode == SelectMode.Uv && IslandMode) { var ih = PickIsland(px, node, ref best); if (ih != null) hit = ih; continue; }
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
                case SelectMode.Object:
                    for (int f = 0; f < m.FaceCount; f++)
                    {
                        if (!m.Faces[f].Alive) continue;
                        if (Geometry2D.IsPointInPolygon(px, FacePoly(m, f, UvToPx).ToArray())) { hit = new SelItem(node.Id, sel.Mode == SelectMode.Face ? f : -1); best = 0; break; }
                    }
                    break;
            }
        }
        return hit;
    }

    /// <summary>Island 모드: 점 하나를 그 점이 속한 섬 전체로 확장.</summary>
    private IEnumerable<SelItem> ExpandIsland(SelItem item)
    {
        if (!IslandMode) { yield return item; yield break; }
        var node = _shell.Document.Find(item.Node); if (node?.Mesh == null) yield break;
        var topo = Topo(node);
        int shell = topo.Points[item.Component].Shell;
        foreach (int p in topo.PointsInShell(shell)) yield return new SelItem(item.Node, p);
    }

    private void ClickSelect(GVec2 px)
    {
        var sel = _shell.Document.Selection;
        var hit = Pick(px);
        var items = hit != null ? (sel.Mode == SelectMode.Uv ? ExpandIsland(hit.Value).ToArray() : new[] { hit.Value }) : Array.Empty<SelItem>();
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
                    {
                        var shells = new HashSet<int>();
                        for (int i = 0; i < topo.Points.Count; i++)
                            if (r.HasPoint(UvToPx(topo.Points[i].Uv))) { if (IslandMode) shells.Add(topo.Points[i].Shell); else items.Add(new SelItem(node.Id, i)); }
                        if (IslandMode)
                        {
                            // 섬 모드: 사각형이 엣지를 가로지르거나 면 중심을 포함해도 그 섬
                            for (int f = 0; f < m.FaceCount; f++)
                            {
                                if (!m.Faces[f].Alive) continue;
                                var poly = FacePoly(m, f, UvToPx);
                                var c = GVec2.Zero; foreach (var p in poly) c += p; c /= poly.Count;
                                bool inside = r.HasPoint(c);
                                for (int i = 0; i < poly.Count && !inside; i++) if (RectIntersectsSegment(r, poly[i], poly[(i + 1) % poly.Count])) inside = true;
                                if (inside) shells.Add(topo.Points[topo.HeToPoint[m.Faces[f].HalfEdge]].Shell);
                            }
                        }
                        foreach (int sh in shells) foreach (int p in topo.PointsInShell(sh)) items.Add(new SelItem(node.Id, p));
                        break;
                    }
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
                        var poly = FacePoly(m, f, UvToPx);
                        var c = GVec2.Zero; foreach (var p in poly) c += p;
                        if (r.HasPoint(c / poly.Count)) items.Add(new SelItem(node.Id, f));
                    }
                    break;
            }
        }
        if (items.Count == 0 && _modifier != SelectModifier.Replace) return;
        if (sel.Mode == SelectMode.Object) return;
        var mod = _modifier;
        _shell.RecordSelection(ss => ss.Apply(items, mod));
    }

    // ---------------------------------------------------------------- 변형 (조작기 드래그)

    private bool TryBeginTransform(GVec2 px, Part part)
    {
        var tool = _shell.Tools.Current?.Id ?? "select";
        if (tool is not ("move" or "rotate" or "scale")) return false;
        _xform.Clear();
        int count = 0;
        foreach (var node in TargetNodes())
        {
            var pts = SelectedPoints(node);
            if (pts.Count == 0) continue;
            var topo = Topo(node);
            var ids = pts.ToArray();
            var init = ids.Select(i => topo.Points[i].Uv).ToArray();
            count += ids.Length;
            var cmd = new UvEditCommand(tool switch { "move" => "Move UVs", "rotate" => "Rotate UVs", _ => "Scale UVs" }, node.Id);
            cmd.Capture(_shell.Document);
            _xform.Add((node.Id, topo, ids, init, cmd));
        }
        if (count == 0) return false;
        _xformTool = tool; _pressPos = px; _dragging = true; _dragPart = part;
        if (Hotkeys.ShellInput.Verbose) GD.Print($"[Uv] transform begin tool={tool} part={part} points={count} pivotPx={UvToPx(_pivotUv)}");
        return true;
    }

    private void UpdateTransform(GVec2 px)
    {
        var d = px - _pressPos;
        var c = UvToPx(_pivotUv);
        Matrix3x2 xf;
        switch (_xformTool)
        {
            case "move":
                {
                    var delta = new NVec2(d.X / _zoom, -d.Y / _zoom);
                    if (_dragPart == Part.X) delta.Y = 0; else if (_dragPart == Part.Y) delta.X = 0;
                    xf = Matrix3x2.CreateTranslation(delta); break;
                }
            case "rotate":
                {
                    float a0 = MathF.Atan2(_pressPos.Y - c.Y, _pressPos.X - c.X), a1 = MathF.Atan2(px.Y - c.Y, px.X - c.X);
                    float angle = -(a1 - a0); // 화면 y가 아래로 커지므로 부호 반전(UV 공간 반시계 = 양수)
                    if (_shell.Viewport.IsSnapHeld) angle = MathF.Round(angle / (MathF.PI / 12f)) * (MathF.PI / 12f);
                    xf = Matrix3x2.CreateRotation(angle, _pivotUv); break;
                }
            default:
                {
                    float len = GizmoLen;
                    float fx = 1f, fy = 1f;
                    if (_dragPart == Part.X) fx = MathF.Max((len + d.X) / len, 0.01f);
                    else if (_dragPart == Part.Y) fy = MathF.Max((len - d.Y) / len, 0.01f);
                    else { float f = MathF.Max(1f + (d.X - d.Y) * 0.005f, 0.01f); fx = fy = f; }
                    xf = Matrix3x2.CreateScale(fx, fy, _pivotUv); break;
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
        _dragging = false; _dragPart = Part.None;
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
