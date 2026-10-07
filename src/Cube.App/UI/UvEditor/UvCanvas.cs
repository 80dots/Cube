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

public enum UvBackground { None, Grid, UvTexture, Mapped, Checker }

/// <summary>UV 편집기 캔버스 툴(Maya UV Editor Tools 메뉴). None = 선택/조작기.</summary>
public enum UvCanvasTool { None, Tweak, Grab, Smooth, Pinch, Smear, PinBrush, CutSew, MoveShell }

/// <summary>
/// UV 편집 캔버스. 선택된 오브젝트들의 UV를 0..1 그리드 위에 그리고, 뷰포트와 같은 선택 UI(클릭/마키/Shift·Ctrl 수식어/호버)와
/// 2D 조작기(W 이동 / E 회전 / R 스케일), 브러시·Tweak·Cut/Sew·Move Shell 툴을 제공한다. Island 모드는 심으로 분리된 UV 섬 단위 선택.
/// 표시 옵션: Shaded(앞/뒤 색), Distortion, Texture Borders, Isolate Select, Grid, UDIM 타일, 이미지 Dim/Unfiltered/Pixel Snap, Checker 배경, Pin(파랑), 통계 HUD.
/// 파이 메뉴 정책은 뷰포트와 같다: RMB = 모드, Shift+RMB = Edit(UV 기능, 서브 파이), Ctrl+RMB = Select(변환).
/// 내비게이션: Alt+MMB 팬, Alt+RMB·휠 줌, F 프레임, A 전체.
/// </summary>
public partial class UvCanvas : Control
{
    private Shell _shell = null!;
    private UvBackground _background = UvBackground.UvTexture;
    public UvBackground Background { get => _background; set { _background = value; QueueRedraw(); } }
    public bool IslandMode { get; private set; }
    public event Action? IslandModeChanged;
    private PieMenu _pie = null!;
    private float _zoom = 400f;
    private GVec2 _origin;
    private Texture2D? _gridTex;
    private ImageTexture? _checkerTex; private int _checkerTexSize = -1;

    // 표시 옵션
    private bool _shaded, _distortion, _texBorders = true, _showStats, _gridLines = true, _tiles, _dim = true, _unfiltered, _pixelSnap;
    public bool Shaded { get => _shaded; set { _shaded = value; QueueRedraw(); } }
    public bool Distortion { get => _distortion; set { _distortion = value; QueueRedraw(); } }
    public bool TextureBorders { get => _texBorders; set { _texBorders = value; QueueRedraw(); } }
    public bool ShowStats { get => _showStats; set { _showStats = value; QueueRedraw(); } }
    public bool ShowGridLines { get => _gridLines; set { _gridLines = value; QueueRedraw(); } }
    public bool ShowTiles { get => _tiles; set { _tiles = value; QueueRedraw(); } }
    public bool DimImage { get => _dim; set { _dim = value; QueueRedraw(); } }
    public bool Unfiltered { get => _unfiltered; set { _unfiltered = value; TextureFilter = value ? TextureFilterEnum.Nearest : TextureFilterEnum.Linear; QueueRedraw(); } }
    public bool PixelSnap { get => _pixelSnap; set { _pixelSnap = value; QueueRedraw(); } }
    public int CheckerSize { get; private set; } = 8;
    public void SetCheckerSize(int n) { CheckerSize = Math.Clamp(n, 1, 256); QueueRedraw(); }
    private Dictionary<NodeId, HashSet<int>>? _isolate;
    public bool Isolated => _isolate != null;
    private UvCanvasTool _tool;
    public UvCanvasTool Tool { get => _tool; set { _tool = value; _brushPos = null; QueueRedraw(); _shell.HelpLine.Text = ToolHelp(value); } }

    private readonly Dictionary<NodeId, UvTopology> _topos = new();
    private bool _dragging;

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

    // 변형 드래그(조작기/Tweak/브러시/셸 이동 공용)
    private readonly List<(NodeId node, UvTopology topo, int[] points, NVec2[] initial, UvEditCommand cmd)> _xform = new();
    private string _xformTool = "";
    private GVec2? _brushPos; private GVec2 _brushLast; private bool _brushing; private bool _cutSewPainting; private bool _cutSewSew;
    private readonly HashSet<(NodeId, int)> _cutSewDone = new();

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
        if (_shell != null) { _shell.Document.Changed -= OnDocChanged; _shell.Document.Selection.Changed -= QueueRedraw; }
    }

    private void OnDocChanged(DocChange c)
    {
        if (_dragging) return;
        if (c.Kind is ChangeKind.MeshTopology or ChangeKind.MeshAttributes or ChangeKind.NodeRemoved or ChangeKind.Reset) { _topos.Remove(c.Node); _statsCache.Remove(c.Node); _distortionCache.Remove(c.Node); }
        if (c.Kind == ChangeKind.Reset) { _topos.Clear(); _isolate = null; }
        QueueRedraw();
    }

    public void Invalidate() { _topos.Clear(); _statsCache.Clear(); _distortionCache.Clear(); QueueRedraw(); }

    public void SetIslandMode(bool on)
    {
        IslandMode = on;
        if (on && _shell.Document.Selection.Mode != SelectMode.Uv) _shell.Actions.Invoke("mode.uv");
        IslandModeChanged?.Invoke();
        QueueRedraw();
    }

    private static string ToolHelp(UvCanvasTool t) => t switch
    {
        UvCanvasTool.Tweak => "Tweak UV: drag a UV point (or the selection) directly.",
        UvCanvasTool.Grab => "Grab UV: drag to move UVs inside the brush. Ctrl+wheel changes the radius (Tools > Brush Options...).",
        UvCanvasTool.Smooth => "Smooth UV: drag to relax UVs inside the brush (pinned UVs stay).",
        UvCanvasTool.Pinch => "Pinch UV: drag to pull UVs toward the brush center.",
        UvCanvasTool.Smear => "Smear UV: drag to push UVs along the stroke.",
        UvCanvasTool.PinBrush => "Pin UV: drag to pin UVs (Ctrl = unpin).",
        UvCanvasTool.CutSew => "Cut/Sew UV: click or drag over edges to cut, Ctrl to sew.",
        UvCanvasTool.MoveShell => "Move UV Shell: drag a shell to move it.",
        _ => "UV Editor: W/E/R manipulators, RMB mode pie, Shift+RMB edit pie, Ctrl+RMB select pie.",
    };

    // ---------------------------------------------------------------- 데이터

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

    private bool FaceVisible(NodeId node, int f) => _isolate == null || (_isolate.TryGetValue(node, out var set) && set.Contains(f));

    /// <summary>Isolate Select: 현재 선택(면/UV 점이 속한 면)만 표시 ↔ 해제.</summary>
    public void ToggleIsolate()
    {
        if (_isolate != null) { _isolate = null; QueueRedraw(); return; }
        var map = new Dictionary<NodeId, HashSet<int>>();
        foreach (var node in TargetNodes())
        {
            var topo = Topo(node); var pts = SelectedPoints(node);
            if (pts.Count == 0) continue;
            map[node.Id] = new HashSet<int>(UvOps.FacesOfPoints(node.Mesh!, topo, pts, all: false));
        }
        _isolate = map.Count > 0 ? map : null;
        QueueRedraw();
    }

    // ---------------------------------------------------------------- 좌표

    public GVec2 UvToPx(NVec2 uv) => _origin + new GVec2(uv.X, -uv.Y) * _zoom;
    public NVec2 PxToUv(GVec2 px) => new((px.X - _origin.X) / _zoom, -(px.Y - _origin.Y) / _zoom);

    public void FrameAll()
    {
        if (Size.X < 10 || Size.Y < 10) return;
        var min = new NVec2(0, 0); var max = new NVec2(1, 1);
        foreach (var n in TargetNodes()) foreach (var p in Topo(n).Points) { min = NVec2.Min(min, p.Uv); max = NVec2.Max(max, p.Uv); }
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

    public HashSet<int> SelectedPoints(SceneNode node) => _shell.UvPointSelection(node, Topo(node));

    private bool IsEdgeSelected(SceneNode node, int e) => _shell.Document.Selection.IsComponentSelected(node.Id, SelectMode.Edge, e);
    private bool IsFaceSelected(SceneNode node, int f) => _shell.Document.Selection.IsComponentSelected(node.Id, SelectMode.Face, f);

    private bool ComputePivot(out NVec2 pivot)
    {
        var sum = NVec2.Zero; int n = 0;
        foreach (var node in TargetNodes()) { var topo = Topo(node); foreach (int p in SelectedPoints(node)) { sum += topo.Points[p].Uv; n++; } }
        pivot = n > 0 ? sum / n : NVec2.Zero;
        return n > 0;
    }

    private bool GizmoActive => _tool == UvCanvasTool.None && _shell.Tools.Current?.Id is "move" or "rotate" or "scale";
    private float GizmoLen => 70f * CubeApp.Instance.UiScale;

    // ---------------------------------------------------------------- 그리기

    private readonly Dictionary<NodeId, (int version, float[] ratio)> _distortionCache = new();
    private readonly Dictionary<NodeId, (int version, (int shells, int overlapping, int reversed, float usage) stats)> _statsCache = new();

    private Texture2D? CheckerTexture()
    {
        int n = Math.Max(CheckerSize, 1);
        if (_checkerTex != null && _checkerTexSize == n) return _checkerTex;
        int px = Math.Max(8, 512 / n) * n;
        var img = Image.CreateEmpty(px, px, false, Image.Format.Rgb8);
        int cell = px / n;
        for (int y = 0; y < px; y++)
            for (int x = 0; x < px; x++)
            {
                bool dark = ((x / cell) + (y / cell)) % 2 == 0;
                img.SetPixel(x, y, dark ? new Color(0.35f, 0.35f, 0.35f) : new Color(0.75f, 0.75f, 0.75f));
            }
        _checkerTex = ImageTexture.CreateFromImage(img); _checkerTexSize = n;
        return _checkerTex;
    }

    private readonly List<GVec2> _polyBuf = new();
    private readonly GVec2[] _triBuf = new GVec2[3];

    /// <summary>모든 연속 변의 외적 부호가 같으면 볼록(삼각분할 없이 바로 그린다).</summary>
    private static bool IsConvex(GVec2[] p)
    {
        int n = p.Length; bool pos = false, neg = false;
        for (int i = 0; i < n; i++)
        {
            var a = p[i]; var b = p[(i + 1) % n]; var c = p[(i + 2) % n];
            float cr = (b.X - a.X) * (c.Y - b.Y) - (b.Y - a.Y) * (c.X - b.X);
            if (cr > 1e-6f) pos = true; else if (cr < -1e-6f) neg = true;
            if (pos && neg) return false;
        }
        return true;
    }

    public override void _Draw()
    {
        float s = CubeApp.Instance.UiScale;
        DrawRect(new Rect2(GVec2.Zero, Size), MathConvert.Rgb(0x2b2b2b));
        var p0 = UvToPx(new NVec2(0, 1)); var p1 = UvToPx(new NVec2(1, 0));
        var unit = new Rect2(p0, p1 - p0);
        float imgAlpha = _dim ? 0.28f : 1f;
        int tileMin = _tiles ? -1 : 0, tileMax = _tiles ? 2 : 0;
        for (int tx = tileMin; tx <= tileMax; tx++)
            for (int ty = tileMin; ty <= tileMax; ty++)
            {
                var r = new Rect2(UvToPx(new NVec2(tx, ty + 1)), unit.Size);
                bool home = tx == 0 && ty == 0;
                switch (_background)
                {
                    case UvBackground.UvTexture: if (_gridTex != null) DrawTextureRect(_gridTex, r, false, new Color(1, 1, 1, home ? imgAlpha : imgAlpha * 0.5f)); else DrawRect(r, MathConvert.Rgb(0x3a3a3a)); break;
                    case UvBackground.Checker: { var ct = CheckerTexture(); if (ct != null) DrawTextureRect(ct, r, false, new Color(1, 1, 1, home ? imgAlpha + 0.2f : imgAlpha * 0.5f)); break; }
                    case UvBackground.Mapped: { var tex = MappedTexture(); if (tex != null) DrawTextureRect(tex, r, false, new Color(1, 1, 1, home ? (_dim ? 0.7f : 1f) : 0.35f)); else DrawRect(r, MathConvert.Rgb(0x3a3a3a)); break; }
                    case UvBackground.Grid: DrawRect(r, MathConvert.Rgb(0x333333)); break;
                }
                if (_gridLines && (_background == UvBackground.Grid || _background == UvBackground.None))
                    for (int i = 0; i <= 10; i++)
                    {
                        float t = i / 10f; var col = i % 5 == 0 ? MathConvert.Rgb(0x6a6a6a) : MathConvert.Rgb(0x4a4a4a);
                        DrawLine(UvToPx(new NVec2(tx + t, ty)), UvToPx(new NVec2(tx + t, ty + 1)), col, 1 * s);
                        DrawLine(UvToPx(new NVec2(tx, ty + t)), UvToPx(new NVec2(tx + 1, ty + t)), col, 1 * s);
                    }
                DrawRect(r, home ? MathConvert.Rgb(0x9a9a9a) : MathConvert.Rgb(0x555555), false, 1 * s);
                if (_tiles && tx >= 0 && ty >= 0) DrawString(GetThemeDefaultFont(), r.Position + new GVec2(4 * s, 14 * s), (1001 + tx + ty * 10).ToString(), HorizontalAlignment.Left, -1, (int)(11 * s), MayaTheme.TextDim);
            }

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
            float[]? ratio = null;
            if (_distortion)
            {
                if (!_distortionCache.TryGetValue(node.Id, out var dc) || dc.version != m.GeometryVersion) { dc = (m.GeometryVersion, UvOps.DistortionPerFace(m)); _distortionCache[node.Id] = dc; }
                ratio = dc.ratio;
            }
            // 면 틴트
            for (int f = 0; f < m.FaceCount; f++)
            {
                if (!m.Faces[f].Alive || !FaceVisible(node.Id, f)) continue;
                bool fsel = sel.Mode == SelectMode.Face && IsFaceSelected(node, f);
                bool fhov = sel.Mode == SelectMode.Face && _hover is { } hf && hf.Node == node.Id && hf.Component == f;
                _polyBuf.Clear();
                int start = m.Faces[f].HalfEdge, he = start;
                do { _polyBuf.Add(UvToPx(m.Hes[he].Uv0)); he = m.Hes[he].Next; } while (he != start);
                if (_polyBuf.Count < 3) continue;
                var arr = _polyBuf.ToArray();
                if (MathF.Abs(PolygonArea(arr)) < 0.5f) continue;
                Color col;
                if (fsel) col = new Color(1f, 0.55f, 0f, 0.35f);
                else if (fhov) col = new Color(1f, 1f, 1f, 0.18f);
                else if (ratio != null) { float rr = ratio[f]; col = rr < 1 ? new Color(1f, 0.3f, 0.3f, Math.Clamp((1 - rr) * 1.5f, 0.05f, 0.6f)) : new Color(0.3f, 0.5f, 1f, Math.Clamp((rr - 1) * 1.5f, 0.05f, 0.6f)); }
                else if (_shaded) col = UvOps.FaceUvSignedArea(m, f) >= 0 ? new Color(0.35f, 0.55f, 1f, 0.25f) : new Color(1f, 0.35f, 0.35f, 0.25f);
                else col = new Color(0.6f, 0.75f, 1f, 0.08f);
                if (IsConvex(arr)) { DrawColoredPolygon(arr, col); continue; }
                var tris = Geometry2D.TriangulatePolygon(arr);
                for (int t = 0; t + 2 < tris.Length; t += 3) { _triBuf[0] = arr[tris[t]]; _triBuf[1] = arr[tris[t + 1]]; _triBuf[2] = arr[tris[t + 2]]; DrawColoredPolygon(_triBuf, col); }
            }
            // 엣지
            for (int e = 0; e < m.EdgeCount; e++)
            {
                var ed = m.Edges[e];
                if (!ed.Alive) continue;
                for (int k = 0; k < 2; k++)
                {
                    int he = k == 0 ? ed.He0 : ed.He1;
                    if (he < 0 || !FaceVisible(node.Id, m.Hes[he].Face)) continue;
                    var a = UvToPx(m.Hes[he].Uv0); var b = UvToPx(m.Hes[m.Hes[he].Next].Uv0);
                    bool esel = sel.Mode == SelectMode.Edge && IsEdgeSelected(node, e);
                    bool ehov = sel.Mode == SelectMode.Edge && _hover is { } hv && hv.Node == node.Id && hv.Component == e;
                    bool border = ed.Seam || ed.He1 < 0;
                    var col = ehov ? MeshView.Hover : esel ? MeshView.EdgeSelected : border && _texBorders ? MathConvert.Rgb(0xffe034) : MathConvert.Rgb(0xdddddd);
                    DrawLine(a, b, col, (esel || ehov ? 2.5f : border && _texBorders ? 2.5f : 1f) * s, true);
                }
            }
            // UV 점(파랑, 선택 빨강, 호버 흰색, 핀 = 진파랑 테두리)
            for (int i = 0; i < topo.Points.Count; i++)
            {
                var pt = topo.Points[i];
                if (_isolate != null && !pt.HalfEdges.Any(h => FaceVisible(node.Id, m.Hes[h].Face))) continue;
                var p = UvToPx(pt.Uv);
                bool ps = selPts.Contains(i);
                bool ph = sel.Mode == SelectMode.Uv && _hover is { } hp && hp.Node == node.Id && hp.Component >= 0 && hp.Component < topo.Points.Count
                          && (IslandMode ? topo.Points[hp.Component].Shell == pt.Shell : hp.Component == i);
                float r = (ps || ph ? 3.5f : 2.5f) * s;
                if (pt.Pinned) DrawRect(new Rect2(p - new GVec2(r + 2 * s, r + 2 * s), new GVec2(2 * r + 4 * s, 2 * r + 4 * s)), MathConvert.Rgb(0x2255ff));
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
        if (_tool is UvCanvasTool.Grab or UvCanvasTool.Smooth or UvCanvasTool.Pinch or UvCanvasTool.Smear or UvCanvasTool.PinBrush && _brushPos is { } bp)
        {
            float r = BrushRadius;
            DrawArc(bp, r, 0, Mathf.Tau, 48, new Color(1f, 0.4f, 0.4f, 0.9f), 1.5f * s, true);
        }
        string modeText = IslandMode && sel.Mode == SelectMode.Uv ? "Island" : sel.Mode switch { SelectMode.Uv => "UV", SelectMode.Edge => "Edge", SelectMode.Face => "Face", SelectMode.Vertex => "Vertex", _ => "Object" };
        string toolText = _tool == UvCanvasTool.None ? _shell.Tools.Current?.Label ?? "" : _tool.ToString();
        string setText = "";
        var first = targets[0].Mesh!; if (first.UvSets.Count > 1) setText = $"   set: {first.UvSets[Math.Clamp(first.CurrentUvSet, 0, first.UvSets.Count - 1)].Name}";
        DrawString(font, new GVec2(12 * s, Size.Y - 10 * s), $"{modeText} mode   tool: {toolText}   zoom {(_zoom / 400f):P0}{setText}{(_isolate != null ? "   [isolate]" : "")}{(_pixelSnap ? "   [pixel snap]" : "")}", HorizontalAlignment.Left, -1, fs, MayaTheme.TextDim);
        if (_showStats)
        {
            int shells = 0, overlap = 0, reversed = 0; float usage = 0;
            foreach (var node in targets)
            {
                var m = node.Mesh!;
                if (!_statsCache.TryGetValue(node.Id, out var sc) || sc.version != m.GeometryVersion) { sc = (m.GeometryVersion, UvOps.Statistics(m, Topo(node))); _statsCache[node.Id] = sc; }
                shells += sc.stats.shells; overlap += sc.stats.overlapping; reversed += sc.stats.reversed; usage += sc.stats.usage;
            }
            DrawString(font, new GVec2(12 * s, 20 * s), $"UV shells: {shells}   overlapping faces: {overlap}   reversed faces: {reversed}   0-1 usage: {usage:P0}", HorizontalAlignment.Left, -1, fs, MayaTheme.Text);
        }
    }

    private static readonly Color AxisX = MathConvert.Rgb(0xff2a2a), AxisY = MathConvert.Rgb(0x5aff2a), AxisC = MathConvert.Rgb(0x6ad0ff), Active = MathConvert.Rgb(0xffff00);

    private void DrawGizmo(float s)
    {
        if (!GizmoActive) { _hasPivot = false; return; }
        if (!_dragging) _hasPivot = ComputePivot(out _pivotUv);
        if (!_hasPivot) return;
        var c = UvToPx(_pivotUv);
        float len = GizmoLen;
        string tool = _shell.Tools.Current!.Id;
        Color Col(Part p, Color normal) => (_dragging ? _dragPart : _hoverPart) == p ? Active : normal;
        if (tool == "rotate") { DrawArc(c, len, 0, Mathf.Tau, 64, Col(Part.Ring, AxisC), 2 * s, true); DrawCircle(c, 3 * s, Col(Part.Ring, AxisC)); return; }
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

    private Texture2D? MappedTexture()
    {
        foreach (var node in TargetNodes()) { var mv = _shell.Viewport.Scene.GetMeshView(node.Id); if (mv?.MappedTexture != null) return mv.MappedTexture; }
        return null;
    }

    /// <summary>Pixel Snap 기준 해상도(매핑 텍스처 크기, 없으면 체커 512).</summary>
    private int ImagePixels() { var t = MappedTexture(); return t != null ? Math.Max(t.GetWidth(), 1) : 512; }

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

    private float BrushRadius => _shell.BrushOptions.Float("radius") * CubeApp.Instance.UiScale;
    private float BrushStrength => _shell.BrushOptions.Float("strength");
    private bool IsBrushTool => _tool is UvCanvasTool.Grab or UvCanvasTool.Smooth or UvCanvasTool.Pinch or UvCanvasTool.Smear or UvCanvasTool.PinBrush;

    public override void _GuiInput(InputEvent e)
    {
        float s = CubeApp.Instance.UiScale;
        switch (e)
        {
            case InputEventMouseButton mb:
                if (mb.Pressed) GrabFocus();
                if (HandlePie(mb)) { AcceptEvent(); return; }
                if (mb.Pressed && mb.CtrlPressed && IsBrushTool && mb.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
                {
                    var o = _shell.BrushOptions; o.Set("radius", Math.Clamp(o.Float("radius") * (mb.ButtonIndex == MouseButton.WheelUp ? 1.15f : 1 / 1.15f), 5f, 500f)); QueueRedraw(); AcceptEvent(); return;
                }
                if (mb.Pressed && mb.ButtonIndex == MouseButton.WheelUp) { ZoomAt(mb.Position, 1.1f); AcceptEvent(); return; }
                if (mb.Pressed && mb.ButtonIndex == MouseButton.WheelDown) { ZoomAt(mb.Position, 1 / 1.1f); AcceptEvent(); return; }
                if (mb.Pressed && mb.AltPressed && mb.ButtonIndex is MouseButton.Middle or MouseButton.Right or MouseButton.Left && _navButton == MouseButton.None)
                { _navButton = mb.ButtonIndex; _last = mb.Position; AcceptEvent(); return; }
                if (!mb.Pressed && mb.ButtonIndex == _navButton) { _navButton = MouseButton.None; AcceptEvent(); return; }
                if (mb.ButtonIndex == MouseButton.Left)
                {
                    if (mb.Pressed)
                    {
                        if (_tool != UvCanvasTool.None && BeginToolPress(mb)) { AcceptEvent(); return; }
                        var part = HitGizmo(mb.Position);
                        if (part != Part.None && TryBeginTransform(mb.Position, part)) { AcceptEvent(); return; }
                        _pressed = true; _marquee = false; _pressPos = mb.Position; _modifier = ModifierOf(mb);
                        AcceptEvent(); return;
                    }
                    if (_cutSewPainting) { _cutSewPainting = false; AcceptEvent(); return; }
                    if (_brushing) { EndBrush(); AcceptEvent(); return; }
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
                if (IsBrushTool) { _brushPos = mm.Position; QueueRedraw(); }
                if (_cutSewPainting) { CutSewAt(mm.Position); AcceptEvent(); return; }
                if (_brushing) { ApplyBrush(mm.Position); AcceptEvent(); return; }
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
                if (k.Keycode == Key.Escape && _tool != UvCanvasTool.None) { Tool = UvCanvasTool.None; AcceptEvent(); return; }
                break;
        }
    }

    public override void _Notification(int what)
    {
        if (what == NotificationMouseExit) { if (_hover != null || _hoverPart != Part.None || _brushPos != null) { _hover = null; _hoverPart = Part.None; _brushPos = null; QueueRedraw(); } }
    }

    private void UpdateHover(GVec2 px)
    {
        var part = HitGizmo(px);
        var hit = part == Part.None ? Pick(px) : null;
        if (part != _hoverPart || !Nullable.Equals(hit, _hover)) { _hoverPart = part; _hover = hit; QueueRedraw(); }
    }

    public override void _UnhandledKeyInput(InputEvent e)
    {
        if (e is not InputEventKey) return;
        _shell.Hotkeys._Input(e);
        if (GetViewport().IsInputHandled() || _shell.GetViewport().IsInputHandled()) GetViewport().SetInputAsHandled();
    }

    /// <summary>RMB 홀드 파이: 기본 = 모드, Shift = Edit(UV 기능; 서브 파이는 버튼을 뗀 뒤 LMB로 선택), Ctrl = Select(변환).</summary>
    private bool HandlePie(InputEventMouseButton mb)
    {
        if (mb.ButtonIndex == MouseButton.Right)
        {
            if (mb.Pressed)
            {
                if (_pie.IsOpen && _pie.Sticky) { _pie.Close(); QueueRedraw(); return true; }
                if (mb.AltPressed || _pie.IsOpen || _dragging) return _pie.IsOpen;
                var items = mb.CtrlPressed ? PieMenus.UvSelectMenu(_shell) : mb.ShiftPressed ? PieMenus.UvMenu(_shell) : PieMenus.UvModeMenu(_shell, IslandMode);
                _pie.Open(items, mb.Position);
                return _pie.IsOpen;
            }
            if (_pie.IsOpen)
            {
                if (_pie.Sticky) return true;
                ExecutePie(_pie.Release());
                return true;
            }
            return false;
        }
        if (_pie.IsOpen && _pie.Sticky && mb.ButtonIndex == MouseButton.Left && mb.Pressed) { ExecutePie(_pie.Release()); return true; }
        return _pie.IsOpen;
    }

    private void ExecutePie(PieItem? chosen)
    {
        if (chosen == null || !chosen.Enabled) { QueueRedraw(); return; }
        if (chosen.Sub != null) { _pie.Open(chosen.Sub(), _pie.Center, sticky: true, title: chosen.Label); return; }
        if (chosen.Run != null) chosen.Run(); else _shell.Actions.Invoke(chosen.ActionId);
        QueueRedraw();
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

    private SelItem? PickIsland(GVec2 px, SceneNode node, ref float best)
    {
        float s = CubeApp.Instance.UiScale;
        var m = node.Mesh!; var topo = Topo(node);
        SelItem? hit = null;
        for (int i = 0; i < topo.Points.Count; i++) { float d = UvToPx(topo.Points[i].Uv).DistanceTo(px); if (d <= 8 * s && d < best) { best = d; hit = new SelItem(node.Id, i); } }
        if (hit != null) return hit;
        for (int e = 0; e < m.EdgeCount; e++)
        {
            var ed = m.Edges[e]; if (!ed.Alive) continue;
            for (int k = 0; k < 2; k++)
            {
                int he = k == 0 ? ed.He0 : ed.He1;
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

    /// <summary>커서 아래 UV 점(모드 무관, 8px).</summary>
    private (SceneNode node, int point)? PickPointAny(GVec2 px)
    {
        float s = CubeApp.Instance.UiScale; float best = 8 * s; (SceneNode, int)? hit = null;
        foreach (var node in TargetNodes())
        {
            var topo = Topo(node);
            for (int i = 0; i < topo.Points.Count; i++) { float d = UvToPx(topo.Points[i].Uv).DistanceTo(px); if (d < best) { best = d; hit = (node, i); } }
        }
        return hit;
    }

    private (SceneNode node, int edge)? PickEdgeAny(GVec2 px)
    {
        float s = CubeApp.Instance.UiScale; float best = 6 * s; (SceneNode, int)? hit = null;
        foreach (var node in TargetNodes())
        {
            var m = node.Mesh!;
            for (int e = 0; e < m.EdgeCount; e++)
            {
                var ed = m.Edges[e]; if (!ed.Alive) continue;
                for (int k = 0; k < 2; k++)
                {
                    int he = k == 0 ? ed.He0 : ed.He1;
                    if (he < 0) continue;
                    float d = Geometry2D.GetClosestPointToSegment(px, UvToPx(m.Hes[he].Uv0), UvToPx(m.Hes[m.Hes[he].Next].Uv0)).DistanceTo(px);
                    if (d < best) { best = d; hit = (node, e); }
                }
            }
        }
        return hit;
    }

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
                    for (int i = 0; i < topo.Points.Count; i++) { float d = UvToPx(topo.Points[i].Uv).DistanceTo(px); if (d <= 8 * s && d < best) { best = d; hit = new SelItem(node.Id, i); } }
                    break;
                case SelectMode.Edge:
                    for (int e = 0; e < m.EdgeCount; e++)
                    {
                        var ed = m.Edges[e]; if (!ed.Alive) continue;
                        for (int k = 0; k < 2; k++)
                        {
                            int he = k == 0 ? ed.He0 : ed.He1;
                            if (he < 0) continue;
                            float d = Geometry2D.GetClosestPointToSegment(px, UvToPx(m.Hes[he].Uv0), UvToPx(m.Hes[m.Hes[he].Next].Uv0)).DistanceTo(px);
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
        if (sel.Mode == SelectMode.Object && items.Length == 0) return;
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
                            for (int f = 0; f < m.FaceCount; f++)
                            {
                                if (!m.Faces[f].Alive) continue;
                                var poly = FacePoly(m, f, UvToPx);
                                var c = GVec2.Zero; foreach (var p in poly) c += p; c /= poly.Count;
                                bool inside = r.HasPoint(c);
                                for (int i = 0; i < poly.Count && !inside; i++) if (RectIntersectsSegment(r, poly[i], poly[(i + 1) % poly.Count])) inside = true;
                                if (inside) shells.Add(topo.Points[topo.HeToPoint[m.Faces[f].HalfEdge]].Shell);
                            }
                        foreach (int sh in shells) foreach (int p in topo.PointsInShell(sh)) items.Add(new SelItem(node.Id, p));
                        break;
                    }
                case SelectMode.Edge:
                    for (int e = 0; e < m.EdgeCount; e++)
                    {
                        var ed = m.Edges[e]; if (!ed.Alive) continue;
                        bool inside = false;
                        for (int k = 0; k < 2; k++) { int he = k == 0 ? ed.He0 : ed.He1; if (he < 0) continue; if (r.HasPoint(UvToPx(m.Hes[he].Uv0)) || r.HasPoint(UvToPx(m.Hes[m.Hes[he].Next].Uv0))) inside = true; }
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
        if (!CaptureSelectionForTransform(tool switch { "move" => "Move UVs", "rotate" => "Rotate UVs", _ => "Scale UVs" }, null)) return false;
        _xformTool = tool; _pressPos = px; _dragging = true; _dragPart = part;
        return true;
    }

    /// <summary>현재 선택(또는 지정 점)을 변형 대상으로 캡처한다.</summary>
    private bool CaptureSelectionForTransform(string name, Func<SceneNode, HashSet<int>>? pointsOf)
    {
        _xform.Clear();
        int count = 0;
        foreach (var node in TargetNodes())
        {
            var pts = pointsOf?.Invoke(node) ?? SelectedPoints(node);
            if (pts.Count == 0) continue;
            var topo = Topo(node);
            var ids = pts.Where(i => !topo.Points[i].Pinned || _tool == UvCanvasTool.Tweak).ToArray();
            if (ids.Length == 0) continue;
            var init = ids.Select(i => topo.Points[i].Uv).ToArray();
            count += ids.Length;
            var cmd = new UvEditCommand(name, node.Id);
            cmd.Capture(_shell.Document);
            _xform.Add((node.Id, topo, ids, init, cmd));
        }
        return count > 0;
    }

    private NVec2 SnapDelta(NVec2 delta)
    {
        if (!_pixelSnap) return delta;
        float px = 1f / ImagePixels();
        return new NVec2(MathF.Round(delta.X / px) * px, MathF.Round(delta.Y / px) * px);
    }

    private void UpdateTransform(GVec2 px)
    {
        var d = px - _pressPos;
        var c = UvToPx(_pivotUv);
        Matrix3x2 xf;
        switch (_xformTool)
        {
            case "move":
            case "tweak":
            case "shell":
                {
                    var delta = new NVec2(d.X / _zoom, -d.Y / _zoom);
                    if (_dragPart == Part.X) delta.Y = 0; else if (_dragPart == Part.Y) delta.X = 0;
                    if (_shell.Viewport.IsGridSnapHeld) { float g = 1f / 8f; delta = new NVec2(MathF.Round(delta.X / g) * g, MathF.Round(delta.Y / g) * g); }
                    xf = Matrix3x2.CreateTranslation(SnapDelta(delta)); break;
                }
            case "rotate":
                {
                    float a0 = MathF.Atan2(_pressPos.Y - c.Y, _pressPos.X - c.X), a1 = MathF.Atan2(px.Y - c.Y, px.X - c.X);
                    float angle = -(a1 - a0);
                    if (_shell.Viewport.IsSnapHeld) { float step = MathF.Max(CubeApp.Instance.Settings.RotateSnapDegrees, 0.1f) * MathF.PI / 180f; angle = MathF.Round(angle / step) * step; }
                    xf = Matrix3x2.CreateRotation(angle, _pivotUv); break;
                }
            default:
                {
                    float len = GizmoLen; float fx = 1f, fy = 1f;
                    if (_dragPart == Part.X) fx = MathF.Max((len + d.X) / len, 0.01f);
                    else if (_dragPart == Part.Y) fy = MathF.Max((len - d.Y) / len, 0.01f);
                    else { float f = MathF.Max(1f + (d.X - d.Y) * 0.005f, 0.01f); fx = fy = f; }
                    if (_shell.Viewport.IsSnapHeld) { float st = MathF.Max(CubeApp.Instance.Settings.ScaleSnapStep, 0.001f); fx = MathF.Max(MathF.Round(fx / st) * st, st); fy = MathF.Max(MathF.Round(fy / st) * st, st); }
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
            foreach (var (id, topo, ids, init, cmd) in _xform)
            {
                var mesh = doc.Get(id).Mesh!;
                if (!commit) { for (int i = 0; i < ids.Length; i++) UvOps.SetPointUv(mesh, topo, ids[i], init[i]); doc.Notify(new DocChange(ChangeKind.MeshAttributes, id)); continue; }
                cmd.Commit(doc);
                if (cmd.Changed) doc.Undo.Push(cmd, alreadyApplied: true);
            }
        _xform.Clear();
        QueueRedraw();
    }

    // ---------------------------------------------------------------- 툴(Tweak / 브러시 / Cut-Sew / Move Shell)

    private bool BeginToolPress(InputEventMouseButton mb)
    {
        var px = mb.Position;
        switch (_tool)
        {
            case UvCanvasTool.Tweak:
                {
                    var hit = PickPointAny(px); if (hit == null) return false;
                    var (node, point) = hit.Value;
                    bool inSel = SelectedPoints(node).Contains(point);
                    if (!CaptureSelectionForTransform("Tweak UVs", n => inSel ? SelectedPoints(n) : n.Id == node.Id ? new HashSet<int> { point } : new HashSet<int>())) return false;
                    _xformTool = "tweak"; _pressPos = px; _dragging = true; _dragPart = Part.Center; _pivotUv = Topo(node).Points[point].Uv;
                    return true;
                }
            case UvCanvasTool.MoveShell:
                {
                    var hit = PickPointAny(px) ?? PickFacePoint(px); if (hit == null) return false;
                    var (node, point) = hit.Value;
                    int shell = Topo(node).Points[point].Shell;
                    if (!CaptureSelectionForTransform("Move UV Shell", n => n.Id == node.Id ? new HashSet<int>(Topo(n).PointsInShell(shell)) : new HashSet<int>())) return false;
                    _xformTool = "shell"; _pressPos = px; _dragging = true; _dragPart = Part.Center; _pivotUv = Topo(node).Points[point].Uv;
                    return true;
                }
            case UvCanvasTool.CutSew:
                _cutSewPainting = true; _cutSewSew = mb.CtrlPressed; _cutSewDone.Clear();
                CutSewAt(px);
                return true;
            default:
                {
                    // 브러시: 대상 노드 전체를 캡처하고 드래그마다 반지름 안의 점을 움직인다
                    string name = _tool switch { UvCanvasTool.Grab => "Grab UVs", UvCanvasTool.Smooth => "Smooth UVs", UvCanvasTool.Pinch => "Pinch UVs", UvCanvasTool.Smear => "Smear UVs", _ => "Pin UVs" };
                    bool pinBrush = _tool == UvCanvasTool.PinBrush;
                    _xform.Clear();
                    foreach (var node in TargetNodes())
                    {
                        var topo = Topo(node);
                        var ids = Enumerable.Range(0, topo.Points.Count).Where(i => pinBrush || !topo.Points[i].Pinned).ToArray();
                        var cmd = new UvEditCommand(name, node.Id); cmd.Capture(_shell.Document);
                        _xform.Add((node.Id, topo, ids, ids.Select(i => topo.Points[i].Uv).ToArray(), cmd));
                    }
                    if (_xform.Count == 0) return false;
                    _brushing = true; _dragging = true; _brushLast = px; _brushUnpin = mb.CtrlPressed;
                    ApplyBrush(px);
                    return true;
                }
        }
    }

    private bool _brushUnpin;

    private (SceneNode node, int point)? PickFacePoint(GVec2 px)
    {
        foreach (var node in TargetNodes())
        {
            var m = node.Mesh!; var topo = Topo(node);
            for (int f = 0; f < m.FaceCount; f++)
                if (m.Faces[f].Alive && Geometry2D.IsPointInPolygon(px, FacePoly(m, f, UvToPx).ToArray())) return (node, topo.HeToPoint[m.Faces[f].HalfEdge]);
        }
        return null;
    }

    private void ApplyBrush(GVec2 px)
    {
        float r = BrushRadius; float strength = BrushStrength;
        var deltaUv = new NVec2((px.X - _brushLast.X) / _zoom, -(px.Y - _brushLast.Y) / _zoom);
        _brushLast = px;
        var centerUv = PxToUv(px);
        foreach (var (id, topo, ids, _, _) in _xform)
        {
            var mesh = _shell.Document.Get(id).Mesh!;
            bool any = false;
            foreach (int i in ids)
            {
                var uv = topo.Points[i].Uv;
                float d = UvToPx(uv).DistanceTo(px);
                if (d > r) continue;
                float w = 1f - d / r; w = w * w * (3 - 2 * w);
                switch (_tool)
                {
                    case UvCanvasTool.Grab: UvOps.SetPointUv(mesh, topo, i, uv + deltaUv * w); any = true; break;
                    case UvCanvasTool.Smear: UvOps.SetPointUv(mesh, topo, i, uv + deltaUv * w * strength); any = true; break;
                    case UvCanvasTool.Pinch: UvOps.SetPointUv(mesh, topo, i, uv + (centerUv - uv) * w * strength * 0.1f); any = true; break;
                    case UvCanvasTool.Smooth:
                        {
                            // 이웃 UV 점 평균(같은 셸, 엣지로 이어진 코너)
                            var sum = NVec2.Zero; int n = 0;
                            foreach (int he in topo.Points[i].HalfEdges)
                            {
                                int nxt = topo.HeToPoint[mesh.Hes[he].Next]; int prv = topo.HeToPoint[mesh.Hes[he].Prev];
                                sum += topo.Points[nxt].Uv + topo.Points[prv].Uv; n += 2;
                            }
                            if (n > 0) { UvOps.SetPointUv(mesh, topo, i, NVec2.Lerp(uv, sum / n, w * strength * 0.5f)); any = true; }
                            break;
                        }
                    case UvCanvasTool.PinBrush:
                        foreach (int he in topo.Points[i].HalfEdges) { var h = mesh.Hes[he]; h.PinUv = !_brushUnpin; mesh.Hes[he] = h; }
                        topo.Points[i].Pinned = !_brushUnpin; any = true;
                        break;
                }
            }
            if (any) _shell.Document.Notify(new DocChange(ChangeKind.MeshAttributes, id));
        }
        QueueRedraw();
    }

    private void EndBrush()
    {
        _brushing = false; _dragging = false;
        var doc = _shell.Document;
        using (doc.Undo.BeginGroup(_xform.Count > 0 ? _xform[0].cmd.Name : "UV"))
            foreach (var (_, _, _, _, cmd) in _xform) { cmd.Commit(doc); if (cmd.Changed) doc.Undo.Push(cmd, alreadyApplied: true); }
        _xform.Clear();
        _topos.Clear();
        QueueRedraw();
    }

    private void CutSewAt(GVec2 px)
    {
        var hit = PickEdgeAny(px); if (hit == null) return;
        var (node, edge) = hit.Value;
        if (!_cutSewDone.Add((node.Id, edge))) return;
        var m = node.Mesh!;
        if (m.Edges[edge].He1 < 0 || m.Edges[edge].Seam == !_cutSewSew) return;
        bool sew = _cutSewSew; int e = edge;
        _shell.Document.Undo.Push(new UvEditCommand(sew ? "Sew UV Edge" : "Cut UV Edge", node.Id, mm => { if (sew) UvOps.SewEdges(mm, new[] { e }); else UvOps.CutEdges(mm, new[] { e }); }));
        _topos.Remove(node.Id);
        QueueRedraw();
    }

    // ---------------------------------------------------------------- 스냅샷

    /// <summary>현재 캔버스 영역을 PNG로 저장한다(UV Snapshot).</summary>
    public Error SaveSnapshot(string path)
    {
        var img = GetViewport().GetTexture().GetImage();
        var rect = GetGlobalRect();
        var region = img.GetRegion(new Rect2I((int)rect.Position.X, (int)rect.Position.Y, (int)rect.Size.X, (int)rect.Size.Y));
        return region.SavePng(path);
    }
}
