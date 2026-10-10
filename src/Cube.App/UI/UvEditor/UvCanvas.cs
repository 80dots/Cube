using System.Numerics;
using System.Runtime.InteropServices;
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
/// UV 캔버스 배경 종류: 없음 / 0..1 그리드만 / UV 그리드 텍스처 / 머티리얼에 매핑된 텍스처(Mapped) / 체커 맵.
/// 값 순서는 UvEditorWindow의 배경 드롭다운 항목 순서와 같다(CycleBackground가 mod 5로 순환).
/// </summary>
public enum UvBackground { None, Grid, UvTexture, Mapped, Checker }

/// <summary>UV 편집기 캔버스 툴(Maya UV Editor Tools 메뉴). None = 선택/조작기.</summary>
// 항목: None = 선택/조작기, Tweak = 점 직접 끌기, Grab/Smooth/Pinch/Smear = 브러시 변형, PinBrush = 핀 칠하기, CutSew = 엣지 자르기/꿰매기, MoveShell = 셸 단위 이동.
public enum UvCanvasTool { None, Tweak, Grab, Smooth, Pinch, Smear, PinBrush, CutSew, MoveShell }

/// <summary>
/// UV 편집 캔버스. 선택된 오브젝트들의 UV를 0..1 그리드 위에 그리고, 뷰포트와 같은 선택 UI(클릭/마키/Shift·Ctrl 수식어/호버)와
/// 2D 조작기(W 이동 / E 회전 / R 스케일), 브러시·Tweak·Cut/Sew·Move Shell 툴을 제공한다. Island 모드는 심으로 분리된 UV 섬 단위 선택.
/// 표시 옵션: Shaded(앞/뒤 색), Distortion, Texture Borders, Isolate Select, Grid, UDIM 타일, 이미지 Dim/Unfiltered/Pixel Snap, Checker 배경, Pin(파랑), 통계 HUD.
/// 파이 메뉴 정책은 뷰포트와 같다: RMB = 모드, Shift+RMB = Edit(UV 기능, 서브 파이), Ctrl+RMB = Select(변환).
/// 내비게이션: Alt+MMB 팬, Alt+RMB·휠 줌, F 프레임, A 전체.
/// 좌표계: UV(0..1, V 위쪽이 +)를 캔버스 px로 <c>px = _origin + (u, -v) × _zoom</c>으로 매핑한다(<see cref="UvToPx"/>/<see cref="PxToUv"/>).
/// 그리기: 정적인 기하(면 틴트·와이어·심·점)는 <c>UvCanvasLayer</c> 레이어에 엔진 메시로 캐시하고, 이 컨트롤의 _Draw는 오버레이(조작기·호버·마키·HUD)만 그린다.
/// UV 위상(<see cref="UvTopology"/>)은 노드별로 캐시하며 위상/속성 변경 때만 다시 만든다. 편집은 모두 <see cref="UvEditCommand"/>로 Undo 가능.
/// </summary>
public partial class UvCanvas : Control
{
    /// <summary>소유 셸(문서, 툴, 액션, 헬프 라인, 파이 메뉴 정의 접근).</summary>
    private Shell _shell = null!;
    /// <summary>현재 배경 종류(백킹 필드).</summary>
    private UvBackground _background = UvBackground.UvTexture;
    /// <summary>배경 종류. 바꾸면 다시 그린다.</summary>
    public UvBackground Background { get => _background; set { _background = value; QueueRedraw(); } }
    /// <summary>Island 모드(UV 모드의 변형): 켜져 있으면 클릭/마키로 집은 요소가 속한 UV 섬 전체를 선택한다.</summary>
    public bool IslandMode { get; private set; }
    /// <summary>Island 모드가 바뀔 때 발생(툴바 모드 버튼 갱신용).</summary>
    public event Action? IslandModeChanged;
    /// <summary>캔버스 전용 파이 메뉴(RMB 모드 / Shift+RMB 편집 / Ctrl+RMB 선택).</summary>
    private PieMenu _pie = null!;
    /// <summary>줌: UV 1단위당 px.</summary>
    private float _zoom = 400f;
    /// <summary>UV (0,0)의 캔버스 px 위치(팬 오프셋). Zero면 아직 프레임되지 않은 상태로 본다.</summary>
    private GVec2 _origin;
    /// <summary>UV 그리드 배경 텍스처(내장 PNG 바이트에서 로드).</summary>
    private Texture2D? _gridTex;
    /// <summary>체커 배경 텍스처 캐시와 그 칸 수(CheckerSize가 바뀌면 다시 만든다).</summary>
    private ImageTexture? _checkerTex; private int _checkerTexSize = -1;

    // 표시 옵션
    // 백킹 필드: Shaded/Distortion/TextureBorders는 기하 레이어 색이 바뀌므로 MarkGeomDirty, 나머지는 오버레이만 다시 그린다.
    private bool _shaded, _distortion, _texBorders = true, _showStats, _gridLines = true, _tiles, _dim = true, _unfiltered, _pixelSnap;
    /// <summary>Shaded: 면을 앞면 파랑/뒷면(UV가 뒤집힌 면) 빨강으로 칠한다.</summary>
    public bool Shaded { get => _shaded; set { _shaded = value; MarkGeomDirty(); } }
    /// <summary>Distortion: 면을 UV 늘어남/줄어듦 비율 색으로 칠한다.</summary>
    public bool Distortion { get => _distortion; set { _distortion = value; MarkGeomDirty(); } }
    /// <summary>Texture Borders: UV 셸 경계 엣지를 굵게 표시한다(기본 켜짐).</summary>
    public bool TextureBorders { get => _texBorders; set { _texBorders = value; MarkGeomDirty(); } }
    /// <summary>통계 HUD(셸 수, 겹침, 뒤집힘, 사용률) 표시.</summary>
    public bool ShowStats { get => _showStats; set { _showStats = value; QueueRedraw(); } }
    /// <summary>그리드 선 표시(기본 켜짐).</summary>
    public bool ShowGridLines { get => _gridLines; set { _gridLines = value; QueueRedraw(); } }
    /// <summary>UDIM 타일 라벨 표시.</summary>
    public bool ShowTiles { get => _tiles; set { _tiles = value; QueueRedraw(); } }
    /// <summary>배경 이미지를 어둡게(기본 켜짐).</summary>
    public bool DimImage { get => _dim; set { _dim = value; QueueRedraw(); } }
    /// <summary>배경 이미지를 최근접 필터(픽셀 그대로)로 그린다.</summary>
    public bool Unfiltered { get => _unfiltered; set { _unfiltered = value; TextureFilter = value ? TextureFilterEnum.Nearest : TextureFilterEnum.Linear; QueueRedraw(); } }
    /// <summary>Pixel Snap: 조작기 이동 결과를 텍스처 픽셀 격자에 맞춘다.</summary>
    public bool PixelSnap { get => _pixelSnap; set { _pixelSnap = value; QueueRedraw(); } }
    /// <summary>체커 배경의 한 변 칸 수(1~256, 기본 8).</summary>
    public int CheckerSize { get; private set; } = 8;
    /// <summary>체커 칸 수를 설정한다(범위 고정).</summary>
    public void SetCheckerSize(int n) { CheckerSize = Math.Clamp(n, 1, 256); QueueRedraw(); }
    /// <summary>Isolate Select 상태: 노드 → 표시할 면 집합. null = 격리 해제(모든 면 표시).</summary>
    private Dictionary<NodeId, HashSet<int>>? _isolate;
    /// <summary>Isolate Select가 켜져 있는지.</summary>
    public bool Isolated => _isolate != null;
    /// <summary>현재 캔버스 툴(백킹 필드).</summary>
    private UvCanvasTool _tool;
    /// <summary>현재 캔버스 툴. 바꾸면 브러시 커서를 지우고 헬프 라인에 툴 설명을 띄운다.</summary>
    public UvCanvasTool Tool { get => _tool; set { _tool = value; _brushPos = null; QueueRedraw(); _shell.HelpLine.Text = ToolHelp(value); } }

    /// <summary>노드 → UV 위상 캐시(UV 점·셸). 위상/속성 변경·리셋·Invalidate 때 무효화.</summary>
    private readonly Dictionary<NodeId, UvTopology> _topos = new();
    /// <summary>UV 변형 드래그 중(이 동안 위상 캐시를 지우지 않는다 — 점 ID가 유지되어야 함).</summary>
    private bool _dragging;

    // 입력 상태
    // _navButton: 내비게이션 중인 버튼(Alt+MMB 팬, Alt+RMB 줌), _last: 직전 마우스 위치.
    private MouseButton _navButton = MouseButton.None;
    private GVec2 _last;
    // _pressed: LMB 선택 클릭 중, _marquee: 드래그가 임계값을 넘어 마키 선택으로 바뀜.
    private bool _pressed, _marquee;
    // _pressPos: 누른 위치, _modifier: Shift/Ctrl 선택 수식어, _marqueeEnd: 마키 끝점, _hover: 프리셀렉션 대상.
    private GVec2 _pressPos;
    private SelectModifier _modifier;
    private GVec2? _marqueeEnd;
    private SelItem? _hover;

    // 조작기
    // Part: 조작기 부분(X/Y 축, 중앙, 회전 링). _hoverPart = 커서 아래, _dragPart = 끌고 있는 부분. _pivotUv = 조작기 중심(UV), _hasPivot = 선택이 있어 조작기를 그릴지.
    private enum Part { None, X, Y, Center, Ring }
    private Part _hoverPart = Part.None, _dragPart = Part.None;
    private NVec2 _pivotUv;
    private bool _hasPivot;

    // 변형 드래그(조작기/Tweak/브러시/셸 이동 공용)
    // _xform: 노드별 변형 대상(위상, 움직일 UV 점, 시작 UV, 명령). 드래그 동안 시작 UV에서 매번 다시 계산하므로 오차가 쌓이지 않는다.
    // _xformTool: 진행 중인 변형의 이름(Undo 항목·Action Popup용).
    private readonly List<(NodeId node, UvTopology topo, int[] points, NVec2[] initial, UvEditCommand cmd)> _xform = new();
    private string _xformTool = "";
    /// <summary>드래그 시작 때의 UV Symmetry 축선(꺼져 있으면 null). UpdateTransform이 반대쪽 점에 거울 변형을 적용한다.</summary>
    private UvSymmetryPlane? _xformPlane;
    // 브러시 상태: _brushPos = 브러시 원 위치(null = 숨김), _brushLast = 직전 스트로크 위치, _brushing = 스트로크 중,
    // _cutSewPainting = Cut/Sew 칠하기 중, _cutSewSew = Ctrl(꿰매기) 모드.
    private GVec2? _brushPos; private GVec2 _brushLast; private bool _brushing; private bool _cutSewPainting; private bool _cutSewSew;
    // 한 스트로크에서 이미 자르거나 꿰맨 (노드, 엣지) — 같은 엣지를 반복 처리하지 않게.
    private readonly HashSet<(NodeId, int)> _cutSewDone = new();
    /// <summary>CutSewAt이 거울 위치를 처리하는 중(재귀 가드).</summary>
    private bool _cutSewMirroring;

    /// <summary>
    /// 캔버스를 초기화한다: 입력/포커스/크기/클리핑 설정, 그리드 텍스처 로드, 그리기 레이어 생성, 파이 메뉴 추가, 문서·선택·모드·툴 이벤트 구독.
    /// 크기가 바뀔 때 아직 프레임되지 않았거나 도킹 직후 창이면 전체 프레임을 다시 한다.
    /// </summary>
    public void Setup(Shell shell)
    {
        _shell = shell;
        MouseFilter = MouseFilterEnum.Stop;
        FocusMode = FocusModeEnum.All;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;
        ClipContents = true;
        _gridTex = Icons.LoadPng("res://assets/textures/uv_grid.bin");
        SetupLayers();
        _pie = new PieMenu { Name = "UvPie" };
        AddChild(_pie);
        shell.Document.Changed += OnDocChanged;
        shell.Document.Selection.Changed += OnSelectionChanged;
        shell.Document.Selection.ModeChanged += () => { _hover = null; _selPts.Clear(); MarkGeomDirty(); };
        shell.Tools.ToolChanged += _ => QueueRedraw();
        Resized += () => { if (_origin == GVec2.Zero || Time.GetTicksMsec() < _frameUntilMs) CallDeferred(nameof(FrameAll)); };
        CallDeferred(nameof(FrameAll));
    }

    /// <summary>이 시각(엔진 ms)까지는 크기가 바뀔 때마다 FrameAll한다(<see cref="FrameOnResize"/>가 설정).</summary>
    private ulong _frameUntilMs;

    /// <summary>
    /// 도크에 붙거나 떨어진 직후: 앞으로 ms 동안 크기가 바뀔 때마다(레이아웃이 자리 잡는 몇 프레임) 전체를 다시 프레임한다.
    /// 타이머로 0.2초 뒤에 한 번 프레임하던 것(v0.0.46 전)보다 바로 맞고, 도크 폭이 뒤늦게 적용돼도 따라간다.
    /// </summary>
    public void FrameOnResize(int ms = 400) => _frameUntilMs = Time.GetTicksMsec() + (ulong)ms;

    /// <summary>도킹/떼어 내기로 트리를 옮겨도 구독을 유지하고, 실제로 지워질 때만 해제한다.</summary>
    private void Unsubscribe()
    {
        if (_shell != null) { _shell.Document.Changed -= OnDocChanged; _shell.Document.Selection.Changed -= OnSelectionChanged; }
    }

    /// <summary>선택이 바뀌면 선택 UV 점 캐시를 지우고 기하 레이어(선택 색)를 다시 만든다.</summary>
    private void OnSelectionChanged() { _selPts.Clear(); MarkGeomDirty(); }

    /// <summary>
    /// 문서 변경 처리. 그리기 캐시 스탬프는 항상 올리고, 드래그 중이면 위상 캐시는 건드리지 않고 다시 그리기만 한다.
    /// 위상/속성/삭제/리셋이면 그 노드의 위상·통계·왜곡 캐시를 버린다(리셋은 전부 + Isolate 해제).
    /// </summary>
    private void OnDocChanged(DocChange c)
    {
        _geomStamp++;   // UV 드래그 중에도 그리기 캐시는 갱신(위상 캐시는 아래에서 드래그가 끝난 뒤에만)
        if (_dragging) { QueueRedraw(); return; }
        _selPts.Clear();
        if (c.Kind is ChangeKind.MeshTopology or ChangeKind.MeshAttributes or ChangeKind.NodeRemoved or ChangeKind.Reset) { _topos.Remove(c.Node); _statsCache.Remove(c.Node); _distortionCache.Remove(c.Node); }
        if (c.Kind == ChangeKind.Reset) { _topos.Clear(); _isolate = null; }
        QueueRedraw();
    }

    /// <summary>모든 캐시(위상·선택 점·통계·왜곡)를 버리고 기하를 다시 만들게 한다(UV 세트 전환, 창 열기 등 외부 변경 후 호출).</summary>
    public void Invalidate() { _topos.Clear(); _selPts.Clear(); _geomStamp++; _statsCache.Clear(); _distortionCache.Clear(); QueueRedraw(); }

    /// <summary>Island 모드를 켜고 끈다. 켤 때 UV 모드가 아니면 UV 모드 액션을 먼저 실행한다.</summary>
    public void SetIslandMode(bool on)
    {
        IslandMode = on;
        if (on && _shell.Document.Selection.Mode != SelectMode.Uv) _shell.Actions.Invoke("mode.uv");
        IslandModeChanged?.Invoke();
        QueueRedraw();
    }

    /// <summary>툴별 헬프 라인 안내 문구.</summary>
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

    /// <summary>
    /// UV를 보여 줄 노드들: 선택된 오브젝트 + 컴포넌트가 선택된 노드(중복 제거) 중 메시를 가진 것.
    /// </summary>
    public IEnumerable<SceneNode> TargetNodes()
    {
        var sel = _shell.Document.Selection;
        var ids = new HashSet<NodeId>(sel.Objects);
        foreach (var (id, c) in sel.Components) if (!c.IsEmpty) ids.Add(id);
        foreach (var id in ids) { var n = _shell.Document.Find(id); if (n?.Mesh != null) yield return n; }
    }

    /// <summary>노드의 UV 위상(캐시; 없으면 만든다). UV 점 ID는 이 위상의 Points 인덱스이며 뷰포트 UV 모드와 같은 순서다.</summary>
    public UvTopology Topo(SceneNode n)
    {
        if (!_topos.TryGetValue(n.Id, out var t)) { t = UvTopology.Build(n.Mesh!); _topos[n.Id] = t; }
        return t;
    }

    /// <summary>Isolate Select 중이면 격리된 면만 보이고, 아니면 모든 면이 보인다.</summary>
    private bool FaceVisible(NodeId node, int f) => _isolate == null || (_isolate.TryGetValue(node, out var set) && set.Contains(f));

    /// <summary>Isolate Select: 현재 선택(면/UV 점이 속한 면)만 표시 ↔ 해제.</summary>
    public void ToggleIsolate()
    {
        if (_isolate != null) { _isolate = null; MarkGeomDirty(); return; }
        // 노드마다 선택 UV 점을 하나라도 포함하는 면을 모은다.
        var map = new Dictionary<NodeId, HashSet<int>>();
        foreach (var node in TargetNodes())
        {
            var topo = Topo(node); var pts = SelectedPoints(node);
            if (pts.Count == 0) continue;
            map[node.Id] = new HashSet<int>(UvOps.FacesOfPoints(node.Mesh!, topo, pts, all: false));
        }
        _isolate = map.Count > 0 ? map : null;
        MarkGeomDirty();
    }

    // ---------------------------------------------------------------- 좌표

    /// <summary>UV → 캔버스 px(V를 뒤집어 위쪽이 +V).</summary>
    public GVec2 UvToPx(NVec2 uv) => _origin + new GVec2(uv.X, -uv.Y) * _zoom;
    /// <summary>캔버스 px → UV(<see cref="UvToPx"/>의 역변환).</summary>
    public NVec2 PxToUv(GVec2 px) => new((px.X - _origin.X) / _zoom, -(px.Y - _origin.Y) / _zoom);

    /// <summary>0..1 영역과 모든 대상 UV를 포함하도록 프레임한다(캔버스가 너무 작으면 건너뜀).</summary>
    public void FrameAll()
    {
        if (Size.X < 10 || Size.Y < 10) return;
        var min = new NVec2(0, 0); var max = new NVec2(1, 1);
        foreach (var n in TargetNodes()) foreach (var p in Topo(n).Points) { min = NVec2.Min(min, p.Uv); max = NVec2.Max(max, p.Uv); }
        FrameRect(min, max);
    }

    /// <summary>선택 UV 점들의 범위로 프레임한다. 선택이 없으면 전체, 범위가 거의 0이면 ±0.05로 넓힌다.</summary>
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

    /// <summary>UV 사각형 [min, max]가 여백 40px(배율 적용)을 두고 캔버스에 꽉 차도록 줌과 원점을 정한다.</summary>
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

    /// <summary>
    /// 현재 선택을 UV 점 집합으로(읽기 전용으로 쓸 것). 그리기·피벗 계산이 매번 수만 개짜리 집합을 새로 만들지 않도록
    /// 선택 변경/문서 변경/UV 위상 재생성 때까지 캐시한다.
    /// </summary>
    public HashSet<int> SelectedPoints(SceneNode node)
    {
        var topo = Topo(node);
        if (_selPts.TryGetValue(node.Id, out var c) && ReferenceEquals(c.topo, topo)) return c.set;
        var set = _shell.UvPointSelection(node, topo);
        _selPts[node.Id] = (topo, set);
        return set;
    }
    /// <summary>노드 → (캐시를 만든 위상, 선택 UV 점 집합). 위상 객체가 바뀌면 다시 계산한다.</summary>
    private readonly Dictionary<NodeId, (UvTopology topo, HashSet<int> set)> _selPts = new();

    /// <summary>엣지 e가 선택되어 있는지(엣지 모드 선택 기준).</summary>
    private bool IsEdgeSelected(SceneNode node, int e) => _shell.Document.Selection.IsComponentSelected(node.Id, SelectMode.Edge, e);
    /// <summary>면 f가 선택되어 있는지(면 모드 선택 기준).</summary>
    private bool IsFaceSelected(SceneNode node, int f) => _shell.Document.Selection.IsComponentSelected(node.Id, SelectMode.Face, f);

    /// <summary>조작기 피벗 = 모든 대상 노드의 선택 UV 점 평균. 선택이 없으면 false.</summary>
    private bool ComputePivot(out NVec2 pivot)
    {
        var sum = NVec2.Zero; int n = 0;
        foreach (var node in TargetNodes()) { var topo = Topo(node); foreach (int p in SelectedPoints(node)) { sum += topo.Points[p].Uv; n++; } }
        pivot = n > 0 ? sum / n : NVec2.Zero;
        return n > 0;
    }

    /// <summary>조작기를 쓰는 상태인지: 캔버스 툴이 없고 셸의 현재 툴이 Move/Rotate/Scale일 때.</summary>
    private bool GizmoActive => _tool == UvCanvasTool.None && _shell.Tools.Current?.Id is "move" or "rotate" or "scale";
    /// <summary>조작기 축 길이(px).</summary>
    private float GizmoLen => 70f * CubeApp.Instance.UiScale;

    // ---------------------------------------------------------------- 그리기

    /// <summary>노드 → (메시 버전, 면별 왜곡 비율) 캐시. Distortion 표시용.</summary>
    private readonly Dictionary<NodeId, (int version, float[] ratio)> _distortionCache = new();
    /// <summary>노드 → (메시 버전, 통계) 캐시. 통계 HUD용.</summary>
    private readonly Dictionary<NodeId, (int version, (int shells, int overlapping, int reversed, float usage) stats)> _statsCache = new();

    /// <summary>
    /// 체커 배경 텍스처를 만든다(캐시). 한 변 CheckerSize 칸, 텍스처 크기는 약 512px가 되도록 칸 크기의 배수로 맞춘다.
    /// </summary>
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

    // ---------------------------------------------------------------- 성능 측정(--uvperf)

    /// <summary>
    /// `-- --uvperf`: UV 편집기가 열려 있는 동안 매 프레임 다시 그리고(레이어 포함) 합성 호버를 돌려,
    /// 60프레임마다 그리기(_Draw + 레이어 Paint 합)/호버 평균 시간과 FPS를 `[UvPerf]` 줄로 출력한다(vsync 끔).
    /// </summary>
    private static readonly bool PerfMode = OS.GetCmdlineUserArgs().Contains("--uvperf");
    /// <summary>_perfDraw/_perfHover: 누적 ms, _perfFrames/_perfHoverN: 누적 횟수.</summary>
    private double _perfDraw, _perfHover; private int _perfFrames, _perfHoverN;

    /// <summary>
    /// 성능 측정 모드에서만 동작: vsync를 끄고, 시간에 따라 움직이는 합성 커서로 호버를 계산해 측정하며, 매 프레임 다시 그린다.
    /// 60프레임마다 평균을 출력하고 누적을 초기화한다.
    /// </summary>
    public override void _Process(double delta)
    {
        if (!PerfMode || !IsVisibleInTree()) return;
        if (_perfFrames == 0 && _perfHoverN == 0) DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        float t = (float)(Time.GetTicksMsec() % 4000) / 4000f;
        UpdateHover(new GVec2(Size.X * t, Size.Y * (0.3f + 0.4f * t)));
        _perfHover += sw.Elapsed.TotalMilliseconds; _perfHoverN++;
        QueueRedraw();
        if (_perfFrames >= 60)
        {
            int faces = 0, pts = 0; foreach (var n in TargetNodes()) { faces += n.Mesh!.FaceCount; pts += Topo(n).Points.Count; }
            GD.Print($"[UvPerf] faces={faces} uvpoints={pts} mode={_shell.Document.Selection.Mode} draw={_perfDraw / _perfFrames:F2}ms hover={_perfHover / Math.Max(_perfHoverN, 1):F2}ms fps={Engine.GetFramesPerSecond():F0}");
            _perfDraw = 0; _perfFrames = 0; _perfHover = 0; _perfHoverN = 1;
        }
    }

    /// <summary>그리기 함수를 실행하며 측정 모드면 걸린 시간을 누적한다(UiPerf가 켜져 있으면 그쪽에도 기록).</summary>
    private void Timed(Action a)
    {
        if (!PerfMode && !UiPerf.Enabled) { a(); return; }
        long t0 = UiPerf.Begin();
        var sw = System.Diagnostics.Stopwatch.StartNew(); a(); _perfDraw += sw.Elapsed.TotalMilliseconds;
        UiPerf.End("uvPaint", t0);
    }

    // ---------------------------------------------------------------- 레이어
    //
    // 그리기 순서(뒤 → 앞): 배경 레이어 → 면 틴트·와이어 레이어 → 텍스처 경계·선택 엣지 레이어 → UV 점 레이어 → 이 컨트롤(_Draw: 호버, 마키,
    // 조작기, 브러시, 상태 글자) → 파이 메뉴. 메시는 노드별 RenderingServer 메시(UV 공간)로 캐시해 레이어마다 명령 한두 개로 그린다.
    // 와이어 굵기와 점 크기는 정점의 UV 속성(픽셀 단위 오프셋)과 셰이더 유니폼(1/줌)으로 정하므로 팬/줌/호버에는 다시 만들 필요가 없다.
    // 예전에는 요소마다 DrawLine/DrawColoredPolygon/DrawRect를 불러 12k 면에서 _Draw 한 번에 130ms(4fps)가 걸렸다.

    /// <summary>그리기 레이어 4개(배경 / 면 틴트·와이어 / 굵은 선 / UV 점). 자식 순서대로 이 컨트롤의 _Draw보다 먼저 그려진다.</summary>
    private UvCanvasLayer _bgLayer = null!, _meshLayer = null!, _lineLayer = null!, _pointLayer = null!;
    /// <summary>메시·점 레이어용 오프셋 셰이더 머티리얼(레이어마다 따로 두어 px_to_local 유니폼을 각각 설정).</summary>
    private ShaderMaterial _meshMat = null!, _pointMat = null!;
    /// <summary>오프셋 셰이더 공유 인스턴스(지연 생성).</summary>
    private static Shader? _offsetShader;

    /// <summary>VERTEX(UV 공간) += UV(픽셀 오프셋) × 1/줌: 줌과 무관하게 일정한 픽셀 굵기/크기.</summary>
    private static Shader OffsetShader => _offsetShader ??= new Shader
    {
        Code = "shader_type canvas_item;\nuniform float px_to_local = 1.0;\nvoid vertex() { VERTEX += UV * px_to_local; }\n",
    };

    /// <summary>레이어 4개를 만들고 각 레이어의 Paint 콜백(측정 래퍼 포함)과 머티리얼을 연결한다.</summary>
    private void SetupLayers()
    {
        _meshMat = new ShaderMaterial { Shader = OffsetShader };
        _pointMat = new ShaderMaterial { Shader = OffsetShader };
        _bgLayer = AddLayer("UvBackground", l => Timed(() => PaintBackground(l)), null);
        _meshLayer = AddLayer("UvMeshes", l => Timed(() => PaintMeshes(l)), _meshMat);
        _lineLayer = AddLayer("UvThickLines", l => Timed(() => PaintThickLines(l)), null);
        _pointLayer = AddLayer("UvPoints", l => Timed(() => PaintPoints(l)), _pointMat);
    }

    /// <summary>캔버스를 꽉 채우는 레이어를 자식으로 추가한다.</summary>
    private UvCanvasLayer AddLayer(string name, Action<UvCanvasLayer> paint, Material? mat)
    {
        var l = new UvCanvasLayer { Name = name, Paint = paint, Material = mat };
        AddChild(l);
        l.SetAnchorsPreset(LayoutPreset.FullRect);
        return l;
    }

    /// <summary>캔버스 전체(레이어 포함)를 다시 그린다.</summary>
    public new void QueueRedraw()
    {
        base.QueueRedraw();
        _bgLayer?.QueueRedraw(); _meshLayer?.QueueRedraw(); _lineLayer?.QueueRedraw(); _pointLayer?.QueueRedraw();
    }

    /// <summary>호버/마키/브러시 커서처럼 맨 위 덧그림만 바뀔 때(메시 레이어는 그대로).</summary>
    private void QueueOverlayRedraw() => base.QueueRedraw();

    /// <summary>UV 공간 → 캔버스 px 변환(x 축 = (줌, 0), y 축 = (0, −줌), 원점 = _origin). 캐시 메시를 그릴 때 쓴다.</summary>
    private Transform2D UvTransform => new(new GVec2(_zoom, 0), new GVec2(0, -_zoom), _origin);

    /// <summary>이 컨트롤 자신의 그리기 = 덧그림(오버레이). 측정 모드면 시간을 재고 프레임 수를 센다.</summary>
    public override void _Draw()
    {
        if (PerfMode) { Timed(DrawOverlay); _perfFrames++; }
        else DrawOverlay();
    }

    /// <summary>
    /// 배경 레이어: 바탕색 → (UDIM 타일이 켜져 있으면 −1..2 범위 3×3 타일, 아니면 0..1 한 칸)마다 배경 이미지·그리드 선·테두리·타일 번호.
    /// 0..1 칸(home)은 더 밝게, 다른 타일은 반투명으로 그린다. Dim이 켜져 있으면 이미지를 어둡게.
    /// </summary>
    private void PaintBackground(UvCanvasLayer L)
    {
        float s = CubeApp.Instance.UiScale;
        L.DrawRect(new Rect2(GVec2.Zero, Size), MathConvert.Rgb(0x2b2b2b));
        // 0..1 칸의 화면 사각형(위쪽 = V 1).
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
                    case UvBackground.UvTexture: if (_gridTex != null) L.DrawTextureRect(_gridTex, r, false, new Color(1, 1, 1, home ? imgAlpha : imgAlpha * 0.5f)); else L.DrawRect(r, MathConvert.Rgb(0x3a3a3a)); break;
                    case UvBackground.Checker: { var ct = CheckerTexture(); if (ct != null) L.DrawTextureRect(ct, r, false, new Color(1, 1, 1, home ? imgAlpha + 0.2f : imgAlpha * 0.5f)); break; }
                    case UvBackground.Mapped: { var tex = MappedTexture(); if (tex != null) L.DrawTextureRect(tex, r, false, new Color(1, 1, 1, home ? (_dim ? 0.7f : 1f) : 0.35f)); else L.DrawRect(r, MathConvert.Rgb(0x3a3a3a)); break; }
                    case UvBackground.Grid: L.DrawRect(r, MathConvert.Rgb(0x333333)); break;
                }
                // 그리드 선은 그리드/없음 배경일 때만: 0.1 간격, 0.5마다 밝게.
                if (_gridLines && (_background == UvBackground.Grid || _background == UvBackground.None))
                    for (int i = 0; i <= 10; i++)
                    {
                        float t = i / 10f; var col = i % 5 == 0 ? MathConvert.Rgb(0x6a6a6a) : MathConvert.Rgb(0x4a4a4a);
                        L.DrawLine(UvToPx(new NVec2(tx + t, ty)), UvToPx(new NVec2(tx + t, ty + 1)), col, 1 * s);
                        L.DrawLine(UvToPx(new NVec2(tx, ty + t)), UvToPx(new NVec2(tx + 1, ty + t)), col, 1 * s);
                    }
                L.DrawRect(r, home ? MathConvert.Rgb(0x9a9a9a) : MathConvert.Rgb(0x555555), false, 1 * s);
                // UDIM 번호 = 1001 + u타일 + v타일×10.
                if (_tiles && tx >= 0 && ty >= 0) L.DrawString(GetThemeDefaultFont(), r.Position + new GVec2(4 * s, 14 * s), (1001 + tx + ty * 10).ToString(), HorizontalAlignment.Left, -1, (int)(11 * s), MayaTheme.TextDim);
            }
    }

    /// <summary>면 틴트 + 일반 와이어(1px×배율, AA 없음: 수만 개 선에 AA 페더를 붙이면 CPU·렌더 비용 대부분을 차지했다).</summary>
    private void PaintMeshes(UvCanvasLayer L)
    {
        // 캐시를 최신으로 맞추고 셰이더에 1/줌을 넘긴 뒤 노드별 면 메시 → 와이어 메시 순으로 한 번씩 추가한다.
        var geoms = EnsureGeometry();
        _meshMat.SetShaderParameter("px_to_local", 1f / _zoom);
        var item = L.GetCanvasItem(); var xf = UvTransform;
        foreach (var g in geoms) if (g.FaceVerts > 0) RenderingServer.CanvasItemAddMesh(item, g.FaceMesh, xf);
        foreach (var g in geoms) if (g.WireVerts > 0) RenderingServer.CanvasItemAddMesh(item, g.WireMesh, xf);
    }

    /// <summary>텍스처 경계 → 선택 엣지(굵게, AA; 페더가 픽셀 단위여야 하므로 화면 공간에서).</summary>
    private void PaintThickLines(UvCanvasLayer L)
    {
        var geoms = EnsureGeometry();
        float w = 2.5f * CubeApp.Instance.UiScale;
        DrawPxLines(L, geoms, g => g.Border, w);
        DrawPxLines(L, geoms, g => g.Selected, w);
    }

    /// <summary>UV 점 레이어: 노드별 점 메시(픽셀 크기 오프셋 사각형)를 한 번에 그린다.</summary>
    private void PaintPoints(UvCanvasLayer L)
    {
        var geoms = EnsureGeometry();
        _pointMat.SetShaderParameter("px_to_local", 1f / _zoom);
        var item = L.GetCanvasItem(); var xf = UvTransform;
        foreach (var g in geoms) if (g.PointVerts > 0) RenderingServer.CanvasItemAddMesh(item, g.PointMesh, xf);
    }

    /// <summary>맨 위 덧그림: 호버 엣지/점, 마키, 조작기, 브러시, 상태 글자, 통계.</summary>
    private void DrawOverlay()
    {
        float s = CubeApp.Instance.UiScale;
        var sel = _shell.Document.Selection;
        var font = GetThemeDefaultFont(); int fs = (int)(11 * s);
        // 대상 목록은 EnsureGeometry가 만든 것을 그대로 쓴다.
        EnsureGeometry();
        var targets = _geomTargets;
        if (targets.Count == 0)
        {
            DrawString(font, new GVec2(12 * s, 20 * s), "Select an object or components to edit its UVs.", HorizontalAlignment.Left, -1, fs, MayaTheme.TextDim);
            return;
        }
        DrawHover(targets, sel, s);
        // UV Symmetry 축선(U = 빨강 세로선, V = 초록 가로선).
        if (_shell.UvSymmetryPlane is { } symPlane)
        {
            var col = symPlane.Axis == 0 ? new Color(1f, 0.35f, 0.35f, 0.85f) : new Color(0.4f, 1f, 0.35f, 0.85f);
            if (symPlane.Axis == 0) { float x = UvToPx(new NVec2(symPlane.Center, 0)).X; DrawLine(new GVec2(x, 0), new GVec2(x, Size.Y), col, 1.5f * s); }
            else { float y = UvToPx(new NVec2(0, symPlane.Center)).Y; DrawLine(new GVec2(0, y), new GVec2(Size.X, y), col, 1.5f * s); }
        }
        // 마키 사각형.
        if (_marquee && _marqueeEnd is { } me)
        {
            var r = RectFrom(_pressPos, me);
            DrawRect(r, new Color(1, 1, 1, 0.08f));
            DrawRect(r, new Color(1, 1, 1, 0.9f), false, 1 * s);
        }
        DrawGizmo(s);
        // 브러시 툴이면 브러시 원.
        if (_tool is UvCanvasTool.Grab or UvCanvasTool.Smooth or UvCanvasTool.Pinch or UvCanvasTool.Smear or UvCanvasTool.PinBrush && _brushPos is { } bp)
        {
            float r = BrushRadius;
            DrawArc(bp, r, 0, Mathf.Tau, 48, new Color(1f, 0.4f, 0.4f, 0.9f), 1.5f * s, true);
        }
        // 하단 상태 줄: 모드, 툴, 줌(400px/단위 = 100%), UV 세트, Isolate/Pixel Snap 표시.
        string modeText = IslandMode && sel.Mode == SelectMode.Uv ? "Island" : sel.Mode switch { SelectMode.Uv => "UV", SelectMode.Edge => "Edge", SelectMode.Face => "Face", SelectMode.Vertex => "Vertex", _ => "Object" };
        string toolText = _tool == UvCanvasTool.None ? _shell.Tools.Current?.Label ?? "" : _tool.ToString();
        string setText = "";
        var first = targets[0].Mesh!; if (first.UvSets.Count > 1) setText = $"   set: {first.UvSets[Math.Clamp(first.CurrentUvSet, 0, first.UvSets.Count - 1)].Name}";
        DrawString(font, new GVec2(12 * s, Size.Y - 10 * s), $"{modeText} mode   tool: {toolText}   zoom {(_zoom / 400f):P0}{setText}{(_isolate != null ? "   [isolate]" : "")}{(_pixelSnap ? "   [pixel snap]" : "")}{(_shell.UvSymmetryPlane is { } sy ? $"   [symmetry {(sy.Axis == 0 ? "U" : "V")} {sy.Center:0.###}]" : "")}", HorizontalAlignment.Left, -1, fs, MayaTheme.TextDim);
        // 통계 HUD: 노드별로 지오메트리 버전이 바뀌었을 때만 다시 계산.
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

    /// <summary>호버 엣지(흰색 굵게)와 호버 UV 점(흰색 크게; Island 모드는 셸 전체)을 캐시 위에 덧그린다. 면 호버는 EnsureGeometry가 면 색을 바꾼다.</summary>
    private void DrawHover(List<SceneNode> targets, SelectionState sel, float s)
    {
        if (_hover is not { } h || h.Component < 0) return;
        var hn = targets.Find(n => n.Id == h.Node);
        if (hn == null || !_geom.TryGetValue(hn.Id, out var g)) return;
        var m = hn.Mesh!;
        // 엣지 모드: 엣지의 양쪽 하프에지 각각의 UV 선분(심이면 UV가 두 군데)을 흰색으로.
        if (sel.Mode == SelectMode.Edge && h.Component < m.EdgeCount && m.Edges[h.Component].Alive)
        {
            var ed = m.Edges[h.Component];
            for (int k = 0; k < 2; k++)
            {
                int he = k == 0 ? ed.He0 : ed.He1;
                if (he < 0 || !FaceVisible(hn.Id, m.Hes[he].Face)) continue;
                DrawLine(UvToPx(m.Hes[he].Uv0), UvToPx(m.Hes[m.Hes[he].Next].Uv0), MeshView.Hover, 2.5f * s, true);
            }
        }
        // UV 모드: 호버 점(Island면 같은 셸의 모든 점)을 즉시 그리기 묶음으로 그린다(핀이면 바깥 파랑 테두리).
        else if (sel.Mode == SelectMode.Uv)
        {
            var topo = Topo(hn);
            if (h.Component >= topo.Points.Count || g.PtFlags.Length != topo.Points.Count) return;
            _pxPoints.Clear();
            float r = 3.5f * s;
            void Add(int i)
            {
                if ((g.PtFlags[i] & PtVisible) == 0) return;
                var p = UvToPx(topo.Points[i].Uv);
                if ((g.PtFlags[i] & PtPinned) != 0) _pxPoints.Rect(p, r + 2 * s, PinCol);
                _pxPoints.Rect(p, r, MeshView.Hover);
            }
            if (IslandMode) { int shell = topo.Points[h.Component].Shell; for (int i = 0; i < topo.Points.Count; i++) if (topo.Points[i].Shell == shell) Add(i); }
            else Add(h.Component);
            _pxPoints.DrawImmediate(GetCanvasItem());
        }
    }

    /// <summary>
    /// 노드별 선분 묶음(UV 공간)을 화면 px로 바꿔 한 번의 DrawMultilineColors(AA)로 그린다. AA 페더가 픽셀 단위여야 해서 메시 캐시 대신 매번 화면 공간에서 그린다.
    /// </summary>
    private void DrawPxLines(CanvasItem target, List<NodeGeom> geoms, Func<NodeGeom, LineBatch> pick, float width)
    {
        _pxLines.Clear();
        foreach (var g in geoms)
        {
            var b = pick(g);
            for (int i = 0; i < b.Count; i++) _pxLines.Add(UvPtToPx(b.Pts[2 * i]), UvPtToPx(b.Pts[2 * i + 1]), b.ColorAt(i));
        }
        if (_pxLines.Count > 0) target.DrawMultilineColors(_pxLines.Points, _pxLines.Colors, width, true);
    }

    /// <summary>UV(Godot Vector2) → 캔버스 px.</summary>
    private GVec2 UvPtToPx(GVec2 uv) => _origin + new GVec2(uv.X, -uv.Y) * _zoom;

    // ---------------------------------------------------------------- 배치 버퍼(재사용)

    /// <summary>색·오프셋이 있는 삼각형 묶음. 메시로 올리거나(Upload) 바로 그린다(DrawImmediate).</summary>
    private sealed class TriBatch
    {
        // 정점 위치/픽셀 오프셋/색 배열과 인덱스 배열. 모자라면 두 배로 늘리고 Clear 후에도 재사용한다(할당 최소화).
        private GVec2[] _pts = new GVec2[256], _offs = new GVec2[256]; private Color[] _cols = new Color[256]; private int[] _idx = new int[512];
        // _np = 정점 수, _ni = 인덱스 수.
        private int _np, _ni;
        /// <summary>현재 정점 수.</summary>
        public int VertexCount => _np;
        /// <summary>내용을 비운다(배열은 유지).</summary>
        public void Clear() { _np = 0; _ni = 0; }
        /// <summary>정점 vertexCount개 자리를 확보하고 첫 정점 인덱스를 돌려준다.</summary>
        public int Begin(int vertexCount)
        {
            if (_np + vertexCount > _pts.Length)
            {
                int n = Math.Max(_pts.Length * 2, _np + vertexCount);
                Array.Resize(ref _pts, n); Array.Resize(ref _cols, n); Array.Resize(ref _offs, n);
            }
            int b = _np; _np += vertexCount; return b;
        }
        /// <summary>정점 i의 위치·색·픽셀 오프셋을 설정한다.</summary>
        public void Set(int i, GVec2 p, Color c, GVec2 off = default) { _pts[i] = p; _cols[i] = c; _offs[i] = off; }
        /// <summary>삼각형 인덱스 하나를 추가한다.</summary>
        public void Tri(int a, int b, int c)
        {
            if (_ni + 3 > _idx.Length) Array.Resize(ref _idx, Math.Max(_idx.Length * 2, _ni + 3));
            _idx[_ni++] = a; _idx[_ni++] = b; _idx[_ni++] = c;
        }
        /// <summary>start부터 count개 정점의 색을 c로 바꾼다.</summary>
        public void SetColorRange(int start, int count, Color c) { for (int i = 0; i < count; i++) _cols[start + i] = c; }
        /// <summary>b..b+3 네 정점으로 사각형(삼각형 2개)을 만든다.</summary>
        public void Quad(int b) { Tri(b, b + 1, b + 2); Tri(b, b + 2, b + 3); }
        /// <summary>화면 공간 사각형(즉시 그리기용).</summary>
        public void Rect(GVec2 center, float r, Color c)
        {
            int b = Begin(4);
            Set(b, center + new GVec2(-r, -r), c); Set(b + 1, center + new GVec2(r, -r), c); Set(b + 2, center + new GVec2(r, r), c); Set(b + 3, center + new GVec2(-r, r), c);
            Quad(b);
        }
        /// <summary>UV 공간 중심 + 픽셀 반지름 오프셋 사각형(셰이더가 1/줌을 곱한다).</summary>
        public void OffsetRect(GVec2 center, float r, Color c)
        {
            int b = Begin(4);
            Set(b, center, c, new GVec2(-r, -r)); Set(b + 1, center, c, new GVec2(r, -r)); Set(b + 2, center, c, new GVec2(r, r)); Set(b + 3, center, c, new GVec2(-r, r));
            Quad(b);
        }
        /// <summary>UV 공간 선분 a→b를 픽셀 굵기 w의 사각형으로(오프셋 = 수직 방향 × w/2 픽셀).</summary>
        public void OffsetLine(GVec2 a, GVec2 b, float w, Color c)
        {
            var d = b - a; float len = d.Length();
            var n = len > 0 ? new GVec2(-d.Y, d.X) / len * (w * 0.5f) : GVec2.Zero;
            int i = Begin(4);
            Set(i, a, c, n); Set(i + 1, b, c, n); Set(i + 2, b, c, -n); Set(i + 3, a, c, -n);
            Quad(i);
        }
        /// <summary>캔버스 아이템에 삼각형 배열로 바로 그린다(호버처럼 매번 바뀌는 소량 그리기용).</summary>
        public void DrawImmediate(Rid item)
        {
            if (_ni == 0) return;
            RenderingServer.CanvasItemAddTriangleArray(item, new ReadOnlySpan<int>(_idx, 0, _ni), new ReadOnlySpan<GVec2>(_pts, 0, _np), new ReadOnlySpan<Color>(_cols, 0, _np),
                ReadOnlySpan<GVec2>.Empty, ReadOnlySpan<int>.Empty, ReadOnlySpan<float>.Empty, default, -1);
        }
        /// <summary>메시 RID의 내용을 이 묶음으로 바꾼다(정점 0이면 비움).</summary>
        public void Upload(Rid mesh)
        {
            RenderingServer.MeshClear(mesh);
            if (_ni == 0) return;
            var arr = new Godot.Collections.Array(); arr.Resize((int)RenderingServer.ArrayType.Max);
            arr[(int)RenderingServer.ArrayType.Vertex] = _pts.AsSpan(0, _np).ToArray();
            arr[(int)RenderingServer.ArrayType.Color] = _cols.AsSpan(0, _np).ToArray();
            arr[(int)RenderingServer.ArrayType.TexUV] = _offs.AsSpan(0, _np).ToArray();
            arr[(int)RenderingServer.ArrayType.Index] = _idx.AsSpan(0, _ni).ToArray();
            RenderingServer.MeshAddSurfaceFromArrays(mesh, RenderingServer.PrimitiveType.Triangles, arr);
        }
    }

    /// <summary>선분 묶음(선분마다 색).</summary>
    private sealed class LineBatch
    {
        /// <summary>Pts: 선분 끝점 쌍(2i, 2i+1), _cols: 선분별 색.</summary>
        public GVec2[] Pts = new GVec2[256]; private Color[] _cols = new Color[128];
        /// <summary>선분 수.</summary>
        public int Count;
        /// <summary>비운다(배열 유지).</summary>
        public void Clear() => Count = 0;
        /// <summary>선분 하나를 추가한다(배열이 모자라면 두 배로).</summary>
        public void Add(GVec2 a, GVec2 b, Color c)
        {
            if (Count >= _cols.Length) { Array.Resize(ref _cols, _cols.Length * 2); Array.Resize(ref Pts, _cols.Length * 2); }
            Pts[2 * Count] = a; Pts[2 * Count + 1] = b; _cols[Count++] = c;
        }
        /// <summary>i번 선분 색.</summary>
        public Color ColorAt(int i) => _cols[i];
        /// <summary>유효한 끝점 범위(DrawMultilineColors 인자).</summary>
        public ReadOnlySpan<GVec2> Points => new(Pts, 0, Count * 2);
        /// <summary>유효한 색 범위.</summary>
        public ReadOnlySpan<Color> Colors => new(_cols, 0, Count);
    }

    /// <summary>노드 하나의 그리기 캐시: UV 공간 메시(면 틴트/와이어/점) + 굵은 선 목록 + 점 플래그. 문서·선택·표시 옵션이 바뀔 때만 다시 만든다.</summary>
    private sealed class NodeGeom
    {
        // Topo: 캐시를 만든 위상, Stamp: 만든 시점의 _geomStamp, Scale: 만든 시점의 UI 배율(바뀌면 다시 만든다).
        public UvTopology? Topo; public int Stamp = -1; public float Scale = -1;
        // 노드별 RenderingServer 메시 RID 3개(면/와이어/점). Free()로 해제한다.
        public readonly Rid FaceMesh = RenderingServer.MeshCreate(), WireMesh = RenderingServer.MeshCreate(), PointMesh = RenderingServer.MeshCreate();
        // 각 메시의 정점 수(0이면 그리지 않음).
        public int FaceVerts, WireVerts, PointVerts;
        // 면 f의 틴트 정점 시작 인덱스·개수(면 호버 패치용), FaceBase = 면의 원래 틴트 색.
        public int[] FaceStart = Array.Empty<int>(), FaceCount = Array.Empty<int>();
        public Color[] FaceBase = Array.Empty<Color>();
        // 면 호버: 메시 속성 버퍼에서 그 면 정점의 색 바이트만 바꾼다(전체 재업로드 없이)
        // Attr: 면 메시 속성 버퍼 사본, AttrStride/ColorOffset/ColorSize: 정점당 바이트 배치, HoverColorBytes: 호버 색 바이트.
        public byte[]? Attr; public int AttrStride, ColorOffset, ColorSize; public byte[]? HoverColorBytes;
        // 현재 호버 색으로 패치된 면(-1 = 없음).
        public int PatchedFace = -1;
        // 굵은 선 목록: 텍스처 경계, 선택 엣지(UV 공간 끝점).
        public readonly LineBatch Border = new(), Selected = new();
        // UV 점별 플래그(PtVisible/PtSelected/PtPinned). 호버 그리기와 피킹에서 쓴다.
        public byte[] PtFlags = Array.Empty<byte>();
        // 렌더링 서버 메시 해제(노드가 사라지거나 캔버스가 지워질 때).
        public void Free() { RenderingServer.FreeRid(FaceMesh); RenderingServer.FreeRid(WireMesh); RenderingServer.FreeRid(PointMesh); }
    }

    /// <summary>UV 점 플래그 비트: 보임(격리·면 가시성), 선택됨, 핀.</summary>
    private const byte PtVisible = 1, PtSelected = 2, PtPinned = 4;
    /// <summary>노드 → 그리기 캐시.</summary>
    private readonly Dictionary<NodeId, NodeGeom> _geom = new();
    /// <summary>이번 프레임에 그릴 캐시 목록(대상 노드 순서).</summary>
    private readonly List<NodeGeom> _geomList = new();
    /// <summary>이번 프레임의 대상 노드 목록(EnsureGeometry가 채움).</summary>
    private readonly List<SceneNode> _geomTargets = new();
    /// <summary>캐시 무효화 번호: 문서/선택/모드/표시 옵션/Isolate가 바뀌면 증가.</summary>
    private int _geomStamp;
    /// <summary>그리기 캐시를 무효화하고 다시 그린다. 새 표시 옵션을 추가하면 setter에서 반드시 이것을 불러야 한다.</summary>
    private void MarkGeomDirty() { _geomStamp++; QueueRedraw(); }

    /// <summary>_build: 캐시 메시를 만들 때 쓰는 공용 묶음, _pxPoints: 호버 점 즉시 그리기용 묶음.</summary>
    private readonly TriBatch _build = new(), _pxPoints = new();
    /// <summary>굵은 선 화면 공간 그리기용 묶음.</summary>
    private readonly LineBatch _pxLines = new();
    /// <summary>면 하나의 UV 좌표 임시 버퍼(면 꼭짓점 수에 맞춰 늘린다).</summary>
    private NVec2[] _faceUv = new NVec2[16];
    /// <summary>색: 일반 와이어(밝은 회색), 텍스처 경계(노랑), 핀(진파랑).</summary>
    private static readonly Color EdgeNormalCol = MathConvert.Rgb(0xdddddd), EdgeBorderCol = MathConvert.Rgb(0xffe034), PinCol = MathConvert.Rgb(0x2255ff);
    /// <summary>색: 선택 면(주황 반투명), 호버 면(흰색 반투명).</summary>
    private static readonly Color FaceSelCol = new(1f, 0.55f, 0f, 0.35f), FaceHoverCol = new(1f, 1f, 1f, 0.18f);

    /// <summary>모든 노드 캐시의 렌더링 서버 메시를 해제하고 비운다.</summary>
    private void FreeGeometry() { foreach (var g in _geom.Values) g.Free(); _geom.Clear(); _geomList.Clear(); }

    /// <summary>현재 대상 노드의 캐시를 최신으로 만들고(필요한 것만 다시 빌드) 면 호버 색을 반영한다. 레이어/덧그림 어디서 먼저 불려도 된다.</summary>
    private List<NodeGeom> EnsureGeometry()
    {
        // 대상 목록을 다시 모으고, 더 이상 대상이 아닌 노드의 캐시는 해제한다.
        var sel = _shell.Document.Selection; float s = CubeApp.Instance.UiScale;
        _geomTargets.Clear(); _geomTargets.AddRange(TargetNodes());
        if (_geom.Count > _geomTargets.Count || _geom.Keys.Any(id => !_geomTargets.Exists(n => n.Id == id)))
            foreach (var id in _geom.Keys.ToList()) if (!_geomTargets.Exists(n => n.Id == id)) { _geom[id].Free(); _geom.Remove(id); }
        _geomList.Clear();
        // 노드마다: 스탬프·배율·위상이 바뀌었으면 다시 빌드.
        foreach (var node in _geomTargets)
        {
            var topo = Topo(node);
            if (!_geom.TryGetValue(node.Id, out var g)) { g = new NodeGeom(); _geom[node.Id] = g; }
            if (g.Stamp != _geomStamp || g.Scale != s || !ReferenceEquals(g.Topo, topo)) { long t0 = UiPerf.Begin(); BuildGeom(g, node, topo, sel, s); UiPerf.End("uvBuildGeom", t0); }
            // 면 모드에서 호버 중인 면(선택된 면은 제외)을 찾는다.
            int want = -1;
            if (sel.Mode == SelectMode.Face && _hover is { } hf && hf.Node == node.Id && hf.Component >= 0 && hf.Component < g.FaceStart.Length
                && g.FaceStart[hf.Component] >= 0 && g.FaceBase[hf.Component] != FaceSelCol) want = hf.Component;
            // 호버 면이 바뀌었으면 이전 면의 색을 되돌리고 새 면을 호버 색으로 패치한다.
            if (want != g.PatchedFace)
            {
                if (g.HoverColorBytes != null)
                {
                    if (g.PatchedFace >= 0) PatchFaceColor(g, g.PatchedFace, hover: false);
                    if (want >= 0) PatchFaceColor(g, want, hover: true);
                    g.PatchedFace = want;
                }
                else BuildGeom(g, node, topo, sel, s);   // 속성 버퍼를 직접 고칠 수 없으면 다시 빌드(호버 색을 구워 넣음)
            }
            _geomList.Add(g);
        }
        return _geomList;
    }

    /// <summary>
    /// 면 f의 틴트 정점들의 색 바이트만 GPU 속성 버퍼에서 바꾼다(hover = 호버 색, false = 원래 캐시 사본의 색으로 복원).
    /// 메시 전체를 다시 올리지 않으므로 수만 면에서도 호버가 가볍다.
    /// </summary>
    private static void PatchFaceColor(NodeGeom g, int f, bool hover)
    {
        int start = g.FaceStart[f], n = g.FaceCount[f], stride = g.AttrStride;
        var region = new byte[n * stride];
        Buffer.BlockCopy(g.Attr!, start * stride, region, 0, region.Length);
        if (hover) for (int i = 0; i < n; i++) Buffer.BlockCopy(g.HoverColorBytes!, 0, region, i * stride + g.ColorOffset, g.ColorSize);
        RenderingServer.MeshSurfaceUpdateAttributeRegion(g.FaceMesh, 0, start * stride, region);
    }

    /// <summary>면 틴트/와이어/점 메시와 경계·선택 선 목록을 다시 만든다(예전 요소별 그리기와 같은 색·굵기·조건).</summary>
    private void BuildGeom(NodeGeom g, SceneNode node, UvTopology topo, SelectionState sel, float s)
    {
        // 캐시 메타데이터 갱신.
        g.Topo = topo; g.Stamp = _geomStamp; g.Scale = s; g.PatchedFace = -1;
        var m = node.Mesh!;
        var selPts = SelectedPoints(node);
        // Distortion이 켜져 있으면 면별 왜곡 비율(지오메트리 버전으로 캐시)을 쓴다.
        float[]? ratio = null;
        if (_distortion)
        {
            if (!_distortionCache.TryGetValue(node.Id, out var dc) || dc.version != m.GeometryVersion) { dc = (m.GeometryVersion, UvOps.DistortionPerFace(m)); _distortionCache[node.Id] = dc; }
            ratio = dc.ratio;
        }
        // 현재 모드의 선택 면/엣지 집합(해당 모드가 아니면 null).
        sel.Components.TryGetValue(node.Id, out var comps);
        var selFaces = sel.Mode == SelectMode.Face ? comps?.Faces : null;
        var selEdges = sel.Mode == SelectMode.Edge ? comps?.Edges : null;
        // List<struct> 인덱서는 구조체(하프에지 48바이트)를 매번 복사하므로 스팬으로 읽는다
        var hes = CollectionsMarshal.AsSpan(m.Hes); var faces = CollectionsMarshal.AsSpan(m.Faces); var edges = CollectionsMarshal.AsSpan(m.Edges);
        bool iso = _isolate != null;
        int hoverFace = sel.Mode == SelectMode.Face && _hover is { } hf && hf.Node == node.Id ? hf.Component : -1;

        // 면 틴트
        _build.Clear();
        if (g.FaceStart.Length != faces.Length) { g.FaceStart = new int[faces.Length]; g.FaceCount = new int[faces.Length]; g.FaceBase = new Color[faces.Length]; }
        for (int f = 0; f < faces.Length; f++)
        {
            g.FaceStart[f] = -1;
            if (!faces[f].Alive || (iso && !FaceVisible(node.Id, f))) continue;
            // 면의 UV 다각형을 모은다.
            int n = 0, start = faces[f].HalfEdge, he = start;
            do
            {
                if (n == _faceUv.Length) Array.Resize(ref _faceUv, n * 2);
                _faceUv[n++] = hes[he].Uv0; he = hes[he].Next;
            } while (he != start);
            if (n < 3) continue;
            var poly = new ReadOnlySpan<NVec2>(_faceUv, 0, n);
            // 부호 있는 넓이(신발끈 공식). 양수 = 앞면(CCW), 음수 = UV가 뒤집힌 면.
            float area = 0;
            for (int i = 0; i < n; i++) { var a = poly[i]; var b = poly[(i + 1) % n]; area += a.X * b.Y - b.X * a.Y; }
            area *= 0.5f;
            if (MathF.Abs(area) < 1e-12f) continue;   // 면적 0(퇴화) 면은 그리지 않음
            // 틴트 색 우선순위: 선택 > 왜곡(빨강 = 줄어듦, 파랑 = 늘어남) > Shaded(앞 파랑/뒤 빨강) > 기본 옅은 파랑.
            Color col;
            if (selFaces != null && selFaces.Contains(f)) col = FaceSelCol;
            else if (ratio != null) { float rr = ratio[f]; col = rr < 1 ? new Color(1f, 0.3f, 0.3f, Math.Clamp((1 - rr) * 1.5f, 0.05f, 0.6f)) : new Color(0.3f, 0.5f, 1f, Math.Clamp((rr - 1) * 1.5f, 0.05f, 0.6f)); }
            else if (_shaded) col = area >= 0 ? new Color(0.35f, 0.55f, 1f, 0.25f) : new Color(1f, 0.35f, 0.35f, 0.25f);
            else col = new Color(0.6f, 0.75f, 1f, 0.08f);
            g.FaceBase[f] = col;
            int b0 = _build.Begin(n);
            for (int i = 0; i < n; i++) _build.Set(b0 + i, new GVec2(poly[i].X, poly[i].Y), col);
            g.FaceStart[f] = b0; g.FaceCount[f] = n;
            // 볼록 면은 팬 삼각분할.
            if (n == 3 || IsConvex(poly)) { for (int i = 1; i + 1 < n; i++) _build.Tri(b0, b0 + i, b0 + i + 1); continue; }
            // 오목 면: 큰 배율로 키워 삼각분할(작은 UV 값에서 엔진 epsilon에 걸리지 않게)
            var tmp = new GVec2[n];
            for (int i = 0; i < n; i++) tmp[i] = new GVec2(poly[i].X, poly[i].Y) * 1000f;
            var tris = Geometry2D.TriangulatePolygon(tmp);
            for (int t = 0; t + 2 < tris.Length; t += 3) _build.Tri(b0 + tris[t], b0 + tris[t + 1], b0 + tris[t + 2]);
        }
        int faceVerts = _build.VertexCount;
        if (faceVerts > 0) { int sv = _build.Begin(1); _build.Set(sv, GVec2.Zero, FaceHoverCol); }   // 호버 색 바이트를 얻기 위한 표본 정점(인덱스 없음)
        _build.Upload(g.FaceMesh);
        g.FaceVerts = faceVerts;
        CaptureFaceAttributes(g, faceVerts);
        if (hoverFace >= 0 && hoverFace < faces.Length && g.FaceStart[hoverFace] >= 0 && g.FaceBase[hoverFace] != FaceSelCol)
        {
            if (g.HoverColorBytes != null) PatchFaceColor(g, hoverFace, hover: true);
            else
            {
                // 패치 불가: 호버 색을 구워 다시 올린다
                _build.SetColorRange(g.FaceStart[hoverFace], g.FaceCount[hoverFace], FaceHoverCol);
                _build.Upload(g.FaceMesh);
            }
            g.PatchedFace = hoverFace;
        }

        // 엣지: 일반 와이어(메시) / 텍스처 경계·선택(굵은 선 목록, 선택이 경계보다 위). 양쪽 하프에지의 UV가 같으면(이음매 없음) 한 번만.
        _build.Clear(); g.Border.Clear(); g.Selected.Clear();
        float wire = 1f * s;
        // 엣지마다 양쪽 하프에지 방향의 UV 선분을 분류한다: 선택 → 선택 목록, 심/경계 → 경계 목록, 나머지 → 와이어 메시.
        for (int e = 0; e < edges.Length; e++)
        {
            ref readonly var ed = ref edges[e];
            if (!ed.Alive) continue;
            bool esel = selEdges != null && selEdges.Contains(e);
            bool border = (ed.Seam || ed.He1 < 0) && _texBorders;
            bool same = ed.He0 >= 0 && ed.He1 >= 0 && hes[ed.He0].Uv0 == hes[hes[ed.He1].Next].Uv0 && hes[hes[ed.He0].Next].Uv0 == hes[ed.He1].Uv0;
            bool drew = false;
            for (int k = 0; k < 2; k++)
            {
                int he = k == 0 ? ed.He0 : ed.He1;
                if (he < 0 || (iso && !FaceVisible(node.Id, hes[he].Face))) continue;
                if (same && drew) continue;
                drew = true;
                var ua = hes[he].Uv0; var ub = hes[hes[he].Next].Uv0;
                var a = new GVec2(ua.X, ua.Y); var b = new GVec2(ub.X, ub.Y);
                if (esel) g.Selected.Add(a, b, MeshView.EdgeSelected);
                else if (border) g.Border.Add(a, b, EdgeBorderCol);
                else _build.OffsetLine(a, b, wire, EdgeNormalCol);
            }
        }
        g.WireVerts = _build.VertexCount;
        _build.Upload(g.WireMesh);

        // UV 점(파랑, 선택 빨강·크게, 핀 = 진파랑 테두리)
        _build.Clear();
        var points = CollectionsMarshal.AsSpan(topo.Points);
        if (g.PtFlags.Length != points.Length) g.PtFlags = new byte[points.Length];
        bool allSel = selPts.Count == points.Length;
        for (int i = 0; i < points.Length; i++)
        {
            var pt = points[i];
            bool vis = true;
            if (iso) { vis = false; foreach (int h in pt.HalfEdges) if (FaceVisible(node.Id, hes[h].Face)) { vis = true; break; } }
            bool ps = allSel || (selPts.Count > 0 && selPts.Contains(i));
            g.PtFlags[i] = (byte)((vis ? PtVisible : 0) | (ps ? PtSelected : 0) | (pt.Pinned ? PtPinned : 0));
            if (!vis) continue;
            float r = (ps ? 3.5f : 2.5f) * s;
            var p = new GVec2(pt.Uv.X, pt.Uv.Y);
            if (pt.Pinned) _build.OffsetRect(p, r + 2 * s, PinCol);
            _build.OffsetRect(p, r, ps ? MeshView.UvSelected : MeshView.UvNormal);
        }
        g.PointVerts = _build.VertexCount;
        _build.Upload(g.PointMesh);
    }

    /// <summary>면 메시의 속성 버퍼(색)를 복사해 두고 마지막 표본 정점에서 호버 색 바이트를 얻는다. 형식을 알 수 없으면 패치를 끈다.</summary>
    /// <remarks>
    /// 원리: BuildGeom이 면 정점 뒤에 호버 색 표본 정점을 하나 붙여 올리므로, 엔진이 실제로 저장한 바이트 형식(압축 여부 포함)의 호버 색을 그대로 얻을 수 있다.
    /// 색 크기는 다음 속성(TexUV) 오프셋까지의 거리로 추정한다. 검증에 실패하면 Attr/HoverColorBytes를 null로 두어 다시 빌드 방식으로 돌아간다.
    /// </remarks>
    private static void CaptureFaceAttributes(NodeGeom g, int faceVerts)
    {
        g.Attr = null; g.HoverColorBytes = null;
        if (faceVerts == 0) return;
        try
        {
            var surf = RenderingServer.MeshGetSurface(g.FaceMesh, 0);
            var fmt = (RenderingServer.ArrayFormat)surf["format"].AsInt64();
            int vc = surf["vertex_count"].AsInt32();
            var attr = surf["attribute_data"].AsByteArray();
            int stride = (int)RenderingServer.MeshSurfaceGetFormatAttributeStride(fmt, vc);
            int colOff = (int)RenderingServer.MeshSurfaceGetFormatOffset(fmt, vc, (int)RenderingServer.ArrayType.Color);
            int uvOff = (int)RenderingServer.MeshSurfaceGetFormatOffset(fmt, vc, (int)RenderingServer.ArrayType.TexUV);
            int colSize = uvOff > colOff ? uvOff - colOff : stride - colOff;
            if (vc != faceVerts + 1 || stride <= 0 || colOff < 0 || colSize <= 0 || colOff + colSize > stride || attr.Length < vc * stride) return;
            var hover = new byte[colSize];
            Buffer.BlockCopy(attr, (vc - 1) * stride + colOff, hover, 0, colSize);
            g.Attr = attr; g.AttrStride = stride; g.ColorOffset = colOff; g.ColorSize = colSize; g.HoverColorBytes = hover;
        }
        catch (Exception) { g.Attr = null; g.HoverColorBytes = null; }
    }

    /// <summary>UV 공간 볼록 판정(외적 부호가 모두 같으면 볼록; 문턱값은 면 크기에 비례).</summary>
    private static bool IsConvex(ReadOnlySpan<NVec2> p)
    {
        int n = p.Length; bool pos = false, neg = false;
        float ext = 0; for (int i = 0; i < n; i++) ext = MathF.Max(ext, NVec2.DistanceSquared(p[i], p[(i + 1) % n]));
        float eps = ext * 1e-6f;
        for (int i = 0; i < n; i++)
        {
            var a = p[i]; var b = p[(i + 1) % n]; var c = p[(i + 2) % n];
            float cr = (b.X - a.X) * (c.Y - b.Y) - (b.Y - a.Y) * (c.X - b.X);
            if (cr > eps) pos = true; else if (cr < -eps) neg = true;
            if (pos && neg) return false;
        }
        return true;
    }

    /// <summary>조작기 색: X(빨강), Y(초록), 중앙/링(하늘색), 활성(노랑 — 호버 또는 드래그 중인 부분).</summary>
    private static readonly Color AxisX = MathConvert.Rgb(0xff2a2a), AxisY = MathConvert.Rgb(0x5aff2a), AxisC = MathConvert.Rgb(0x6ad0ff), Active = MathConvert.Rgb(0xffff00);

    /// <summary>
    /// 2D 조작기를 선택 중심(피벗)에 그린다. 회전 툴 = 원 링, 이동 = X/Y 화살표, 스케일 = X/Y 끝 상자, 공통 중앙 사각형.
    /// 드래그 중에는 피벗을 다시 계산하지 않는다(움직이는 점의 평균을 따라가지 않도록 시작 위치 고정).
    /// </summary>
    private void DrawGizmo(float s)
    {
        if (!GizmoActive) { _hasPivot = false; return; }
        if (!_dragging) _hasPivot = ComputePivot(out _pivotUv);
        if (!_hasPivot) return;
        var c = UvToPx(_pivotUv);
        float len = GizmoLen;
        string tool = _shell.Tools.Current!.Id;
        // 호버/드래그 중인 부분은 활성 색으로.
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

    /// <summary>
    /// 화면 px 위치가 조작기의 어느 부분에 닿는지. 회전 = 링 반지름 ±8px, 그 외 = 중앙 9px 원 → X축 선분 → Y축 선분 순(거리 8px 이내).
    /// </summary>
    private Part HitGizmo(GVec2 px)
    {
        if (!GizmoActive || !_hasPivot) return Part.None;
        float s = CubeApp.Instance.UiScale;
        var c = UvToPx(_pivotUv);
        float len = GizmoLen, th = 8 * s;
        string tool = _shell.Tools.Current!.Id;
        if (tool == "rotate") return MathF.Abs(px.DistanceTo(c) - len) <= th ? Part.Ring : Part.None;
        if (px.DistanceTo(c) <= 9 * s) return Part.Center;
        if (SegDist(px, c, c + new GVec2(len + 6 * s, 0)) <= th) return Part.X;
        if (SegDist(px, c, c - new GVec2(0, len + 6 * s)) <= th) return Part.Y;
        return Part.None;
    }

    /// <summary>대상 노드 중 처음으로 매핑 텍스처(머티리얼 컬러 텍스처)를 가진 뷰포트 MeshView의 텍스처.</summary>
    private Texture2D? MappedTexture()
    {
        foreach (var node in TargetNodes()) { var mv = _shell.Viewport.Scene.GetMeshView(node.Id); if (mv?.MappedTexture != null) return mv.MappedTexture; }
        return null;
    }

    /// <summary>Pixel Snap 기준 해상도(매핑 텍스처 크기, 없으면 체커 512).</summary>
    private int ImagePixels() { var t = MappedTexture(); return t != null ? Math.Max(t.GetWidth(), 1) : 512; }

    /// <summary>선분이 사각형과 겹치는지(Liang–Barsky 클리핑).</summary>
    private static bool RectIntersectsSegment(Rect2 r, GVec2 a, GVec2 b)
    {
        if (r.HasPoint(a) || r.HasPoint(b)) return true;
        // 매개변수 t ∈ [0, 1]을 사각형 네 변의 반평면으로 잘라 남는 구간이 있으면 교차.
        var d = b - a; float t0 = 0, t1 = 1;
        bool Clip(float p, float q, ref float lo, ref float hi)
        {
            if (p == 0) return q >= 0;
            float t = q / p;
            if (p < 0) { if (t > hi) return false; if (t > lo) lo = t; }
            else { if (t < lo) return false; if (t < hi) hi = t; }
            return true;
        }
        return Clip(-d.X, a.X - r.Position.X, ref t0, ref t1) && Clip(d.X, r.End.X - a.X, ref t0, ref t1)
            && Clip(-d.Y, a.Y - r.Position.Y, ref t0, ref t1) && Clip(d.Y, r.End.Y - a.Y, ref t0, ref t1);
    }

    /// <summary>두 점으로 정규화된(최소/최대) 사각형을 만든다(마키용).</summary>
    private static Rect2 RectFrom(GVec2 a, GVec2 b)
    {
        var min = new GVec2(MathF.Min(a.X, b.X), MathF.Min(a.Y, b.Y));
        var max = new GVec2(MathF.Max(a.X, b.X), MathF.Max(a.Y, b.Y));
        return new Rect2(min, max - min);
    }

    // ---------------------------------------------------------------- 입력

    /// <summary>브러시 반지름(px, 배율 적용). 셸의 Brush Options 값.</summary>
    private float BrushRadius => _shell.BrushOptions.Float("radius") * CubeApp.Instance.UiScale;
    /// <summary>브러시 세기(Brush Options).</summary>
    private float BrushStrength => _shell.BrushOptions.Float("strength");
    /// <summary>현재 툴이 원형 브러시 계열인지(Grab/Smooth/Pinch/Smear/PinBrush).</summary>
    private bool IsBrushTool => _tool is UvCanvasTool.Grab or UvCanvasTool.Smooth or UvCanvasTool.Pinch or UvCanvasTool.Smear or UvCanvasTool.PinBrush;

    /// <summary>
    /// 캔버스 입력 처리(우선순위 순):
    /// 버튼 — 파이 메뉴 → Ctrl+휠 브러시 반지름 → 휠 줌 → Alt+버튼 내비게이션 시작/끝 → LMB(툴 누름 / 조작기 핸들 / 선택 클릭 시작, 뗌 = 진행 중 작업 종료·클릭/마키 선택 확정).
    /// 이동 — 파이 포인터 → 내비게이션(MMB 팬, RMB 줌) → 브러시 커서 → Cut/Sew 칠하기 → 브러시 → 변형 드래그 → 마키(4px 이상 끌면 시작) → 호버.
    /// 키 — F 선택 프레임, A 전체 프레임, Esc 변형 취소 / 툴 해제. 그 밖의 키는 _UnhandledKeyInput이 셸 단축키로 넘긴다.
    /// </summary>
    public override void _GuiInput(InputEvent e)
    {
        float s = CubeApp.Instance.UiScale;
        switch (e)
        {
            case InputEventMouseButton mb:
                if (mb.Pressed) GrabFocus();
                if (HandlePie(mb)) { AcceptEvent(); return; }
                // Ctrl+휠: 브러시 반지름 ×1.15 / ÷1.15 (5~500).
                if (mb.Pressed && mb.CtrlPressed && IsBrushTool && mb.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
                {
                    var o = _shell.BrushOptions; o.Set("radius", Math.Clamp(o.Float("radius") * (mb.ButtonIndex == MouseButton.WheelUp ? 1.15f : 1 / 1.15f), 5f, 500f)); QueueRedraw(); AcceptEvent(); return;
                }
                if (mb.Pressed && mb.ButtonIndex == MouseButton.WheelUp) { ZoomAt(mb.Position, 1.1f); AcceptEvent(); return; }
                if (mb.Pressed && mb.ButtonIndex == MouseButton.WheelDown) { ZoomAt(mb.Position, 1 / 1.1f); AcceptEvent(); return; }
                // Alt+버튼 = 내비게이션(MMB 팬, RMB 줌). 누른 버튼을 기억해 같은 버튼을 뗄 때 끝낸다.
                if (mb.Pressed && mb.AltPressed && mb.ButtonIndex is MouseButton.Middle or MouseButton.Right or MouseButton.Left && _navButton == MouseButton.None)
                { _navButton = mb.ButtonIndex; _last = mb.Position; AcceptEvent(); return; }
                if (!mb.Pressed && mb.ButtonIndex == _navButton) { _navButton = MouseButton.None; AcceptEvent(); return; }
                // LMB 누름: 캔버스 툴 → 조작기 핸들 → 일반 선택 클릭 순으로 시도.
                if (mb.ButtonIndex == MouseButton.Left)
                {
                    if (mb.Pressed)
                    {
                        if (_tool != UvCanvasTool.None && BeginToolPress(mb)) { AcceptEvent(); return; }
                        var part = HitGizmo(mb.Position);
                        if (part != Part.None && TryBeginTransform(mb.Position, part)) { AcceptEvent(); return; }
                        // 더블클릭 = 집은 UV/엣지/면이 속한 섬(셸) 전체 선택(v0.0.70; 뷰포트의 더블클릭 루프/셸 선택과 같은 자리)
                        if (mb.DoubleClick && _tool == UvCanvasTool.None && DoubleClickSelectIsland(mb)) { AcceptEvent(); return; }
                        _pressed = true; _marquee = false; _pressPos = mb.Position; _modifier = ModifierOf(mb);
                        AcceptEvent(); return;
                    }
                    // LMB 뗌: 진행 중인 작업(칠하기/브러시/변형/선택) 중 하나를 마무리한다.
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
                    // 팬은 이동량만큼 원점 이동, 줌은 누른 위치를 중심으로 (dx − dy)에 지수 비례.
                    var d = mm.Position - _last; _last = mm.Position;
                    if (_navButton == MouseButton.Middle) _origin += d;
                    else if (_navButton == MouseButton.Right) ZoomAt(_pressPos == GVec2.Zero ? Size / 2 : _pressPos, MathF.Exp((d.X - d.Y) * 0.004f));
                    QueueRedraw(); AcceptEvent(); return;
                }
                if (IsBrushTool) { _brushPos = mm.Position; QueueOverlayRedraw(); }
                if (_cutSewPainting) { CutSewAt(mm.Position); AcceptEvent(); return; }
                if (_brushing) { ApplyBrush(mm.Position); AcceptEvent(); return; }
                if (_dragging) { UpdateTransform(mm.Position); AcceptEvent(); return; }
                if (_pressed)
                {
                    if (!_marquee && (mm.Position - _pressPos).Length() >= 4 * s) _marquee = true;
                    if (_marquee) { _marqueeEnd = mm.Position; QueueOverlayRedraw(); }
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

    /// <summary>해제 직전(Predelete)에는 이벤트 구독과 렌더링 서버 메시를 정리하고, 마우스가 나가면 호버·브러시 커서를 지운다.</summary>
    public override void _Notification(int what)
    {
        if (what == (int)NotificationPredelete) { Unsubscribe(); FreeGeometry(); return; }
        if (what == NotificationMouseExit) { if (_hover != null || _hoverPart != Part.None || _brushPos != null) { _hover = null; _hoverPart = Part.None; _brushPos = null; QueueOverlayRedraw(); } }
    }

    /// <summary>호버 갱신: 조작기 핸들이 우선이고, 아니면 피킹 결과를 호버로. 바뀌었을 때만 덧그림을 다시 그린다.</summary>
    private void UpdateHover(GVec2 px)
    {
        var part = HitGizmo(px);
        var hit = part == Part.None ? Pick(px) : null;
        if (part != _hoverPart || !Nullable.Equals(hit, _hover)) { _hoverPart = part; _hover = hit; QueueOverlayRedraw(); }
    }

    /// <summary>
    /// 캔버스가 처리하지 않은 키를 셸 단축키 라우터로 넘긴다(임베디드/플로팅 상태에서 루트 _Input을 받지 못하는 경우 대비).
    /// 처리되었으면 이 뷰포트에서도 처리됨으로 표시한다.
    /// </summary>
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
            // RMB 누름: 열린 sticky 서브 파이는 닫고, Alt(줌)·드래그 중이면 열지 않는다. Ctrl = 선택 파이, Shift = 편집 파이, 기본 = 모드 파이.
            if (mb.Pressed)
            {
                if (_pie.IsOpen && _pie.Sticky) { _pie.Close(); QueueRedraw(); return true; }
                if (mb.AltPressed || _pie.IsOpen || _dragging) return _pie.IsOpen;
                var items = mb.CtrlPressed ? PieMenus.UvSelectMenu(_shell) : mb.ShiftPressed ? PieMenus.UvMenu(_shell) : PieMenus.UvModeMenu(_shell, IslandMode);
                _pie.Open(items, mb.Position);
                return _pie.IsOpen;
            }
            // RMB 뗌: sticky가 아니면 고른 항목 실행.
            if (_pie.IsOpen)
            {
                if (_pie.Sticky) return true;
                ExecutePie(_pie.Release());
                return true;
            }
            return false;
        }
        // sticky 서브 파이는 LMB 클릭으로 고른다.
        if (_pie.IsOpen && _pie.Sticky && mb.ButtonIndex == MouseButton.Left && mb.Pressed) { ExecutePie(_pie.Release()); return true; }
        return _pie.IsOpen;
    }

    /// <summary>
    /// 고른 파이 항목 실행: 비활성/없음이면 무시, 서브 파이 생성기가 있으면 그 자리에 sticky 서브 파이를 열고, 아니면 직접 실행(Run) 또는 액션 호출.
    /// </summary>
    private void ExecutePie(PieItem? chosen)
    {
        if (chosen == null || !chosen.Enabled) { QueueRedraw(); return; }
        if (chosen.Sub != null) { _pie.Open(chosen.Sub(), _pie.Center, sticky: true, title: chosen.Label); return; }
        if (chosen.Run != null) chosen.Run(); else _shell.Actions.Invoke(chosen.ActionId);
        QueueRedraw();
    }

    /// <summary>커서 아래 UV가 제자리에 있도록 줌(20~20000 px/단위)을 바꾸고 원점을 보정한다.</summary>
    private void ZoomAt(GVec2 px, float factor)
    {
        var uv = PxToUv(px);
        _zoom = Math.Clamp(_zoom * factor, 20f, 20000f);
        _origin = px - new GVec2(uv.X, -uv.Y) * _zoom;
        QueueRedraw();
    }

    /// <summary>마우스 수식어 → 선택 수식어(Maya 규칙: Ctrl+Shift 추가, Ctrl 제거, Shift 토글, 없음 교체).</summary>
    private static SelectModifier ModifierOf(InputEventWithModifiers e)
        => e.CtrlPressed && e.ShiftPressed ? SelectModifier.Add : e.CtrlPressed ? SelectModifier.Remove : e.ShiftPressed ? SelectModifier.Toggle : SelectModifier.Replace;

    // ---------------------------------------------------------------- 선택

    // 피킹 도우미는 모두 관리 코드(엔진 Geometry2D 호출·면마다 배열 할당은 마우스 이동마다 수만 번 불려 느렸다)

    /// <summary>점 p와 선분 ab 사이 최단 거리(px).</summary>
    private static float SegDist(GVec2 p, GVec2 a, GVec2 b)
    {
        var ab = b - a; float l2 = ab.LengthSquared();
        float t = l2 > 0 ? Math.Clamp((p - a).Dot(ab) / l2, 0f, 1f) : 0f;
        return (a + ab * t).DistanceTo(p);
    }

    /// <summary>UV 좌표가 면의 UV 다각형 안에 있는지(짝홀 규칙).</summary>
    private static bool FaceContains(PolyMesh m, int f, NVec2 uv)
    {
        var hes = CollectionsMarshal.AsSpan(m.Hes);
        bool inside = false;
        int start = m.Faces[f].HalfEdge, he = start;
        do
        {
            int nx = hes[he].Next;
            var a = hes[he].Uv0; var b = hes[nx].Uv0;
            if ((a.Y > uv.Y) != (b.Y > uv.Y) && uv.X < (b.X - a.X) * (uv.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
            he = nx;
        } while (he != start);
        return inside;
    }

    /// <param name="topo">대상 UV 위상.</param>
    /// <param name="px">커서 위치(px).</param>
    /// <param name="maxPx">허용 거리(px).</param>
    /// <param name="best">지금까지의 최단 거리(여러 노드에 걸쳐 갱신된다).</param>
    /// <summary>best보다 가깝고 maxPx 이내인 가장 가까운 UV 점(없으면 -1). UV 공간 상자 검사로 먼 점은 바로 건너뛴다.</summary>
    private int NearestPoint(UvTopology topo, GVec2 px, float maxPx, ref float best)
    {
        var uv = PxToUv(px); float r = maxPx / _zoom; int hit = -1;
        var pts = CollectionsMarshal.AsSpan(topo.Points);
        for (int i = 0; i < pts.Length; i++)
        {
            var dv = pts[i].Uv - uv;
            if (MathF.Abs(dv.X) > r || MathF.Abs(dv.Y) > r) continue;
            float d = dv.Length() * _zoom;
            if (d <= maxPx && d < best) { best = d; hit = i; }
        }
        return hit;
    }

    /// <summary>best보다 가깝고 maxPx 이내인 가장 가까운 엣지와 그 하프에지(없으면 -1, -1).</summary>
    private (int edge, int he) NearestEdge(PolyMesh m, GVec2 px, float maxPx, ref float best)
    {
        // 엣지의 두 하프에지 방향 UV 선분을 각각 검사한다(심이면 두 선분이 다른 위치).
        var uv = PxToUv(px); float r = maxPx / _zoom; int hitE = -1, hitH = -1;
        var hes = CollectionsMarshal.AsSpan(m.Hes); var edges = CollectionsMarshal.AsSpan(m.Edges);
        for (int e = 0; e < edges.Length; e++)
        {
            ref readonly var ed = ref edges[e]; if (!ed.Alive) continue;
            for (int k = 0; k < 2; k++)
            {
                int he = k == 0 ? ed.He0 : ed.He1;
                if (he < 0) continue;
                var a = hes[he].Uv0; var b = hes[hes[he].Next].Uv0;
                if (MathF.Min(a.X, b.X) - r > uv.X || MathF.Max(a.X, b.X) + r < uv.X || MathF.Min(a.Y, b.Y) - r > uv.Y || MathF.Max(a.Y, b.Y) + r < uv.Y) continue;
                var ab = b - a; float l2 = ab.LengthSquared();
                float t = l2 > 0 ? Math.Clamp(NVec2.Dot(uv - a, ab) / l2, 0f, 1f) : 0f;
                float d = NVec2.Distance(a + ab * t, uv) * _zoom;
                if (d <= maxPx && d < best) { best = d; hitE = e; hitH = he; }
            }
        }
        return (hitE, hitH);
    }

    /// <summary>커서 아래 면(첫 번째, 없으면 -1).</summary>
    private int FaceAt(PolyMesh m, GVec2 px)
    {
        var uv = PxToUv(px);
        var faces = CollectionsMarshal.AsSpan(m.Faces);
        for (int f = 0; f < faces.Length; f++) if (faces[f].Alive && FaceContains(m, f, uv)) return f;
        return -1;
    }

    /// <summary>면의 UV 중심(꼭짓점 평균)을 px로(마키 면 선택 판정용).</summary>
    private GVec2 FaceCenterPx(PolyMesh m, int f)
    {
        var c = NVec2.Zero; int n = 0;
        int start = m.Faces[f].HalfEdge, he = start;
        do { c += m.Hes[he].Uv0; n++; he = m.Hes[he].Next; } while (he != start);
        return UvToPx(c / n);
    }

    /// <summary>
    /// Island 모드 피킹: 점(8px) → 엣지(6px, 그 하프에지의 UV 점) → 면 내부(첫 하프에지의 UV 점) 순으로 섬을 대표할 UV 점을 고른다.
    /// </summary>
    private SelItem? PickIsland(GVec2 px, SceneNode node, ref float best)
    {
        float s = CubeApp.Instance.UiScale;
        var m = node.Mesh!; var topo = Topo(node);
        int pi = NearestPoint(topo, px, 8 * s, ref best);
        if (pi >= 0) return new SelItem(node.Id, pi);
        var (_, ehe) = NearestEdge(m, px, 6 * s, ref best);
        if (ehe >= 0) return new SelItem(node.Id, topo.HeToPoint[ehe]);
        int f = FaceAt(m, px);
        if (f >= 0) { best = 0; return new SelItem(node.Id, topo.HeToPoint[m.Faces[f].HalfEdge]); }
        return null;
    }

    /// <summary>커서 아래 UV 점(모드 무관, 8px).</summary>
    private (SceneNode node, int point)? PickPointAny(GVec2 px)
    {
        float s = CubeApp.Instance.UiScale; float best = 8 * s; (SceneNode, int)? hit = null;
        foreach (var node in TargetNodes())
        {
            int i = NearestPoint(Topo(node), px, 8 * s, ref best);
            if (i >= 0) hit = (node, i);
        }
        return hit;
    }

    /// <summary>커서 아래 엣지(모드 무관, 6px). Cut/Sew 툴용.</summary>
    private (SceneNode node, int edge)? PickEdgeAny(GVec2 px)
    {
        float s = CubeApp.Instance.UiScale; float best = 6 * s; (SceneNode, int)? hit = null;
        foreach (var node in TargetNodes())
        {
            var (e, _) = NearestEdge(node.Mesh!, px, 6 * s, ref best);
            if (e >= 0) hit = (node, e);
        }
        return hit;
    }

    /// <summary>
    /// 현재 모드에 맞는 피킹: UV = 가장 가까운 점, Island = 섬 대표 점, Edge = 가장 가까운 엣지, Face/Object = 커서 아래 면(Object면 Component -1).
    /// 여러 노드에 걸쳐 가장 가까운 것을 고른다(best 공유).
    /// </summary>
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
                    { int i = NearestPoint(topo, px, 8 * s, ref best); if (i >= 0) hit = new SelItem(node.Id, i); }
                    break;
                case SelectMode.Edge:
                    { var (e, _) = NearestEdge(m, px, 6 * s, ref best); if (e >= 0) hit = new SelItem(node.Id, e); }
                    break;
                case SelectMode.Face:
                case SelectMode.Object:
                    { int f = FaceAt(m, px); if (f >= 0) { hit = new SelItem(node.Id, sel.Mode == SelectMode.Face ? f : -1); best = 0; } }
                    break;
            }
        }
        return hit;
    }

    /// <summary>Island 모드면 항목을 그 UV 섬의 모든 점으로 펼치고, 아니면 그대로 돌려준다.</summary>
    private IEnumerable<SelItem> ExpandIsland(SelItem item)
    {
        if (!IslandMode) { yield return item; yield break; }
        var node = _shell.Document.Find(item.Node); if (node?.Mesh == null) yield break;
        var topo = Topo(node);
        int shell = topo.Points[item.Component].Shell;
        foreach (int p in topo.PointsInShell(shell)) yield return new SelItem(item.Node, p);
    }

    /// <summary>
    /// 더블클릭 섬 선택: 커서 아래 UV 점/엣지/면이 속한 UV 섬 전체(UV 모드 = 섬의 모든 UV 점, 엣지 모드 = 섬의 모든 엣지, 면 모드 = 섬의 모든 면)를
    /// 선택한다. 첫 클릭이 이미 그 요소를 토글했으므로 Shift는 토글이 아니라 추가로, Ctrl은 제거, Ctrl+Shift는 추가로 적용한다(Maya 더블클릭 루프 선택과 같음).
    /// </summary>
    /// <returns>섬을 선택했으면 true(아무것도 집지 못했거나 오브젝트/Island 모드면 false → 일반 클릭 처리).</returns>
    private bool DoubleClickSelectIsland(InputEventMouseButton mb)
    {
        var sel = _shell.Document.Selection;
        if (sel.Mode is not (SelectMode.Uv or SelectMode.Edge or SelectMode.Face) || (sel.Mode == SelectMode.Uv && IslandMode)) return false;
        var hit = Pick(mb.Position); if (hit == null || hit.Value.Component < 0) return false;
        var node = _shell.Document.Find(hit.Value.Node); var m = node?.Mesh; if (node == null || m == null) return false;
        var topo = Topo(node);
        int shell = sel.Mode switch
        {
            SelectMode.Uv => topo.Points[hit.Value.Component].Shell,
            SelectMode.Edge => topo.HeToPoint[m.Edges[hit.Value.Component].He0] is var p0 && p0 >= 0 ? topo.Points[p0].Shell : -1,
            _ => topo.HeToPoint[m.Faces[hit.Value.Component].HalfEdge] is var p1 && p1 >= 0 ? topo.Points[p1].Shell : -1,
        };
        if (shell < 0) return false;
        var items = new List<SelItem>();
        switch (sel.Mode)
        {
            case SelectMode.Uv: foreach (int p in topo.PointsInShell(shell)) items.Add(new SelItem(node.Id, p)); break;
            case SelectMode.Edge:
                for (int e = 0; e < m.EdgeCount; e++)
                {
                    var ed = m.Edges[e]; if (!ed.Alive) continue;
                    // 엣지의 어느 한 하프에지라도 이 섬의 점에 닿으면 섬의 엣지(심 엣지는 양쪽 섬에 모두 속한다)
                    bool inShell = false;
                    for (int k = 0; k < 2 && !inShell; k++) { int he = k == 0 ? ed.He0 : ed.He1; if (he < 0) continue; int p = topo.HeToPoint[he]; if (p >= 0 && topo.Points[p].Shell == shell) inShell = true; }
                    if (inShell) items.Add(new SelItem(node.Id, e));
                }
                break;
            default: foreach (int f in UvOps.ShellFaces(m, topo, shell)) items.Add(new SelItem(node.Id, f)); break;
        }
        var mod = ModifierOf(mb);
        if (mod == SelectModifier.Toggle) mod = SelectModifier.Add;
        _shell.RecordUvSelection(ss => ss.Apply(items, mod));
        _pressed = false; _marquee = false;
        QueueRedraw();
        return true;
    }

    /// <summary>
    /// 클릭 선택: 피킹 결과(UV 모드면 섬 펼치기)를 수식어와 함께 셸의 선택 기록 경로로 적용한다(Undo 가능).
    /// </summary>
    private void ClickSelect(GVec2 px)
    {
        var sel = _shell.Document.Selection;
        var hit = Pick(px);
        var items = hit != null ? (sel.Mode == SelectMode.Uv ? ExpandIsland(hit.Value).ToArray() : new[] { hit.Value }) : Array.Empty<SelItem>();
        // 빈 곳 클릭은 교체 모드일 때만 선택 해제로 기록한다. 오브젝트 모드에서는 빈 곳 클릭을 무시한다.
        if (items.Length == 0 && _modifier != SelectModifier.Replace) return;
        if (sel.Mode == SelectMode.Object && items.Length == 0) return;
        var mod = _modifier;
        _shell.RecordUvSelection(ss => ss.Apply(items, mod)); // UV Symmetry가 켜져 있으면 UV 거울 짝까지(ShellUvSymmetry.cs)
    }

    /// <summary>
    /// 마키 선택 확정. 모드별 포함 규칙: UV = 사각형 안의 점(Island면 점·면 중심·사각형과 교차하는 면 테두리로 섬 전체),
    /// Edge = 어느 한 끝점이 안에 있는 엣지(양쪽 하프에지 UV 기준), Face = 면 UV 중심이 안에 있는 면. 오브젝트 모드는 무시.
    /// </summary>
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
                        // Island: 면 중심이 안에 있거나 면 테두리가 사각형과 교차하면 그 면의 섬도 포함한다(점이 하나도 안에 없는 큰 섬 대응).
                        if (IslandMode)
                            for (int f = 0; f < m.FaceCount; f++)
                            {
                                if (!m.Faces[f].Alive) continue;
                                bool inside = r.HasPoint(FaceCenterPx(m, f));
                                if (!inside)
                                {
                                    int start = m.Faces[f].HalfEdge, he = start;
                                    do { int nx = m.Hes[he].Next; if (RectIntersectsSegment(r, UvToPx(m.Hes[he].Uv0), UvToPx(m.Hes[nx].Uv0))) { inside = true; break; } he = nx; } while (he != start);
                                }
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
                        if (r.HasPoint(FaceCenterPx(m, f))) items.Add(new SelItem(node.Id, f));
                    }
                    break;
            }
        }
        if (items.Count == 0 && _modifier != SelectModifier.Replace) return;
        if (sel.Mode == SelectMode.Object) return;
        var mod = _modifier;
        _shell.RecordUvSelection(ss => ss.Apply(items, mod));
    }

    // ---------------------------------------------------------------- 변형 (조작기 드래그)

    /// <summary>
    /// 조작기 핸들 드래그 시작: 셸의 현재 툴이 Move/Rotate/Scale일 때만, 선택 UV 점을 캡처하고 드래그 상태로 들어간다.
    /// </summary>
    /// <returns>시작했으면 true(캡처할 점이 없으면 false).</returns>
    private bool TryBeginTransform(GVec2 px, Part part)
    {
        var tool = _shell.Tools.Current?.Id ?? "select";
        if (tool is not ("move" or "rotate" or "scale")) return false;
        if (!CaptureSelectionForTransform(tool switch { "move" => "Move UVs", "rotate" => "Rotate UVs", _ => "Scale UVs" }, null)) return false;
        _xformTool = tool; _pressPos = px; _dragging = true; _dragPart = part;
        return true;
    }

    /// <summary>현재 선택(또는 지정 점)을 변형 대상으로 캡처한다.</summary>
    /// <param name="name">Undo 항목 이름.</param>
    /// <param name="pointsOf">노드 → 움직일 UV 점 집합(null이면 현재 선택). 핀 점은 Tweak 툴이 아니면 제외한다.</param>
    /// <returns>캡처한 점이 하나라도 있으면 true.</returns>
    private bool CaptureSelectionForTransform(string name, Func<SceneNode, HashSet<int>>? pointsOf)
    {
        _xform.Clear();
        _xformPlane = _shell.UvSymmetryPlane;
        int count = 0;
        foreach (var node in TargetNodes())
        {
            var pts = pointsOf?.Invoke(node) ?? SelectedPoints(node);
            if (pts.Count == 0) continue;
            var topo = Topo(node);
            // UV Symmetry: 집은 점의 거울 짝도 함께 움직인다(Tweak/Move Shell처럼 선택과 무관하게 집은 점도 포함)
            if (_xformPlane != null) { var map = UvSymmetryMap.Build(topo, _xformPlane); var ext = new HashSet<int>(pts); foreach (int q in pts) { int mq = map.MirrorPoint(q); if (mq >= 0) ext.Add(mq); } pts = ext; }
            var ids = pts.Where(i => !topo.Points[i].Pinned || _tool == UvCanvasTool.Tweak).ToArray();
            if (ids.Length == 0) continue;
            var init = ids.Select(i => topo.Points[i].Uv).ToArray();
            count += ids.Length;
            // 노드마다 UvEditCommand를 만들어 시작 상태를 캡처해 둔다(놓을 때 Commit).
            var cmd = new UvEditCommand(name, node.Id);
            cmd.Capture(_shell.Document);
            _xform.Add((node.Id, topo, ids, init, cmd));
        }
        return count > 0;
    }

    /// <summary>Pixel Snap이 켜져 있으면 이동량을 텍스처 1픽셀(1/해상도 UV) 단위로 반올림한다.</summary>
    private NVec2 SnapDelta(NVec2 delta)
    {
        if (!_pixelSnap) return delta;
        float px = 1f / ImagePixels();
        return new NVec2(MathF.Round(delta.X / px) * px, MathF.Round(delta.Y / px) * px);
    }

    /// <summary>
    /// 변형 드래그 갱신: 누른 위치부터의 마우스 이동으로 변환 행렬을 만들어 모든 대상 점의 "시작 UV"에 적용한다.
    /// 이동(move/tweak/shell) = px/줌 이동(축 핸들이면 한 축만, X 홀드 = 1/8 그리드 스냅, Pixel Snap),
    /// 회전 = 피벗 중심 각도 차(J 홀드 = 회전 스냅 각), 스케일 = 축 핸들 길이 비율 또는 중앙(dx − dy) 균등(J 홀드 = 스케일 스냅).
    /// </summary>
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
                    // 화면 Y는 아래가 +이므로 V는 부호를 뒤집는다.
                    var delta = new NVec2(d.X / _zoom, -d.Y / _zoom);
                    if (_dragPart == Part.X) delta.Y = 0; else if (_dragPart == Part.Y) delta.X = 0;
                    if (_shell.Viewport.IsGridSnapHeld) { float g = 1f / 8f; delta = new NVec2(MathF.Round(delta.X / g) * g, MathF.Round(delta.Y / g) * g); }
                    xf = Matrix3x2.CreateTranslation(SnapDelta(delta)); break;
                }
            case "rotate":
                {
                    float a0 = MathF.Atan2(_pressPos.Y - c.Y, _pressPos.X - c.X), a1 = MathF.Atan2(px.Y - c.Y, px.X - c.X);
                    // 화면 각도와 UV 각도는 Y 뒤집기 때문에 부호가 반대.
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
        // 시작 UV × 변환을 각 점에 쓰고 메시 속성 변경을 통지한다(Undo 기록은 놓을 때).
        // UV Symmetry: 피벗(집은 점/선택 중심)이 있는 쪽이 드래그를 그대로 따르고 반대쪽은 거울 변형, 축선 위 점은 축선에 남는다.
        var plane = _xformPlane; bool positive = plane == null || plane.Signed(_pivotUv) >= -plane.Tolerance;
        foreach (var (id, topo, ids, init, _) in _xform)
        {
            var mesh = _shell.Document.Get(id).Mesh!;
            for (int i = 0; i < ids.Length; i++) UvOps.SetPointUv(mesh, topo, ids[i], plane == null ? NVec2.Transform(init[i], xf) : UvSymmetryOps.Transform(init[i], xf, plane, positive));
            _shell.Document.Notify(new DocChange(ChangeKind.MeshAttributes, id));
        }
        QueueRedraw();
    }

    /// <summary>
    /// 변형 드래그 끝. commit이면 노드별 명령을 Commit해 바뀐 것만 한 Undo 그룹으로 넣고, 취소면 모든 점을 시작 UV로 되돌린다.
    /// </summary>
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

    /// <summary>
    /// 캔버스 툴의 LMB 누름 처리. Tweak = 커서 아래 점(선택에 속하면 선택 전체) 드래그 시작, Move Shell = 커서 아래 점/면의 셸 드래그 시작,
    /// Cut/Sew = 칠하기 시작(Ctrl = 꿰매기), 브러시 = 대상 노드의 모든(핀 제외; PinBrush는 전부) 점을 캡처하고 첫 스탬프 적용.
    /// </summary>
    /// <returns>툴이 누름을 처리했으면 true(아니면 일반 선택으로 넘어간다).</returns>
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

    /// <summary>PinBrush에서 Ctrl로 시작했으면 핀 해제 모드.</summary>
    private bool _brushUnpin;

    /// <summary>커서 아래 면의 첫 하프에지 UV 점(점을 직접 집지 못했을 때 Move Shell 대상 찾기).</summary>
    private (SceneNode node, int point)? PickFacePoint(GVec2 px)
    {
        var uv = PxToUv(px);
        foreach (var node in TargetNodes())
        {
            var m = node.Mesh!; var topo = Topo(node);
            for (int f = 0; f < m.FaceCount; f++)
                if (m.Faces[f].Alive && FaceContains(m, f, uv)) return (node, topo.HeToPoint[m.Faces[f].HalfEdge]);
        }
        return null;
    }

    /// <summary>
    /// 브러시 스탬프 적용: 반지름 안의 점에 smoothstep 감쇠 가중치 w를 주어 툴별로 움직인다.
    /// Grab = 직전 위치부터의 이동량 × w, Smear = 이동량 × w × 세기, Pinch = 브러시 중심 쪽으로 당김, Smooth = 이웃(셸 안 엣지로 이어진) UV 점 평균 쪽으로,
    /// PinBrush = 핀 설정/해제(코너 PinUv와 위상 캐시 둘 다).
    /// </summary>
    private void ApplyBrush(GVec2 px)
    {
        var deltaUv = new NVec2((px.X - _brushLast.X) / _zoom, -(px.Y - _brushLast.Y) / _zoom);
        _brushLast = px;
        BrushStamp(px, deltaUv, PxToUv(px));
        // UV Symmetry: 거울 위치에도 같은 스탬프(이동량은 축 성분 반전)
        if (_shell.UvSymmetryPlane is { } sp)
        {
            var mc = sp.Reflect(PxToUv(px));
            var md = sp.Axis == 0 ? new NVec2(-deltaUv.X, deltaUv.Y) : new NVec2(deltaUv.X, -deltaUv.Y);
            BrushStamp(UvToPx(mc), md, mc);
        }
        QueueRedraw();
    }

    /// <summary>브러시 스탬프 하나(중심 px, UV 이동량, 중심 UV)를 모든 대상에 적용한다.</summary>
    private void BrushStamp(GVec2 px, NVec2 deltaUv, NVec2 centerUv)
    {
        float r = BrushRadius; float strength = BrushStrength;
        foreach (var (id, topo, ids, _, _) in _xform)
        {
            var mesh = _shell.Document.Get(id).Mesh!;
            bool any = false;
            foreach (int i in ids)
            {
                var uv = topo.Points[i].Uv;
                float d = UvToPx(uv).DistanceTo(px);
                if (d > r) continue;
                // smoothstep(1 − d/r): 가장자리에서 부드럽게 0.
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
    }

    /// <summary>
    /// 브러시 스트로크 끝: 노드별 명령을 Commit해 바뀐 것만 한 Undo 그룹으로 넣고, 핀 등이 바뀌었을 수 있으므로 위상 캐시를 버린다.
    /// </summary>
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

    /// <summary>
    /// Cut/Sew 칠하기: 커서 아래 엣지를 한 스트로크에 한 번만, 경계가 아니고 아직 원하는 상태가 아닐 때(자르기 = 심 아님, 꿰매기 = 심) 처리한다.
    /// 엣지마다 Undo 가능한 <see cref="UvEditCommand"/>를 넣고 그 노드의 위상 캐시를 버린다.
    /// </summary>
    private void CutSewAt(GVec2 px)
    {
        // UV Symmetry: 거울 위치의 엣지도 같은 스트로크에서 처리(재귀 1단계)
        if (!_cutSewMirroring && _shell.UvSymmetryPlane is { } sp)
        {
            _cutSewMirroring = true;
            try { CutSewAt(UvToPx(sp.Reflect(PxToUv(px)))); } finally { _cutSewMirroring = false; }
        }
        var hit = PickEdgeAny(px); if (hit == null) return;
        var (node, edge) = hit.Value;
        if (!_cutSewDone.Add((node.Id, edge))) return;
        var m = node.Mesh!;
        // 이미 그 상태면 건너뛴다: Cut = 이미 심, Sew = 이미 이어짐(심 플래그 없이 UV만 갈라진 엣지도 꿰맨다)
        if (m.Edges[edge].He1 < 0 || (_cutSewSew ? UvOps.IsEdgeSewn(m, edge) : m.Edges[edge].Seam)) return;
        bool sew = _cutSewSew; int e = edge;
        _shell.Document.Undo.Push(new UvEditCommand(sew ? "Sew UV Edge" : "Cut UV Edge", node.Id, mm => { if (sew) UvOps.SewEdges(mm, new[] { e }); else UvOps.CutEdges(mm, new[] { e }); }));
        _topos.Remove(node.Id);
        QueueRedraw();
    }

    // ---------------------------------------------------------------- 스냅샷

    /// <summary>현재 캔버스 영역을 PNG로 저장한다(UV Snapshot).</summary>
    public Error SaveSnapshot(string path)
    {
        // 화면 전체 이미지를 읽어 캔버스의 전역 사각형 부분만 잘라 저장한다(오버레이·조작기 포함 화면 그대로).
        var img = GetViewport().GetTexture().GetImage();
        var rect = GetGlobalRect();
        var region = img.GetRegion(new Rect2I((int)rect.Position.X, (int)rect.Position.Y, (int)rect.Size.X, (int)rect.Size.Y));
        return region.SavePng(path);
    }
}
