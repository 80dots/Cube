using Cube.App.Bridge;
using Cube.Core.Mesh;
using Cube.Core.Scene;
using Godot;

namespace Cube.App.Viewport;

/// <summary>
/// MeshShape 하나의 표시. 셰이딩 표면, 와이어, 정점 점, 면 중심 점, 선택 면 틴트를 자식 노드로 가진다.
/// 색/표시 여부는 <see cref="ComponentStyle"/>로 바깥(선택 상태, 셰이딩 모드)에서 결정한다.
/// </summary>
/// <remarks>
/// 흐름: 위상이 바뀌면 <see cref="Rebuild"/>(테셀레이션 + 전체 업로드), 위치만 바뀌면 <see cref="UpdatePositions"/>(버퍼 부분 갱신),
/// 색/표시만 바뀌면 <see cref="RefreshStyle"/>. 렌더 데이터(<see cref="Render"/>)는 코어 <c>MeshTessellator</c>가 만들고
/// <see cref="GodotMeshBridge"/>가 Godot 메시로 올린다. 노드 트랜스폼(월드 위치)은 SceneView가 설정하며 여기 지오메트리는 오브젝트 로컬 공간이다.
/// 스킨 변형(<see cref="Deformed"/>)과 Smooth Mesh Preview는 표시 전용이며 문서 메시는 바꾸지 않는다.
/// 와이어/점/틴트는 깊이 바이어스 셰이더(wire/points/face_tint)로 표면 위에 그려진다.
/// </remarks>
public partial class MeshView : Node3D
{
    /// <summary>오브젝트가 선택되지 않았을 때 와이어 색(어두운 회색).</summary>
    public static readonly Color WireNormal = MathConvert.Rgb(0x1b1b1b);
    /// <summary>활성(마지막 선택) 오브젝트 와이어 색(초록, Maya와 같음).</summary>
    public static readonly Color WireActive = MathConvert.Rgb(0x3fff3f);
    /// <summary>선택된(비활성) 오브젝트 와이어 색(흰색).</summary>
    public static readonly Color WireSelectedObject = MathConvert.Rgb(0xffffff);
    /// <summary>컴포넌트 모드에서 소프트 엣지 등 덜 강조할 와이어 색.</summary>
    public static readonly Color WireSoft = MathConvert.Rgb(0x5a5a5a);
    /// <summary>크리즈 엣지(Mesh Tools → Crease).</summary>
    public static readonly Color EdgeCrease = MathConvert.Rgb(0xd070ff);
    /// <summary>선택된 엣지 색(주황).</summary>
    public static readonly Color EdgeSelected = MathConvert.Rgb(0xff8c00);
    /// <summary>호버(프리셀렉션) 컴포넌트 색.</summary>
    public static readonly Color Hover = MathConvert.Rgb(0xf0f0f0);
    /// <summary>선택되지 않은 정점 점 색(보라).</summary>
    public static readonly Color VertexNormal = MathConvert.Rgb(0xb060c0);
    /// <summary>선택된 정점 점 색(노랑). 선택된 면 중심 점에도 쓴다.</summary>
    public static readonly Color VertexSelected = MathConvert.Rgb(0xffff00);
    /// <summary>뷰포트 UV 모드(F12)에서 선택되지 않은 UV 점 색(파랑).</summary>
    public static readonly Color UvNormal = MathConvert.Rgb(0x4aa3ff);
    /// <summary>뷰포트 UV 모드에서 그 정점의 UV 점이 하나라도 선택됐을 때 색(빨강).</summary>
    public static readonly Color UvSelected = MathConvert.Rgb(0xff3030);
    /// <summary><see cref="UvTopo"/> 지연 캐시(null = 다시 만들어야 함, <see cref="Rebuild"/>가 비움).</summary>
    private Core.Uv.UvTopology? _uvTopo;
    /// <summary>메시의 UV 위상(UV 점/셸). 위상/속성 변경 시 다시 만든다. UV 편집기와 같은 순서라 점 ID가 일치한다.</summary>
    public Core.Uv.UvTopology UvTopo => _uvTopo ??= Core.Uv.UvTopology.Build(Node.Mesh!);
    /// <summary>스무스 프리뷰 표면용 렌더 데이터(케이지와 별개).</summary>
    private readonly RenderMeshData _smoothRender = new();
    /// <summary>스무스 프리뷰 2(3키)에서 쓰는 스무스 메시 와이어.</summary>
    private readonly ArrayMesh _smoothWireMesh = new();
    /// <summary>스무스 와이어 표시 노드.</summary>
    private MeshInstance3D _smoothWire = null!;
    /// <summary>지금 표면이 스무스 프리뷰 결과로 올라가 있는지(위치 부분 갱신 불가 판단에 사용).</summary>
    private bool _smoothShown;
    /// <summary>머티리얼에 매핑된 텍스처(UV 편집기 Mapped Texture 배경용). 머티리얼 텍스처 지원 전까지는 null.</summary>
    public Texture2D? MappedTexture { get; set; }
    /// <summary>면 중심 점 기본 색(면 모드, 파랑).</summary>
    public static readonly Color FaceCenter = MathConvert.Rgb(0x5aa0ff);

    /// <summary>와이어·점·면 틴트 셰이더(모든 MeshView가 공유, 처음 _Ready에서 로드).</summary>
    private static Shader? _wireShader, _pointsShader, _tintShader;
    /// <summary>표면 셰이더(surface.gdshader: 양면, 뒷면 검정, use_texture/use_vertex_color 유니폼).</summary>
    private static Shader? _surfaceShader;
    /// <summary>기본 회색 셰이딩 머티리얼과 가중치 표시(정점 색 사용) 머티리얼. 공유 인스턴스.</summary>
    private static ShaderMaterial? _shadedMat, _weightMat;
    /// <summary>스킨 변형된 표시 위치(정점 ID 순). null이면 메시 위치 그대로.</summary>
    public System.Numerics.Vector3[]? Deformed { get; private set; }
    /// <summary>현재 표면에 정점 색(가중치 램프)이 올라가 있는지. 가중치 표시를 켜고 끌 때 다시 올려야 하는지 판단한다.</summary>
    private bool _surfaceHasColors;
    /// <summary>정점/면 중심 점에 쓰는 단위 쿼드(D3D12는 포인트 크기를 지원하지 않아 MultiMesh 쿼드로 그림).</summary>
    private static QuadMesh? _quad;

    /// <summary>표시하는 문서 노드(MeshShape를 가진 SceneNode).</summary>
    public SceneNode Node { get; }
    /// <summary>케이지 메시의 렌더 데이터(코너 언롤 삼각형, 선, 점, 면 중심 + 렌더 요소 → 컴포넌트 ID 매핑). 피킹도 이걸 쓴다.</summary>
    public RenderMeshData Render { get; } = new();

    /// <summary>Godot 메시 업로드 도우미(버퍼 재사용, 부분 갱신).</summary>
    private readonly GodotMeshBridge _bridge = new();
    /// <summary>셰이딩 표면 메시.</summary>
    private readonly ArrayMesh _surfaceMesh = new();
    /// <summary>케이지 와이어 메시.</summary>
    private readonly ArrayMesh _wireMesh = new();
    /// <summary>선택 면 반투명 틴트 메시.</summary>
    private readonly ArrayMesh _tintMesh = new();
    /// <summary>정점 점 MultiMesh(인스턴스 = 렌더 점).</summary>
    private readonly MultiMesh _pointsMm = new();
    /// <summary>면 중심 점 MultiMesh.</summary>
    private readonly MultiMesh _faceCentersMm = new();
    /// <summary>표면·와이어·틴트 표시 노드.</summary>
    private MeshInstance3D _surface = null!, _wire = null!, _tint = null!;
    /// <summary>정점 점·면 중심 점 표시 노드.</summary>
    private MultiMeshInstance3D _points = null!, _faceCenters = null!;
    /// <summary>점 머티리얼(point_px 유니폼 = 화면 픽셀 크기). 점과 면 중심이 따로 가진다.</summary>
    private ShaderMaterial _pointsMat = null!, _faceCentersMat = null!;
    /// <summary>점/면 중심 MultiMesh 인스턴스 버퍼(변환 12 + 색 4 float). 위치만 갱신할 때 색을 유지한다.</summary>
    private float[] _pointsBuf = Array.Empty<float>(), _faceCentersBuf = Array.Empty<float>();

    /// <summary>표시 스타일. 선택/호버 상태를 바깥에서 주입한다.</summary>
    /// <remarks>ViewportDisplay가 선택 모드·셰이딩 모드·선택 상태를 보고 필드를 채운 뒤 <see cref="RefreshStyle"/>을 부른다.</remarks>
    public sealed class ComponentStyle
    {
        /// <summary>셰이딩 표면을 보일지(와이어프레임 모드면 false).</summary>
        public bool ShowSurface = true;
        /// <summary>와이어를 보일지.</summary>
        public bool ShowWire = true;
        /// <summary>정점 점을 보일지(정점/UV 모드).</summary>
        public bool ShowVertices = false;
        /// <summary>면 중심 점을 보일지(면 모드).</summary>
        public bool ShowFaceCenters = false;
        /// <summary>EdgeColor가 없을 때 모든 엣지에 쓰는 오브젝트 와이어 색(선택 상태).</summary>
        public Color ObjectWireColor = WireNormal;
        public Func<int, Color>? EdgeColor;     // edgeId → 색 (null이면 ObjectWireColor/소프트 구분)
        public Func<int, Color>? VertexColor;   // vertexId → 색
        public Func<int, bool>? FaceSelected;   // faceId → 틴트 여부
        /// <summary>정점 점 크기(px, UiScale을 곱하기 전).</summary>
        public float VertexPx = 4f;
        /// <summary>null이면 기본 회색 lambert, 아니면 이 머티리얼로 표면을 그린다(UV 그리드 등).</summary>
        public Material? SurfaceMaterial;
        /// <summary>Paint Skin Weights: 정점 ID → 가중치(0..1). 있으면 표면을 흑백 램프로 그린다.</summary>
        public Func<int, float>? WeightOf;
    }

    /// <summary>현재 표시 스타일(인스턴스 하나를 계속 고쳐 쓴다).</summary>
    public ComponentStyle Style { get; } = new();

    /// <summary>문서 노드를 받아 뷰를 만든다. 자식 노드는 <see cref="_Ready"/>에서 만든다.</summary>
    public MeshView(SceneNode node)
    {
        Node = node;
        Name = node.Name;
    }

    /// <summary>
    /// 공유 셰이더·머티리얼을 (처음 한 번) 로드하고 자식 노드를 만든다: Surface(그림자 O), Wire/SmoothWire(RenderPriority 1),
    /// FaceTint(2), Points/FaceCenters(3, MultiMesh 쿼드). 마지막에 <see cref="Rebuild"/>로 첫 업로드.
    /// </summary>
    public override void _Ready()
    {
        // 공유 리소스 지연 로드(모든 MeshView가 한 벌을 공유)
        _wireShader ??= GD.Load<Shader>("res://assets/shaders/wire.gdshader");
        _pointsShader ??= GD.Load<Shader>("res://assets/shaders/points.gdshader");
        _tintShader ??= GD.Load<Shader>("res://assets/shaders/face_tint.gdshader");
        _surfaceShader ??= GD.Load<Shader>("res://assets/shaders/surface.gdshader");
        _shadedMat ??= new ShaderMaterial { Shader = _surfaceShader };
        if (_weightMat == null) { _weightMat = new ShaderMaterial { Shader = _surfaceShader }; _weightMat.SetShaderParameter("use_vertex_color", true); }
        _quad ??= new QuadMesh { Size = new Vector2(1, 1) };

        // 셰이딩 표면
        _surface = new MeshInstance3D { Name = "Surface", Mesh = _surfaceMesh, MaterialOverride = _shadedMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.On };
        AddChild(_surface);

        // 케이지 와이어와 스무스 와이어(같은 와이어 셰이더, 머티리얼은 노드별)
        var wireMat = new ShaderMaterial { Shader = _wireShader, RenderPriority = 1 };
        _wire = new MeshInstance3D { Name = "Wire", Mesh = _wireMesh, MaterialOverride = wireMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(_wire);

        var smoothWireMat = new ShaderMaterial { Shader = _wireShader, RenderPriority = 1 };
        _smoothWire = new MeshInstance3D { Name = "SmoothWire", Mesh = _smoothWireMesh, MaterialOverride = smoothWireMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Visible = false };
        AddChild(_smoothWire);

        // 선택 면 틴트
        var tintMat = new ShaderMaterial { Shader = _tintShader, RenderPriority = 2 };
        _tint = new MeshInstance3D { Name = "FaceTint", Mesh = _tintMesh, MaterialOverride = tintMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(_tint);

        // 정점 점: 위치 + 색 인스턴스 MultiMesh
        _pointsMm.TransformFormat = MultiMesh.TransformFormatEnum.Transform3D;
        _pointsMm.UseColors = true;
        _pointsMm.Mesh = _quad;
        _pointsMat = new ShaderMaterial { Shader = _pointsShader, RenderPriority = 3 };
        _points = new MultiMeshInstance3D { Name = "Points", Multimesh = _pointsMm, MaterialOverride = _pointsMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Visible = false };
        AddChild(_points);

        // 면 중심 점
        _faceCentersMm.TransformFormat = MultiMesh.TransformFormatEnum.Transform3D;
        _faceCentersMm.UseColors = true;
        _faceCentersMm.Mesh = _quad;
        _faceCentersMat = new ShaderMaterial { Shader = _pointsShader, RenderPriority = 3 };
        _faceCenters = new MultiMeshInstance3D { Name = "FaceCenters", Multimesh = _faceCentersMm, MaterialOverride = _faceCentersMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Visible = false };
        AddChild(_faceCenters);

        Rebuild();
    }

    /// <summary>위상 변경 후 전체 재빌드.</summary>
    /// <remarks>UV 위상 캐시를 버리고 테셀레이션을 다시 한 뒤(스킨 변형이 있으면 위치를 덮어씀) 표면·스타일·바운드를 모두 다시 올린다.</remarks>
    public void Rebuild()
    {
        var mesh = Node.Mesh;
        if (mesh == null) return;
        _uvTopo = null;
        MeshTessellator.Build(mesh, Render);
        if (Deformed != null) MeshTessellator.UpdatePositions(mesh, Render, Deformed);
        UploadSurface();
        RefreshStyle();
        ApplyBounds();
    }

    /// <summary>Smooth Mesh Preview(1/2/3키): 0 = 케이지, 1 = 케이지 와이어 + 스무스 표면, 2 = 스무스 표면 + 스무스 와이어.</summary>
    private int SmoothPreview => Node.MeshShape?.SmoothPreview ?? 0;

    /// <summary>케이지(변형 위치 반영)를 Catmull-Clark으로 나눈 표면 렌더 데이터를 만든다.</summary>
    /// <remarks>
    /// 문서 메시를 복제해 변형 위치를 넣고 SmoothPreviewLevels(1..3)만큼 CatmullClark을 반복한 뒤 노멀을 다시 계산해 <c>_smoothRender</c>에 테셀레이션한다.
    /// 원본 메시와 <see cref="Render"/>(케이지, 선택/피킹용)는 건드리지 않는다.
    /// </remarks>
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

    /// <summary>
    /// 위치만 바뀐 경우(드래그, 스킨 변형 재생). 위상 동일.
    /// 표면/와이어/점/틴트의 정점 버퍼 위치만 덮어쓰고(<see cref="GodotMeshBridge.UpdateSurfacePositions"/> 등) 서피스를 다시 만들지 않는다.
    /// 스무스 프리뷰는 서브디비전 결과라 전체 재생성. 노멀은 코너 노멀 그대로(변형 전과 같다).
    /// </summary>
    public void UpdatePositions()
    {
        // CPU 단계(렌더 배열 위치 갱신)와 업로드 단계를 차례로 실행하고 AnimPerf로 시간을 잰다
        if (Node.Mesh == null) return;
        long t0 = AnimPerf.Begin();
        PreparePositions();
        AnimPerf.End("mesh.updatePositions", t0);
        CommitPositions();
    }

    /// <summary>위치 갱신의 CPU 단계(렌더 배열만 건드림, Godot 호출 없음 → 여러 메시를 병렬로 돌릴 수 있다). 뒤에 <see cref="CommitPositions"/>를 부를 것.</summary>
    public void PreparePositions()
    {
        var mesh = Node.Mesh;
        if (mesh == null) return;
        MeshTessellator.UpdatePositions(mesh, Render, Deformed);
    }

    /// <summary>위치 갱신의 업로드 단계(메인 스레드).</summary>
    public void CommitPositions()
    {
        // 스무스 프리뷰가 켜져 있거나 막 꺼진 경우: 서브디비전 결과는 부분 갱신이 불가능하므로 전체 재업로드
        if (Node.Mesh == null || _surface == null) return;
        long t1 = AnimPerf.Begin();
        if (SmoothPreview > 0 || _smoothShown)
        {
            UploadSurface();
            AnimPerf.End("mesh.uploadSurface", t1);
            long t2 = AnimPerf.Begin();
            RefreshStyle();
            AnimPerf.End("mesh.refreshStyle", t2);
            ApplyBounds();
            return;
        }
        // 일반: 표면 위치만 부분 갱신(실패하면 전체 업로드), 나머지 요소도 위치만 따라가게
        if (!_bridge.UpdateSurfacePositions(_surfaceMesh, Render)) UploadSurface();
        AnimPerf.End("mesh.uploadSurface", t1);
        long t3 = AnimPerf.Begin();
        RefreshPositions();
        AnimPerf.End("mesh.refreshStyle", t3);
        ApplyBounds();
    }

    /// <summary>스타일(색·표시 여부)은 그대로 두고 와이어/점/면 중심/틴트의 위치만 따라가게 한다. 부분 갱신이 안 되면 그 요소만 다시 올린다.</summary>
    private void RefreshPositions()
    {
        // 보이는 요소만 갱신: 와이어 → 정점 점 → 면 중심 → 틴트 순. 각각 부분 갱신이 안 되면 그 요소만 전체 업로드
        var s = Style;
        if (_wire.Visible && !_bridge.UpdateLinePositions(_wireMesh, Render)) UploadWire();
        if (_points.Visible)
        {
            var pts = new ReadOnlySpan<System.Numerics.Vector3>(Render.PointPositions, 0, Render.PointCount);
            if (!GodotMeshBridge.UpdatePointPositions(_pointsMm, pts, _pointsBuf)) UploadVertexPoints();
        }
        if (_faceCenters.Visible)
        {
            var pts = new ReadOnlySpan<System.Numerics.Vector3>(Render.FaceCenters, 0, Render.FaceCenterCount);
            if (!GodotMeshBridge.UpdatePointPositions(_faceCentersMm, pts, _faceCentersBuf)) UploadFaceCenterPoints();
        }
        if (s.FaceSelected != null && _tintMesh.GetSurfaceCount() > 0 && !_bridge.UpdateFaceSubsetPositions(_tintMesh, Render)) _bridge.UploadFaceSubset(_tintMesh, Render, s.FaceSelected);
    }

    /// <summary>
    /// 부분 갱신은 Godot이 서피스 AABB를 다시 재지 않으므로(변형된 메시가 원래 상자 밖으로 나가면 컬링됨) 현재 점 바운드를 커스텀 AABB로 준다.
    /// 스킨 변형 메시의 AABB 재계산 비용도 피한다.
    /// </summary>
    private void ApplyBounds()
    {
        if (_surface == null) return;
        // 렌더 점 바운드에 작은 여유(1mm)를 더한 상자를 모든 자식에 커스텀 AABB로 지정
        var mn = Render.BoundsMin; var mx = Render.BoundsMax;
        const float pad = 1e-3f;
        var aabb = new Aabb((mn - new System.Numerics.Vector3(pad)).ToGodot(), (mx - mn + new System.Numerics.Vector3(pad * 2)).ToGodot());
        _surface.CustomAabb = aabb; _wire.CustomAabb = aabb; _tint.CustomAabb = aabb; _points.CustomAabb = aabb; _faceCenters.CustomAabb = aabb;
        // 스무스 프리뷰 표면은 케이지 바운드와 다를 수 있어 커스텀 AABB를 해제(Godot이 직접 계산)
        if (_smoothShown) { _surface.CustomAabb = default; _smoothWire.CustomAabb = default; }
    }

    /// <summary>스킨 변형 위치를 지정(null = 해제)하고 표시를 갱신한다.</summary>
    public void SetDeformed(System.Numerics.Vector3[]? positions)
    {
        Deformed = positions;
        if (_surface != null) UpdatePositions();
    }

    /// <summary>변형 위치만 바꾼다(표시 갱신은 호출자가 <see cref="PreparePositions"/>/<see cref="CommitPositions"/>로).</summary>
    public void SetDeformedNoUpload(System.Numerics.Vector3[]? positions) => Deformed = positions;

    /// <summary>
    /// 표면을 전체 업로드한다. 스무스 프리뷰면 스무스 렌더 데이터를, 가중치 표시(Style.WeightOf)면 코너마다 그 정점의 가중치 램프 색을,
    /// 아니면 케이지 렌더 데이터를 그대로 올린다. <c>_surfaceHasColors</c>/<c>_smoothShown</c> 상태를 갱신한다.
    /// </summary>
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
        // 가중치 표시: 렌더 코너 → 하프에지 → 정점 ID로 가중치를 찾아 정점 색으로 넣는다
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
    /// <remarks>
    /// 표시 여부·머티리얼을 정하고 보이는 요소(와이어/점/면 중심/틴트)의 색을 다시 올린다. 가중치 표시나 스무스 프리뷰 상태가 바뀌었으면 표면도 다시 올린다.
    /// </remarks>
    {
        var mesh = Node.Mesh;
        if (mesh == null || _surface == null) return;
        var s = Style;
        _surface.Visible = s.ShowSurface;
        // 표면을 다시 올려야 하는 경우: 가중치 색 유무가 바뀜, 가중치 표시 중(값이 바뀌었을 수 있음), 스무스 프리뷰 상태가 바뀜
        if ((s.WeightOf != null) != _surfaceHasColors || s.WeightOf != null || (SmoothPreview > 0) != _smoothShown) UploadSurface();
        // 머티리얼: 가중치 표시(스무스 아님) > 스타일 지정 머티리얼(문서 머티리얼·UV 그리드) > 기본 회색
        _surface.MaterialOverride = s.WeightOf != null && !_smoothShown ? _weightMat : (s.SurfaceMaterial ?? _shadedMat);
        // 스무스 프리뷰 2(3키): 스무스 와이어, 케이지 와이어 숨김. 1(2키): 케이지 와이어 + 스무스 표면
        _smoothWire.Visible = s.ShowWire && SmoothPreview == 2;
        if (_smoothWire.Visible) _bridge.UploadLines(_smoothWireMesh, _smoothRender, _ => s.ObjectWireColor);
        _wire.Visible = s.ShowWire && SmoothPreview != 2;
        _points.Visible = s.ShowVertices;
        _faceCenters.Visible = s.ShowFaceCenters;

        // 보이는 요소만 현재 스타일 색으로 다시 업로드, 틴트는 선택 면이 없으면 비운다
        if (s.ShowWire) UploadWire();
        if (s.ShowVertices) UploadVertexPoints();
        if (s.ShowFaceCenters) UploadFaceCenterPoints();
        if (s.FaceSelected != null) _bridge.UploadFaceSubset(_tintMesh, Render, s.FaceSelected);
        else _tintMesh.ClearSurfaces();
    }

    /// <summary>케이지 와이어를 엣지별 색(Style.EdgeColor, 없으면 오브젝트 와이어 색)으로 올린다.</summary>
    private void UploadWire()
    {
        var s = Style;
        _bridge.UploadLines(_wireMesh, Render, e =>
        {
            if (s.EdgeColor != null) return s.EdgeColor(e);
            return s.ObjectWireColor;
        });
    }

    /// <summary>정점 점을 올린다. 화면 크기(point_px)를 UI 배율에 맞추고, 색은 렌더 점 → 정점 ID로 Style.VertexColor를 조회한다.</summary>
    private void UploadVertexPoints()
    {
        var s = Style;
        _pointsMat.SetShaderParameter("point_px", s.VertexPx * CubeApp.Instance.UiScale);
        var pts = new ReadOnlySpan<System.Numerics.Vector3>(Render.PointPositions, 0, Render.PointCount);
        GodotMeshBridge.UploadPoints(_pointsMm, pts, i => s.VertexColor != null ? s.VertexColor(Render.PointToVertex[i]) : VertexNormal, ref _pointsBuf);
    }

    /// <summary>면 중심 점을 올린다. 선택된 면이면 노랑, 아니면 파랑.</summary>
    private void UploadFaceCenterPoints()
    {
        var s = Style;
        _faceCentersMat.SetShaderParameter("point_px", s.VertexPx * CubeApp.Instance.UiScale);
        var pts = new ReadOnlySpan<System.Numerics.Vector3>(Render.FaceCenters, 0, Render.FaceCenterCount);
        GodotMeshBridge.UploadPoints(_faceCentersMm, pts, i => s.FaceSelected != null && s.FaceSelected(Render.FaceCenterToFace[i]) ? VertexSelected : FaceCenter, ref _faceCentersBuf);
    }
}
