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
    public static readonly Color FaceCenter = MathConvert.Rgb(0x5aa0ff);

    private static Shader? _wireShader, _pointsShader, _tintShader;
    private static StandardMaterial3D? _shadedMat;
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
        public float VertexPx = 3f;
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
        _shadedMat ??= new StandardMaterial3D
        {
            AlbedoColor = new Color(0.5f, 0.5f, 0.5f),
            Roughness = 1f,
            Metallic = 0f,
            CullMode = BaseMaterial3D.CullModeEnum.Back,
        };
        _quad ??= new QuadMesh { Size = new Vector2(1, 1) };

        _surface = new MeshInstance3D { Name = "Surface", Mesh = _surfaceMesh, MaterialOverride = _shadedMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.On };
        AddChild(_surface);

        var wireMat = new ShaderMaterial { Shader = _wireShader, RenderPriority = 1 };
        _wire = new MeshInstance3D { Name = "Wire", Mesh = _wireMesh, MaterialOverride = wireMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(_wire);

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
        MeshTessellator.Build(mesh, Render);
        _bridge.UploadSurface(_surfaceMesh, Render);
        RefreshStyle();
    }

    /// <summary>위치만 바뀐 경우(드래그). 위상 동일.</summary>
    public void UpdatePositions()
    {
        var mesh = Node.Mesh;
        if (mesh == null) return;
        MeshTessellator.UpdatePositions(mesh, Render);
        // 노멀이 바뀌므로 표면은 다시 올린다(코너 수는 동일)
        _bridge.UploadSurface(_surfaceMesh, Render);
        RefreshStyle();
    }

    /// <summary>선택/모드 변경 등 색만 바뀐 경우.</summary>
    public void RefreshStyle()
    {
        var mesh = Node.Mesh;
        if (mesh == null || _surface == null) return;
        var s = Style;
        _surface.Visible = s.ShowSurface;
        _wire.Visible = s.ShowWire;
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
