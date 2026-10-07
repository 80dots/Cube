using Cube.App.Bridge;
using Cube.Core.Mesh;
using Cube.Core.Scene;
using Godot;

namespace Cube.App.Viewport;

/// <summary>
/// MeshShape 하나의 표시. 셰이딩 표면, 와이어, 정점 점, 면 중심 점, 선택 면 틴트를 자식 노드로 가진다.
/// 색/표시 여부는 <see cref="ComponentStyle"/>로 바깥(선택 상태, 셰이딩 모드)에서 결정한다.
/// </summary>
public partial class MeshView : Node3D
{
    public static readonly Color WireNormal = MathConvert.Rgb(0x1b1b1b);
    public static readonly Color WireActive = MathConvert.Rgb(0x3fff3f);
    public static readonly Color WireSelectedObject = MathConvert.Rgb(0xffffff);
    public static readonly Color WireSoft = MathConvert.Rgb(0x5a5a5a);
    public static readonly Color EdgeSelected = MathConvert.Rgb(0xff8c00);
    public static readonly Color Hover = MathConvert.Rgb(0xf0f0f0);
    public static readonly Color VertexNormal = MathConvert.Rgb(0xb060c0);
    public static readonly Color VertexSelected = MathConvert.Rgb(0xffff00);
    public static readonly Color UvNormal = MathConvert.Rgb(0x4aa3ff);
    public static readonly Color UvSelected = MathConvert.Rgb(0xff3030);
    private Core.Uv.UvTopology? _uvTopo;
    /// <summary>메시의 UV 위상(UV 점/셸). 위상/속성 변경 시 다시 만든다. UV 편집기와 같은 순서라 점 ID가 일치한다.</summary>
    public Core.Uv.UvTopology UvTopo => _uvTopo ??= Core.Uv.UvTopology.Build(Node.Mesh!);
    /// <summary>스무스 프리뷰 표면용 렌더 데이터(케이지와 별개).</summary>
    private readonly RenderMeshData _smoothRender = new();
    private readonly ArrayMesh _smoothWireMesh = new();
    private MeshInstance3D _smoothWire = null!;
    private bool _smoothShown;
    /// <summary>머티리얼에 매핑된 텍스처(UV 편집기 Mapped Texture 배경용). 머티리얼 텍스처 지원 전까지는 null.</summary>
    public Texture2D? MappedTexture { get; set; }
    public static readonly Color FaceCenter = MathConvert.Rgb(0x5aa0ff);

    private static Shader? _wireShader, _pointsShader, _tintShader;
    private static Shader? _surfaceShader;
    private static ShaderMaterial? _shadedMat, _weightMat;
    /// <summary>스킨 변형된 표시 위치(정점 ID 순). null이면 메시 위치 그대로.</summary>
    public System.Numerics.Vector3[]? Deformed { get; private set; }
    private bool _surfaceHasColors;
    private static QuadMesh? _quad;

    public SceneNode Node { get; }
    public RenderMeshData Render { get; } = new();

    private readonly GodotMeshBridge _bridge = new();
    private readonly ArrayMesh _surfaceMesh = new();
    private readonly ArrayMesh _wireMesh = new();
    private readonly ArrayMesh _tintMesh = new();
    private readonly MultiMesh _pointsMm = new();
    private readonly MultiMesh _faceCentersMm = new();
    private MeshInstance3D _surface = null!, _wire = null!, _tint = null!;
    private MultiMeshInstance3D _points = null!, _faceCenters = null!;
    private ShaderMaterial _pointsMat = null!, _faceCentersMat = null!;

    /// <summary>표시 스타일. 선택/호버 상태를 바깥에서 주입한다.</summary>
    public sealed class ComponentStyle
    {
        public bool ShowSurface = true;
        public bool ShowWire = true;
        public bool ShowVertices = false;
        public bool ShowFaceCenters = false;
        public Color ObjectWireColor = WireNormal;
        public Func<int, Color>? EdgeColor;     // edgeId → 색 (null이면 ObjectWireColor/소프트 구분)
        public Func<int, Color>? VertexColor;   // vertexId → 색
        public Func<int, bool>? FaceSelected;   // faceId → 틴트 여부
        public float VertexPx = 4f;
        /// <summary>null이면 기본 회색 lambert, 아니면 이 머티리얼로 표면을 그린다(UV 그리드 등).</summary>
        public Material? SurfaceMaterial;
        /// <summary>Paint Skin Weights: 정점 ID → 가중치(0..1). 있으면 표면을 흑백 램프로 그린다.</summary>
        public Func<int, float>? WeightOf;
    }

    public ComponentStyle Style { get; } = new();

    public MeshView(SceneNode node)
    {
        Node = node;
        Name = node.Name;
    }

    public override void _Ready()
    {
        _wireShader ??= GD.Load<Shader>("res://assets/shaders/wire.gdshader");
        _pointsShader ??= GD.Load<Shader>("res://assets/shaders/points.gdshader");
        _tintShader ??= GD.Load<Shader>("res://assets/shaders/face_tint.gdshader");
        _surfaceShader ??= GD.Load<Shader>("res://assets/shaders/surface.gdshader");
        _shadedMat ??= new ShaderMaterial { Shader = _surfaceShader };
        if (_weightMat == null) { _weightMat = new ShaderMaterial { Shader = _surfaceShader }; _weightMat.SetShaderParameter("use_vertex_color", true); }
        _quad ??= new QuadMesh { Size = new Vector2(1, 1) };

        _surface = new MeshInstance3D { Name = "Surface", Mesh = _surfaceMesh, MaterialOverride = _shadedMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.On };
        AddChild(_surface);

        var wireMat = new ShaderMaterial { Shader = _wireShader, RenderPriority = 1 };
        _wire = new MeshInstance3D { Name = "Wire", Mesh = _wireMesh, MaterialOverride = wireMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(_wire);

        var smoothWireMat = new ShaderMaterial { Shader = _wireShader, RenderPriority = 1 };
        _smoothWire = new MeshInstance3D { Name = "SmoothWire", Mesh = _smoothWireMesh, MaterialOverride = smoothWireMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Visible = false };
        AddChild(_smoothWire);

        var tintMat = new ShaderMaterial { Shader = _tintShader, RenderPriority = 2 };
        _tint = new MeshInstance3D { Name = "FaceTint", Mesh = _tintMesh, MaterialOverride = tintMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(_tint);

        _pointsMm.TransformFormat = MultiMesh.TransformFormatEnum.Transform3D;
        _pointsMm.UseColors = true;
        _pointsMm.Mesh = _quad;
        _pointsMat = new ShaderMaterial { Shader = _pointsShader, RenderPriority = 3 };
        _points = new MultiMeshInstance3D { Name = "Points", Multimesh = _pointsMm, MaterialOverride = _pointsMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Visible = false };
        AddChild(_points);

        _faceCentersMm.TransformFormat = MultiMesh.TransformFormatEnum.Transform3D;
        _faceCentersMm.UseColors = true;
        _faceCentersMm.Mesh = _quad;
        _faceCentersMat = new ShaderMaterial { Shader = _pointsShader, RenderPriority = 3 };
        _faceCenters = new MultiMeshInstance3D { Name = "FaceCenters", Multimesh = _faceCentersMm, MaterialOverride = _faceCentersMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Visible = false };
        AddChild(_faceCenters);

        Rebuild();
    }

    /// <summary>위상 변경 후 전체 재빌드.</summary>
    public void Rebuild()
    {
        var mesh = Node.Mesh;
        if (mesh == null) return;
        _uvTopo = null;
        MeshTessellator.Build(mesh, Render);
        if (Deformed != null) MeshTessellator.UpdatePositions(mesh, Render, Deformed);
        UploadSurface();
        RefreshStyle();
    }

    /// <summary>Smooth Mesh Preview(1/2/3키): 0 = 케이지, 1 = 케이지 와이어 + 스무스 표면, 2 = 스무스 표면 + 스무스 와이어.</summary>
    private int SmoothPreview => Node.MeshShape?.SmoothPreview ?? 0;

    /// <summary>케이지(변형 위치 반영)를 Catmull-Clark으로 나눈 표면 렌더 데이터를 만든다.</summary>
    private void BuildSmooth()
    {
        var mesh = Node.Mesh!;
        var cage = mesh.Clone();
        if (Deformed != null) for (int v = 0; v < cage.VertexCount && v < Deformed.Length; v++) { var vert = cage.Verts[v]; vert.Position = Deformed[v]; cage.Verts[v] = vert; }
        int levels = Math.Clamp(Node.MeshShape?.SmoothPreviewLevels ?? 2, 1, 3);
        PolyMesh cur = cage;
        for (int i = 0; i < levels; i++) cur = MeshOps.CatmullClark(cur);
        MeshNormals.Recompute(cur);
        MeshTessellator.Build(cur, _smoothRender);
    }

    /// <summary>위치만 바뀐 경우(드래그). 위상 동일.</summary>
    public void UpdatePositions()
    {
        var mesh = Node.Mesh;
        if (mesh == null) return;
        MeshTessellator.UpdatePositions(mesh, Render, Deformed);
        // 노멀이 바뀌므로 표면은 다시 올린다(코너 수는 동일)
        UploadSurface();
        RefreshStyle();
    }

    /// <summary>스킨 변형 위치를 지정(null = 해제)하고 표시를 갱신한다.</summary>
    public void SetDeformed(System.Numerics.Vector3[]? positions)
    {
        Deformed = positions;
        if (_surface != null) UpdatePositions();
    }

    private void UploadSurface()
    {
        var mesh = Node.Mesh!;
        var w = Style.WeightOf;
        if (SmoothPreview > 0)
        {
            BuildSmooth();
            _bridge.UploadSurface(_surfaceMesh, _smoothRender);
            _surfaceHasColors = false; _smoothShown = true;
            return;
        }
        _smoothShown = false;
        if (w != null)
        {
            _bridge.UploadSurface(_surfaceMesh, Render, i => WeightColor(w(mesh.Hes[Render.CornerToHalfEdge[i]].Vertex)));
            _surfaceHasColors = true;
        }
        else { _bridge.UploadSurface(_surfaceMesh, Render); _surfaceHasColors = false; }
    }

    /// <summary>Maya 식 가중치 램프: 0 = 어두운 회색, 1 = 흰색(선형 색).</summary>
    private static Color WeightColor(float w)
    {
        w = Math.Clamp(w, 0f, 1f);
        float g = 0.12f + 0.88f * w;
        return new Color(g, g, g).SrgbToLinear();
    }

    /// <summary>선택/모드 변경 등 색만 바뀐 경우.</summary>
    public void RefreshStyle()
    {
        var mesh = Node.Mesh;
        if (mesh == null || _surface == null) return;
        var s = Style;
        _surface.Visible = s.ShowSurface;
        if ((s.WeightOf != null) != _surfaceHasColors || s.WeightOf != null || (SmoothPreview > 0) != _smoothShown) UploadSurface();
        _surface.MaterialOverride = s.WeightOf != null && !_smoothShown ? _weightMat : (s.SurfaceMaterial ?? _shadedMat);
        // 스무스 프리뷰 2(3키): 스무스 와이어, 케이지 와이어 숨김. 1(2키): 케이지 와이어 + 스무스 표면
        _smoothWire.Visible = s.ShowWire && SmoothPreview == 2;
        if (_smoothWire.Visible) _bridge.UploadLines(_smoothWireMesh, _smoothRender, _ => s.ObjectWireColor);
        _wire.Visible = s.ShowWire && SmoothPreview != 2;
        _points.Visible = s.ShowVertices;
        _faceCenters.Visible = s.ShowFaceCenters;

        if (s.ShowWire)
        {
            _bridge.UploadLines(_wireMesh, Render, e =>
            {
                if (s.EdgeColor != null) return s.EdgeColor(e);
                return s.ObjectWireColor;
            });
        }
        if (s.ShowVertices)
        {
            _pointsMat.SetShaderParameter("point_px", s.VertexPx * CubeApp.Instance.UiScale);
            var pts = new ReadOnlySpan<System.Numerics.Vector3>(Render.PointPositions, 0, Render.PointCount);
            GodotMeshBridge.UploadPoints(_pointsMm, pts, i => s.VertexColor != null ? s.VertexColor(Render.PointToVertex[i]) : VertexNormal);
        }
        if (s.ShowFaceCenters)
        {
            _faceCentersMat.SetShaderParameter("point_px", s.VertexPx * CubeApp.Instance.UiScale);
            var pts = new ReadOnlySpan<System.Numerics.Vector3>(Render.FaceCenters, 0, Render.FaceCenterCount);
            GodotMeshBridge.UploadPoints(_faceCentersMm, pts, i => s.FaceSelected != null && s.FaceSelected(Render.FaceCenterToFace[i]) ? VertexSelected : FaceCenter);
        }
        if (s.FaceSelected != null) _bridge.UploadFaceSubset(_tintMesh, Render, s.FaceSelected);
        else _tintMesh.ClearSurfaces();
    }
}
