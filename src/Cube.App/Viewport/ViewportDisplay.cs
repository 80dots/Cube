using Cube.Core.Scene;
using Cube.Core.Selection;
using Godot;

namespace Cube.App.Viewport;

/// <summary>
/// 뷰포트 셰이딩 모드. 값은 Maya 숫자 키와 같다: Wireframe = 4, Shaded = 5(Smooth Shade All, 텍스처 없음),
/// Textured = 6(텍스처 표시), Lit = 7(Use All Lights, 씬 라이트), UvGrid = 9(UV 체커 텍스처).
/// </summary>
public enum ShadingMode { Wireframe = 4, Shaded = 5, Textured = 6, Lit = 7, UvGrid = 9 }

/// <summary>
/// 뷰포트 표시 상태(셰이딩 모드, 와이어 on 셰이디드, 그리드)와 선택 상태를 MeshView 스타일로 변환한다.
/// 선택/호버 변경 시 <see cref="RefreshAll"/>을 호출한다.
/// </summary>
/// <remarks>
/// 패널마다 하나 있다. 문서 선택이 바뀌거나 모드가 바뀌면 모든 MeshView에 <see cref="ApplyStyle"/>을 적용하고,
/// 새 MeshView가 만들어지거나 스킨/머티리얼이 바뀔 때도 SceneView 이벤트로 그 뷰에만 적용한다.
/// 컴포넌트 색은 람다(EdgeColor/VertexColor/FaceSelected)로 넘겨 MeshView가 업로드할 때 요소마다 조회한다.
/// </remarks>
public sealed class ViewportDisplay
{
    /// <summary>이 표시 상태가 속한 패널(SceneView·그리드·헤드라이트 접근).</summary>
    private readonly ViewportPanel _panel;
    /// <summary>바인드된 문서(선택 상태·머티리얼 조회).</summary>
    private Document? _doc;

    /// <summary>현재 셰이딩 모드. <see cref="SetMode"/>로만 바꾼다.</summary>
    public ShadingMode Mode { get; private set; } = ShadingMode.Shaded;
    /// <summary>셰이딩 위에 와이어를 함께 그릴지(Wireframe on Shaded, 기본 OFF).</summary>
    public bool WireOnShaded { get; set; }
    /// <summary>패널 그리드 표시 여부(그리드 노드 Visible에 직접 연결).</summary>
    public bool ShowGrid { get => _panel.Grid.Visible; set => _panel.Grid.Visible = value; }

    /// <summary>호버 프리셀렉션(노드, 모드, 컴포넌트 id). Picker가 갱신한다.</summary>
    public (NodeId node, SelectMode mode, int id)? Hover;

    /// <summary>셰이딩 모드가 바뀌었을 때(HUD 버튼 상태 갱신용).</summary>
    public event Action? ModeChanged;

    /// <summary>패널을 받아 만들고, SceneView의 새 MeshView/스타일 재요청 이벤트에 <see cref="ApplyStyle"/>을 연결한다.</summary>
    public ViewportDisplay(ViewportPanel panel)
    {
        _panel = panel;
        panel.Scene.MeshViewCreated += ApplyStyle;
        panel.Scene.StyleRefreshRequested += ApplyStyle;
    }

    /// <summary>조인트 색: 활성 초록, 선택 흰색, 호버 밝은 회색, 그 외 파랑.</summary>
    public void ApplyJointStyles()
    {
        // 조인트: 활성 > 선택 > 호버(선택 안 된 것만) > 기본 순으로 색 결정
        var sel = _doc?.Selection;
        foreach (var (id, jv) in _panel.Scene.JointViews)
        {
            bool selected = sel != null && sel.IsObjectSelected(id);
            bool active = sel != null && sel.ActiveObject == id;
            bool hovered = !selected && Hover is { } h && h.node == id && h.mode == SelectMode.Object;
            jv.SetColor(active ? JointView.JointActive : selected ? JointView.JointSelected : hovered ? JointView.JointHover : JointView.JointNormal);
        }
        // 라이트 아이콘: 활성 > 선택 > 기본
        foreach (var (id, lv) in _panel.Scene.LightViews)
        {
            bool selected = sel != null && sel.IsObjectSelected(id);
            bool active = sel != null && sel.ActiveObject == id;
            lv.SetColor(active ? LightView.IconActive : selected ? LightView.IconSelected : LightView.IconNormal);
        }
        // 이미지 플레인 테두리: 활성 > 선택 > 기본
        foreach (var (id, ipv) in _panel.Scene.ImagePlaneViews)
        {
            bool selected = sel != null && sel.IsObjectSelected(id);
            bool active = sel != null && sel.ActiveObject == id;
            ipv.SetColor(active ? ImagePlaneView.BorderActive : selected ? ImagePlaneView.BorderSelected : ImagePlaneView.BorderNormal);
        }
    }

    /// <summary>문서에 연결한다(이전 문서의 선택 이벤트 해제 → 새 문서 선택/모드 변경 시 RefreshAll) 후 즉시 한 번 적용.</summary>
    public void Bind(Document doc)
    {
        if (_doc != null) { _doc.Selection.Changed -= RefreshAll; _doc.Selection.ModeChanged -= RefreshAll; }
        _doc = doc;
        doc.Selection.Changed += RefreshAll;
        doc.Selection.ModeChanged += RefreshAll;
        RefreshAll();
    }

    /// <summary>UV 그리드 머티리얼 지연 캐시(모든 패널 공유).</summary>
    private static ShaderMaterial? _uvGridMaterial;

    /// <summary>UV 그리드 체커 머티리얼(assets/textures/uv_grid.bin). 메시의 UV를 그대로 보여준다. 뒷면은 검정.</summary>
    public static ShaderMaterial UvGridMaterial
    {
        get
        {
            // 처음 접근할 때 내장 PNG 바이트(임포터 회피용 .bin)를 읽어 surface 셰이더에 텍스처로 넣는다
            if (_uvGridMaterial == null)
            {
                var tex = UI.Icons.LoadPng("res://assets/textures/uv_grid.bin");
                _uvGridMaterial = new ShaderMaterial { Shader = GD.Load<Shader>("res://assets/shaders/surface.gdshader") };
                _uvGridMaterial.SetShaderParameter("use_texture", tex != null);
                if (tex != null) _uvGridMaterial.SetShaderParameter("albedo_tex", tex);
            }
            return _uvGridMaterial;
        }
    }

    /// <summary>
    /// 셰이딩 모드를 바꾼다. 헤드라이트는 Lit이 아닐 때만(그리고 Render Settings에서 켜져 있을 때), 씬 라이트는 Lit일 때만 켠 뒤
    /// 모든 스타일을 다시 적용하고 <see cref="ModeChanged"/>를 알린다.
    /// </summary>
    public void SetMode(ShadingMode mode)
    {
        Mode = mode;
        _panel.HeadLight.Visible = mode != ShadingMode.Lit && CubeApp.Instance.Settings.Render.Headlight;
        // Use All Lights(7)만 씬 라이트로 비춘다. 다른 모드는 기본 조명(헤드라이트)만(Maya Default Lighting)
        _panel.Scene.SetSceneLights(mode == ShadingMode.Lit);
        RefreshAll();
        ModeChanged?.Invoke();
    }

    /// <summary>조인트·라이트 색과 모든 MeshView 스타일을 다시 적용한다(선택/호버/모드 변경 시).</summary>
    public void RefreshAll()
    {
        ApplyJointStyles();
        foreach (var mv in _panel.Scene.MeshViews.Values) ApplyStyle(mv);
    }

    /// <summary>
    /// MeshView 하나의 스타일을 현재 선택·모드·호버·셰이딩 모드로 채우고 <see cref="MeshView.RefreshStyle"/>을 부른다.
    /// </summary>
    /// <remarks>
    /// 규칙: 표면은 Wireframe이 아니면 표시, 머티리얼은 UV Grid &gt; 문서 머티리얼(Shaded면 텍스처 끔) &gt; 기본 회색.
    /// 와이어는 Wireframe 모드·Wire on Shaded·오브젝트 선택·컴포넌트 편집 대상일 때. 정점 점은 정점/UV 모드, 면 중심은 면 모드에서
    /// 컴포넌트 편집 대상에만. 엣지 색 우선순위: 호버 &gt; 선택 &gt; 크리즈 &gt; 소프트 엣지 &gt; 기본(흰색).
    /// </remarks>
    public void ApplyStyle(MeshView mv)
    {
        // 이 노드의 선택 상태: 오브젝트 선택/활성, 컴포넌트 편집 대상 여부, 컴포넌트 집합, 호버 정보
        var s = mv.Style;
        var sel = _doc?.Selection;
        var id = mv.Node.Id;
        bool objSelected = sel != null && sel.IsObjectSelected(id);
        bool active = sel != null && sel.ActiveObject == id;
        // 컴포넌트 모드여도 편집 대상(하나)이 아닌 개체는 오브젝트 모드처럼(선택 안 됨) 그린다
        bool compMode = sel != null && sel.IsComponentMode && sel.IsComponentEditable(id);
        ComponentSet? comps = null;
        if (sel != null && compMode) sel.Components.TryGetValue(id, out comps);
        var mode = sel?.Mode ?? SelectMode.Object;
        var hover = Hover is { } h && h.node == id ? h : ((NodeId, SelectMode, int)?)null;

        // 표면 표시와 머티리얼
        s.ShowSurface = Mode != ShadingMode.Wireframe;
        var matDef = _doc?.FindMaterial(mv.Node.MaterialId);
        // Smooth Shade All(5)은 텍스처 없이 머티리얼 값만, Textured(6)/Use All Lights(7)는 텍스처까지(Maya와 같음)
        s.SurfaceMaterial = Mode == ShadingMode.UvGrid ? UvGridMaterial : matDef != null ? MaterialCache.Get(matDef, textured: Mode != ShadingMode.Shaded) : null;
        mv.MappedTexture = matDef != null ? MaterialCache.LoadTexture(matDef.TexturePath) : null;
        // Paint Skin Weights 가중치 표시 대상이면 가중치 조회 함수를 넘긴다
        var wd = UI.Shell.Instance?.WeightDisplay;
        s.WeightOf = wd is { } w && w.node == id ? w.weight : null;
        // 요소 표시 여부
        s.ShowWire = Mode == ShadingMode.Wireframe || WireOnShaded || objSelected || compMode;
        s.ShowVertices = compMode && mode is SelectMode.Vertex or SelectMode.Uv;
        s.ShowFaceCenters = compMode && mode == SelectMode.Face;

        // 오브젝트 와이어 색: 활성 초록, 선택 흰색, 그 외 어두운 회색
        s.ObjectWireColor = active ? MeshView.WireActive : objSelected ? MeshView.WireSelectedObject : MeshView.WireNormal;
        var mesh = mv.Node.Mesh;

        // 컴포넌트 편집 대상: 요소별 색 람다
        if (compMode)
        {
            var baseColor = MeshView.WireSelectedObject;
            s.EdgeColor = e =>
            {
                if (mode == SelectMode.Edge && hover is { } hv && hv.Item2 == SelectMode.Edge && hv.Item3 == e) return MeshView.Hover;
                if (comps != null && comps.Edges.Contains(e) && mode == SelectMode.Edge) return MeshView.EdgeSelected;
                if (mesh != null && mesh.Edges[e].Crease > 0f) return MeshView.EdgeCrease;
                if (mesh != null && !mesh.Edges[e].Hard) return MeshView.WireSoft;
                return baseColor;
            };
            if (mode == SelectMode.Uv)
            {
                // UV 모드: 정점 위치에 UV 점(파랑). 그 정점의 UV 점 중 하나라도 선택되면 빨강
                var topo = mv.UvTopo;
                var selectedVerts = new HashSet<int>();
                if (comps != null) foreach (int p in comps.Uvs) if (p < topo.Points.Count) selectedVerts.Add(topo.Points[p].Vertex);
                s.VertexColor = v =>
                {
                    if (hover is { } hv && hv.Item2 == SelectMode.Uv && hv.Item3 == v) return MeshView.Hover;
                    return selectedVerts.Contains(v) ? MeshView.UvSelected : MeshView.UvNormal;
                };
            }
            else
            s.VertexColor = v =>
            {
                if (hover is { } hv && hv.Item2 == SelectMode.Vertex && hv.Item3 == v) return MeshView.Hover;
                return comps != null && comps.Verts.Contains(v) ? MeshView.VertexSelected : MeshView.VertexNormal;
            };
            // 면 모드: 선택 면 또는 호버 면을 틴트
            s.FaceSelected = mode == SelectMode.Face
                ? f => (comps != null && comps.Faces.Contains(f)) || (hover is { } hv && hv.Item2 == SelectMode.Face && hv.Item3 == f)
                : null;
        }
        // 오브젝트 모드(또는 편집 대상이 아닌 개체): 오브젝트 와이어 색 하나, 소프트 엣지는 선택 안 됐을 때만 연하게
        else
        {
            var oc = s.ObjectWireColor;
            // Maya: 프리셀렉션 하이라이트는 아직 선택되지 않은 오브젝트에만
            bool hovered = !objSelected && hover is { } hv && hv.Item2 == SelectMode.Object;
            s.EdgeColor = e => hovered ? MeshView.Hover : (mesh != null && !mesh.Edges[e].Hard && !objSelected ? MeshView.WireSoft : oc);
            s.VertexColor = null;
            s.FaceSelected = null;
        }
        mv.RefreshStyle();
    }
}
