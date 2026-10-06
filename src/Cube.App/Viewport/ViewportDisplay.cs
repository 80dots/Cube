using Cube.Core.Scene;
using Cube.Core.Selection;
using Godot;

namespace Cube.App.Viewport;

public enum ShadingMode { Wireframe = 4, Shaded = 5, Textured = 6, Lit = 7, UvGrid = 9 }

/// <summary>
/// 뷰포트 표시 상태(셰이딩 모드, 와이어 on 셰이디드, 그리드)와 선택 상태를 MeshView 스타일로 변환한다.
/// 선택/호버 변경 시 <see cref="RefreshAll"/>을 호출한다.
/// </summary>
public sealed class ViewportDisplay
{
    private readonly ViewportPanel _panel;
    private Document? _doc;

    public ShadingMode Mode { get; private set; } = ShadingMode.Shaded;
    public bool WireOnShaded { get; set; } = true;
    public bool ShowGrid { get => _panel.Grid.Visible; set => _panel.Grid.Visible = value; }

    /// <summary>호버 프리셀렉션(노드, 모드, 컴포넌트 id). Picker가 갱신한다.</summary>
    public (NodeId node, SelectMode mode, int id)? Hover;

    public event Action? ModeChanged;

    public ViewportDisplay(ViewportPanel panel)
    {
        _panel = panel;
        panel.Scene.MeshViewCreated += ApplyStyle;
    }

    public void Bind(Document doc)
    {
        if (_doc != null) { _doc.Selection.Changed -= RefreshAll; _doc.Selection.ModeChanged -= RefreshAll; }
        _doc = doc;
        doc.Selection.Changed += RefreshAll;
        doc.Selection.ModeChanged += RefreshAll;
        RefreshAll();
    }

    private static ShaderMaterial? _uvGridMaterial;

    /// <summary>UV 그리드 체커 머티리얼(assets/textures/uv_grid.bin). 메시의 UV를 그대로 보여준다. 뒷면은 검정.</summary>
    public static ShaderMaterial UvGridMaterial
    {
        get
        {
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

    public void SetMode(ShadingMode mode)
    {
        Mode = mode;
        _panel.HeadLight.Visible = mode != ShadingMode.Lit;
        RefreshAll();
        ModeChanged?.Invoke();
    }

    public void RefreshAll()
    {
        foreach (var mv in _panel.Scene.MeshViews.Values) ApplyStyle(mv);
    }

    public void ApplyStyle(MeshView mv)
    {
        var s = mv.Style;
        var sel = _doc?.Selection;
        var id = mv.Node.Id;
        bool objSelected = sel != null && sel.IsObjectSelected(id);
        bool active = sel != null && sel.ActiveObject == id;
        bool compMode = sel != null && sel.IsComponentMode;
        ComponentSet? comps = null;
        if (sel != null && compMode) sel.Components.TryGetValue(id, out comps);
        var mode = sel?.Mode ?? SelectMode.Object;
        var hover = Hover is { } h && h.node == id ? h : ((NodeId, SelectMode, int)?)null;

        s.ShowSurface = Mode != ShadingMode.Wireframe;
        s.SurfaceMaterial = Mode == ShadingMode.UvGrid ? UvGridMaterial : null;
        s.ShowWire = Mode == ShadingMode.Wireframe || WireOnShaded || objSelected || compMode;
        s.ShowVertices = compMode && mode == SelectMode.Vertex;
        s.ShowFaceCenters = compMode && mode == SelectMode.Face;

        // 오브젝트 와이어 색: 활성 초록, 선택 흰색, 그 외 어두운 회색
        s.ObjectWireColor = active ? MeshView.WireActive : objSelected ? MeshView.WireSelectedObject : MeshView.WireNormal;
        var mesh = mv.Node.Mesh;

        if (compMode)
        {
            var baseColor = MeshView.WireSelectedObject;
            s.EdgeColor = e =>
            {
                if (mode == SelectMode.Edge && hover is { } hv && hv.Item2 == SelectMode.Edge && hv.Item3 == e) return MeshView.Hover;
                if (comps != null && comps.Edges.Contains(e) && mode == SelectMode.Edge) return MeshView.EdgeSelected;
                if (mesh != null && !mesh.Edges[e].Hard) return MeshView.WireSoft;
                return baseColor;
            };
            s.VertexColor = v =>
            {
                if (hover is { } hv && hv.Item2 == SelectMode.Vertex && hv.Item3 == v) return MeshView.Hover;
                return comps != null && comps.Verts.Contains(v) ? MeshView.VertexSelected : MeshView.VertexNormal;
            };
            s.FaceSelected = mode == SelectMode.Face
                ? f => (comps != null && comps.Faces.Contains(f)) || (hover is { } hv && hv.Item2 == SelectMode.Face && hv.Item3 == f)
                : null;
        }
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
