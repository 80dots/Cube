using Cube.App.Bridge;
using Cube.Core.Camera;
using Cube.Core.Scene;
using Godot;

namespace Cube.App.Viewport;

/// <summary>
/// 3D 뷰포트 위젯. SubViewport에 자체 World3D를 두고 카메라·헤드라이트·그리드·SceneView를 담는다.
/// 입력은 이 컨테이너의 _GuiInput에서만 받아 내비게이션 → 파이 메뉴 → 툴 순으로 넘긴다.
/// </summary>
/// <remarks>
/// ViewportLayout이 4개(top/persp/front/side)를 만든다. 각 패널은 자체 World3D라 SceneView(문서 미러)·그리드·조작기 루트를 따로 가진다.
/// 노드 구성: SubViewport[Background(캔버스 그라디언트), WorldEnvironment, Camera(+HeadLight 자식), Grid, Scene, Gizmos] + Overlay(2D) + PieMenu + Hud.
/// 입력 파이프라인(<see cref="_GuiInput"/>): 모달 툴(Alt 없는 마우스 버튼) → <see cref="NavigationHandler"/>(Alt+버튼/휠) → 파이 메뉴(RMB) → <see cref="ToolInput"/>(현재 툴).
/// <c>_PropagateInputEvent</c>가 false라 SubViewport 안으로 입력이 전달되지 않는다(모든 피킹은 CPU에서 Picker가 한다).
/// </remarks>
public partial class ViewportPanel : SubViewportContainer
{
    /// <summary>처음 시작할 뷰 방향(_Ready 전에 ViewportLayout이 설정). Persp가 아니면 직교 프리셋으로 시작한다.</summary>
    public ViewKind InitialView = ViewKind.Persp;

    /// <summary>3D 장면을 렌더링하는 SubViewport(OwnWorld3D, 입력은 로컬 처리 안 함).</summary>
    public SubViewport Viewport { get; private set; } = null!;
    /// <summary>패널 카메라(Godot 노드).</summary>
    public Camera3D Camera { get; private set; } = null!;
    /// <summary>궤도 카메라 상태·프리셋 애니메이션 컨트롤러.</summary>
    public ViewportCamera CameraController { get; private set; } = null!;
    /// <summary>Maya Alt 내비게이션 입력 해석기.</summary>
    public NavigationHandler Navigation { get; private set; } = null!;
    /// <summary>카메라에 붙은 방향광(헤드라이트, Maya Default Lighting). Lit 모드가 아닐 때만 켠다.</summary>
    public DirectionalLight3D HeadLight { get; private set; } = null!;
    /// <summary>바닥 그리드(측면 프리셋 뷰에서는 뷰 평면으로 세운다).</summary>
    public GridView Grid { get; private set; } = null!;
    /// <summary>문서 DAG 미러(메시·조인트·라이트 뷰).</summary>
    public SceneView Scene { get; private set; } = null!;
    /// <summary>2D 오버레이(축 기즈모, 카메라 이름, 마키, 브러시, Poly Count).</summary>
    public ViewportOverlay Overlay { get; private set; } = null!;
    /// <summary>우상단 HUD(뷰 큐브, 셰이딩 모드 버튼 등).</summary>
    public ViewportHud Hud { get; private set; } = null!;
    /// <summary>조작기(Gizmo) 노드를 붙이는 루트. 툴이 활성 패널의 이 노드로 조작기를 옮긴다.</summary>
    public Node3D GizmoRoot { get; private set; } = null!;
    /// <summary>셰이딩 모드·선택 상태를 MeshView 스타일로 바꾸는 표시 상태.</summary>
    public ViewportDisplay Display { get; private set; } = null!;
    /// <summary>이 패널의 파이 메뉴 위젯.</summary>
    public UI.PieMenu Pie { get; private set; } = null!;

    /// <summary><see cref="Picker"/> 지연 생성 캐시.</summary>
    private Picker? _picker;
    /// <summary>이 패널의 피커(화면 좌표 → 오브젝트/컴포넌트). 처음 접근할 때 만든다.</summary>
    public Picker Picker => _picker ??= new Picker(this);

    /// <summary>내비게이션/파이가 소비하지 않은 이벤트를 받는다(툴 라우팅). true를 반환하면 소비.</summary>
    public Func<InputEvent, bool>? ToolInput;
    /// <summary>RMB 파이 메뉴 항목 공급자(shift 여부 → 항목). Shell이 설정한다.</summary>
    /// <summary>(shift, ctrl) → 파이 항목. RMB = 기본(모드), Shift+RMB = Edit, Ctrl+RMB = Select.</summary>
    public Func<bool, bool, IEnumerable<UI.PieItem>>? PieItems;
    /// <summary>파이 메뉴에서 고른 항목을 실행하는 콜백(Shell이 설정; 보통 ActionRegistry 액션 또는 항목의 Run).</summary>
    public Action<UI.PieItem>? PieExecute;
    /// <summary>마우스가 들어오거나 버튼이 눌리면 발생(활성 패널 전환).</summary>
    public event Action? Activated;

    /// <summary>바인드된 문서(_Ready 전에 Bind되면 _Ready에서 SceneView에 연결).</summary>
    private Document? _doc;
    /// <summary>World 배경이 Canvas일 때 보이는 세로 그라디언트 배경.</summary>
    private TextureRect _background = null!;

    /// <summary>배경 그라디언트 위쪽 색 순환 목록(<see cref="CycleBackground"/>). 첫 항목만 아래쪽이 더 어두운 그라디언트다.</summary>
    public static readonly Color[] BackgroundCycle =
    {
        MathConvert.Rgb(0x3a3a3a), MathConvert.Rgb(0x161616), MathConvert.Rgb(0x4a4a4a), MathConvert.Rgb(0x8a8a8a),
    };
    /// <summary>현재 배경 색 인덱스.</summary>
    private int _bgIndex;

    /// <summary>
    /// 패널 자식 노드를 모두 코드로 만든다(SubViewport, 배경, 환경, 카메라·헤드라이트, 그리드, SceneView, 조작기 루트, 오버레이, 파이, HUD).
    /// 이미 문서가 Bind되어 있으면 연결하고, 마지막에 렌더 설정을 적용한다.
    /// </summary>
    public override void _Ready()
    {
        // 컨테이너 설정: SubViewport를 패널 크기로 늘리고, 마우스/키보드 포커스를 받는다
        Stretch = true;
        MouseFilter = MouseFilterEnum.Stop;
        FocusMode = FocusModeEnum.All;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;
        CustomMinimumSize = new Vector2(200, 150);

        // 자체 World3D를 가진 SubViewport(MSAA 4x, 항상 렌더)
        Viewport = new SubViewport
        {
            Name = "SubViewport",
            OwnWorld3D = true,
            HandleInputLocally = false,
            Msaa3D = Godot.Viewport.Msaa.Msaa4X,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
        };
        AddChild(Viewport);

        // 기본 배경: 위 → 아래 세로 그라디언트 텍스처(Canvas 배경 모드에서 보임)
        var gradient = new Gradient();
        gradient.SetColor(0, BackgroundCycle[0]);
        gradient.SetColor(1, MathConvert.Rgb(0x2a2a2a));
        var tex = new GradientTexture2D { Gradient = gradient, Fill = GradientTexture2D.FillEnum.Linear, FillFrom = new Vector2(0, 0), FillTo = new Vector2(0, 1), Width = 4, Height = 256 };
        _background = new TextureRect { Name = "Background", Texture = tex, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = MouseFilterEnum.Ignore };
        _background.SetAnchorsPreset(LayoutPreset.FullRect);
        Viewport.AddChild(_background);

        // 환경: 기본은 캔버스 배경 + 약한 흰색 앰비언트, 선형 톤 매핑(IBL·톤 매핑은 ApplyRenderSettings가 덮어씀)
        _env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Canvas,
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color(1, 1, 1),
            AmbientLightEnergy = 0.18f,
            TonemapMode = Godot.Environment.ToneMapper.Linear,
        };
        Viewport.AddChild(new WorldEnvironment { Name = "Env", Environment = _env });

        // 카메라와 컨트롤러·내비게이션
        Camera = new Camera3D { Name = "Camera", Fov = 45, Near = 0.05f, Far = 10000f, Current = true };
        Viewport.AddChild(Camera);
        CameraController = new ViewportCamera(Camera, InitialView);
        Navigation = new NavigationHandler(this);

        // 헤드라이트는 카메라 자식이라 항상 시선 방향으로 비춘다
        HeadLight = new DirectionalLight3D { Name = "HeadLight", LightEnergy = 1.0f, ShadowEnabled = false };
        Camera.AddChild(HeadLight);

        // 3D 내용: 그리드, 문서 미러, 조작기 루트
        Grid = new GridView { Name = "Grid" };
        Viewport.AddChild(Grid);
        Scene = new SceneView { Name = "Scene" };
        // 숨겨진 패널(단일 뷰의 나머지 3개)은 재생 중 스킨 변형·업로드를 건너뛰고 다시 보일 때 한 번 갱신한다
        Scene.IsShown = IsVisibleInTree;
        VisibilityChanged += () => { if (IsVisibleInTree()) Scene.FlushSkins(); };
        Viewport.AddChild(Scene);
        GizmoRoot = new Node3D { Name = "Gizmos" };
        Viewport.AddChild(GizmoRoot);

        // 2D 위젯: 오버레이와 파이 메뉴(SubViewportContainer의 자식이라 3D 위에 그려짐)
        Overlay = new ViewportOverlay { Name = "Overlay", Camera = Camera };
        AddChild(Overlay);
        Pie = new UI.PieMenu { Name = "PieMenu" };
        AddChild(Pie);

        // 표시 상태와 카메라 변경 연동(뷰 이름 라벨, 측면 뷰 그리드 방향)
        Display = new ViewportDisplay(this);
        CameraController.Changed += () => { Overlay.CameraLabel = CameraController.Label; UpdateGridOrientation(); };
        Overlay.CameraLabel = CameraController.Label;
        UpdateGridOrientation();

        // 우상단 HUD(오른쪽 위 앵커, 왼쪽으로 자라게)
        float s = CubeApp.Instance.UiScale;
        Hud = new ViewportHud { Name = "Hud" };
        Hud.SetAnchorsPreset(LayoutPreset.TopRight);
        Hud.Position = new Vector2(-8 * s, 8 * s);
        Hud.GrowHorizontal = GrowDirection.Begin;
        AddChild(Hud);
        // 뷰포트가 좁아져도 HUD·오버레이가 패널 밖(도킹된 패널 위)으로 넘쳐 그려지지 않게
        ClipContents = true;
        Hud.Setup(this);

        if (_doc != null) { Scene.Bind(_doc); Display.Bind(_doc); }
        ApplyRenderSettings();
    }

    /// <summary>패널 WorldEnvironment의 환경(IBL·톤 매핑·포스트 이펙트 설정 대상).</summary>
    private Godot.Environment _env = null!;
    /// <summary>IBL용 Sky(처음 IBL을 켤 때 만든다).</summary>
    private Sky? _sky;
    /// <summary>HDRI 하늘 셰이더 머티리얼(<see cref="HdriBlur.SkyShader"/>).</summary>
    private ShaderMaterial? _skyMat;

    /// <summary>Settings.Render(IBL HDRI/세기/회전/배경, 톤 매핑/노출, SSAO, MSAA/FXAA, 헤드라이트/그림자)를 이 패널에 적용한다.</summary>
    public void ApplyRenderSettings()
    {
        // HDRI 로드(IBL이 꺼졌거나 실패하면 null → 일반 앰비언트 색)
        var r = CubeApp.Instance.Settings.Render;
        var tex = r.IblEnabled ? HdriLibrary.Load(r) : null;
        bool ibl = tex != null;
        if (ibl)
        {
            // PanoramaSkyMaterial 대신 하늘 셰이더: 배경 패스만 흐린 파노라마(Background Blur), 조명용 큐브맵 패스는 원본
            _skyMat ??= new ShaderMaterial { Shader = HdriBlur.SkyShader };
            _skyMat.SetShaderParameter("source", tex);
            var blurred = HdriBlur.Blurred(tex!, r.BackgroundBlur);
            _skyMat.SetShaderParameter("blurred", blurred);
            _skyMat.SetShaderParameter("use_blur", blurred != null);
            _sky ??= new Sky { SkyMaterial = _skyMat, RadianceSize = Sky.RadianceSizeEnum.Size256, ProcessMode = Sky.ProcessModeEnum.Realtime };
            _env.Sky = _sky;
        }
        else _env.Sky = null;
        // 앰비언트·반사 조명 소스: IBL이면 하늘, 아니면 단색 앰비언트 + 반사 없음
        _env.AmbientLightSource = ibl ? Godot.Environment.AmbientSource.Sky : Godot.Environment.AmbientSource.Color;
        _env.AmbientLightSkyContribution = 1f;
        _env.AmbientLightEnergy = ibl ? r.IblIntensity : 0.18f;
        _env.ReflectedLightSource = ibl ? Godot.Environment.ReflectionSource.Sky : Godot.Environment.ReflectionSource.Disabled;
        // 배경: IBL + Show Background면 하늘, 아니면 캔버스 그라디언트. 하늘 회전은 Y축(도 → 라디안)
        bool showSky = ibl && r.ShowBackground;
        _env.BackgroundMode = showSky ? Godot.Environment.BGMode.Sky : Godot.Environment.BGMode.Canvas;
        _env.BackgroundEnergyMultiplier = ibl ? MathF.Max(r.IblIntensity, 0.01f) : 1f;
        _env.SkyRotation = new Vector3(0, Mathf.DegToRad(r.IblRotation), 0);
        _background.Visible = !showSky;
        // 톤 매핑·노출·SSAO·안티앨리어싱(MSAA 단계, SMAA > FXAA)
        _env.TonemapMode = (Godot.Environment.ToneMapper)Math.Clamp(r.Tonemap, 0, 4);
        _env.TonemapExposure = r.Exposure;
        _env.SsaoEnabled = r.Ssao;
        Viewport.Msaa3D = r.Msaa switch { 0 => Godot.Viewport.Msaa.Disabled, 1 => Godot.Viewport.Msaa.Msaa2X, 2 => Godot.Viewport.Msaa.Msaa4X, _ => Godot.Viewport.Msaa.Msaa8X };
        Viewport.ScreenSpaceAA = r.Smaa ? Godot.Viewport.ScreenSpaceAAEnum.Smaa : r.Fxaa ? Godot.Viewport.ScreenSpaceAAEnum.Fxaa : Godot.Viewport.ScreenSpaceAAEnum.Disabled;
        ApplyPostEffects(r);
        // 헤드라이트·그림자, 씬 라이트(그림자 설정 반영)
        HeadLight.Visible = r.Headlight && Display.Mode != ShadingMode.Lit;
        HeadLight.ShadowEnabled = r.Shadows;
        foreach (var lv in Scene.LightViews.Values) lv.Refresh();
    }

    /// <summary>DOF·자동 노출용 카메라 속성(필요할 때만 만들어 카메라에 연결).</summary>
    private CameraAttributesPractical? _camAttr;

    /// <summary>Post Effects(Render Settings → Post Effects, Render 메뉴/셸프): Godot Environment·CameraAttributes·Viewport 기능을 켠다.</summary>
    private void ApplyPostEffects(App.RenderSettings r)
    /// <remarks>Glow/SSR/SSIL/SDFGI/Fog/Volumetric Fog/Adjustments는 Environment, DOF·자동 노출은 CameraAttributesPractical, TAA·디밴딩은 Viewport 속성이다.</remarks>
    {
        _env.GlowEnabled = r.Glow;
        _env.GlowIntensity = r.GlowIntensity;
        _env.GlowBloom = r.GlowBloom;
        _env.GlowHdrThreshold = r.GlowThreshold;
        _env.GlowBlendMode = (Godot.Environment.GlowBlendModeEnum)Math.Clamp(r.GlowBlend, 0, 4);
        _env.SsrEnabled = r.Ssr;
        _env.SsrMaxSteps = Math.Clamp(r.SsrMaxSteps, 8, 512);
        _env.SsilEnabled = r.Ssil;
        _env.SsilIntensity = r.SsilIntensity;
        _env.SdfgiEnabled = r.Sdfgi;
        _env.FogEnabled = r.Fog;
        _env.FogDensity = r.FogDensity;
        _env.FogSkyAffect = 0.5f; // HDRI 배경이 완전히 덮이지 않게
        if (r.FogColor is { Length: >= 3 } fc) _env.FogLightColor = new Color(fc[0], fc[1], fc[2]);
        _env.VolumetricFogEnabled = r.VolumetricFog;
        _env.VolumetricFogDensity = r.VolumetricFogDensity;
        _env.AdjustmentEnabled = r.Adjust;
        _env.AdjustmentBrightness = r.Brightness;
        _env.AdjustmentContrast = r.Contrast;
        _env.AdjustmentSaturation = r.Saturation;
        // 카메라 속성 효과가 하나라도 켜져 있을 때만 CameraAttributes를 연결(없으면 null로 기본 동작)
        bool camFx = r.DofFar || r.DofNear || r.AutoExposure;
        if (camFx)
        {
            _camAttr ??= new CameraAttributesPractical();
            _camAttr.DofBlurFarEnabled = r.DofFar;
            _camAttr.DofBlurFarDistance = r.DofFarDistance;
            _camAttr.DofBlurFarTransition = r.DofFarTransition;
            _camAttr.DofBlurNearEnabled = r.DofNear;
            _camAttr.DofBlurNearDistance = r.DofNearDistance;
            _camAttr.DofBlurNearTransition = r.DofNearTransition;
            _camAttr.DofBlurAmount = r.DofAmount;
            _camAttr.AutoExposureEnabled = r.AutoExposure;
        }
        Camera.Attributes = camFx ? _camAttr : null;
        Viewport.UseTaa = r.Taa;
        Viewport.UseDebanding = r.Debanding;
    }

    /// <summary>front/side/back/left 같은 측면 프리셋 뷰에서는 그리드를 뷰 평면에 세운다(Maya와 동일). 텀블하면 바닥으로 돌아간다.</summary>
    private void UpdateGridOrientation()
    {
        // Front/Back = X축 90° 회전(XY 평면), Side/Left = Z축 90° 회전(YZ 평면), 나머지 = 바닥(XZ)
        Grid.Transform = CameraController.Kind switch
        {
            ViewKind.Front or ViewKind.Back => new Transform3D(Basis.FromEuler(new Vector3(Mathf.Pi / 2, 0, 0)), Vector3.Zero),
            ViewKind.Side or ViewKind.Left => new Transform3D(Basis.FromEuler(new Vector3(0, 0, Mathf.Pi / 2)), Vector3.Zero),
            _ => Transform3D.Identity,
        };
    }

    /// <summary>매 프레임 카메라 프리셋 애니메이션을 진행한다.</summary>
    public override void _Process(double delta) => CameraController?.Update((float)delta);

    /// <summary>문서를 연결한다. _Ready 전이면 저장만 해 두고 _Ready에서 SceneView/Display에 연결한다.</summary>
    public void Bind(Document doc)
    {
        _doc = doc;
        if (Scene != null) { Scene.Bind(doc); Display.Bind(doc); }
    }

    /// <summary>바인드된 문서.</summary>
    public Document? Document => _doc;
    /// <summary>패널 종횡비(가로/세로, 높이 0이면 1). 프레임 계산에 쓴다.</summary>
    public float Aspect => Size.Y > 0 ? Size.X / Size.Y : 1f;

    /// <summary>뷰 프리셋으로 전환하고 HUD를 갱신한다(뷰 큐브·Space 파이·메뉴에서 호출).</summary>
    public void SetView(ViewKind kind)
    {
        CameraController.SetView(kind);
        Hud?.Refresh();
    }

    /// <summary>F: 선택을 프레임, 선택이 없으면 전체(A).</summary>
    /// <remarks>오브젝트 선택 + 컴포넌트를 가진 노드의 월드 AABB(컴포넌트 모드면 선택 컴포넌트 정점만)를 합쳐 카메라를 맞춘다.</remarks>
    public void FrameSelected()
    {
        if (_doc == null) return;
        var ids = _doc.Selection.Objects.ToList();
        if (_doc.Selection.IsComponentMode) ids.AddRange(_doc.Selection.NodesWithComponents(_doc.Selection.Mode));
        if (ids.Count == 0) { FrameAll(); return; }
        Aabb? total = null;
        foreach (var id in ids.Distinct())
        {
            var aabb = ComponentOrObjectAabb(id);
            if (aabb == null) continue;
            total = total == null ? aabb : total.Value.Merge(aabb.Value);
        }
        if (total == null) { FrameAll(); return; }
        CameraController.Frame(total.Value, Aspect);
    }

    /// <summary>A: 보이는 모든 메시·조인트·라이트의 월드 AABB에 카메라를 맞춘다(아무것도 없으면 원점 주변 12×12 영역).</summary>
    /// <remarks>예전에는 메시만 보아 조인트/라이트만 있는 씬에서 원점 영역을 프레임했고, 숨긴 메시도 포함했다.</remarks>
    public void FrameAll()
    {
        if (_doc == null) return;
        Aabb? total = null;
        foreach (var n in _doc.Nodes.Values)
        {
            if (n.IsRoot || n.Shape == null) continue;
            if (Scene.GetView(n.Id) is not { } v || !v.IsVisibleInTree()) continue;
            var aabb = ShapeAabb(n.Id);
            if (aabb == null) continue;
            total = total == null ? aabb : total.Value.Merge(aabb.Value);
        }
        CameraController.Frame(total ?? new Aabb(new Vector3(-6, 0, -6), new Vector3(12, 0.01f, 12)), Aspect);
    }

    /// <summary>
    /// 오브젝트의 월드 AABB(F 프레임). 메시는 렌더 점(변형·스킨 표시 위치 반영), 조인트·라이트는 월드 위치 한 점,
    /// 셰이프가 없는 그룹 노드는 자손들의 AABB 합. 대상이 없으면 null.
    /// </summary>
    /// <remarks>예전에는 메시만 다뤄 조인트·라이트·그룹을 선택하고 F를 누르면 씬 전체를 프레임했다.</remarks>
    private Aabb? ObjectAabb(NodeId id)
    {
        if (ShapeAabb(id) is { } own) return own;
        var n = _doc?.Find(id);
        if (n == null || n.Shape != null) return null;
        Aabb? box = null;
        foreach (var d in n.Descendants())
            if (ShapeAabb(d.Id) is { } b) box = box == null ? b : box.Value.Merge(b);
        return box;
    }

    /// <summary>노드 자신의 셰이프(메시 렌더 점 / 조인트·라이트 위치)만의 월드 AABB. 셰이프가 없으면 null.</summary>
    private Aabb? ShapeAabb(NodeId id)
    {
        var mv = Scene.GetMeshView(id);
        if (mv != null)
        {
            if (mv.Render.PointCount == 0) return null;
            var xf = mv.GlobalTransform;
            Aabb? box = null;
            for (int i = 0; i < mv.Render.PointCount; i++)
            {
                var p = xf * mv.Render.PointPositions[i].ToGodot();
                box = box == null ? new Aabb(p, Vector3.Zero) : box.Value.Expand(p);
            }
            return box;
        }
        var n = _doc?.Find(id);
        if (n != null && (n.IsJoint || n.IsLight) && Scene.GetView(id) is { } v) return new Aabb(v.GlobalPosition, Vector3.Zero);
        return null;
    }

    /// <summary>
    /// 컴포넌트 모드에서 이 노드에 선택된 컴포넌트가 있으면 그 정점들(엣지 = 양끝, 면 = 모든 정점)의 월드 AABB, 아니면 오브젝트 AABB.
    /// </summary>
    private Aabb? ComponentOrObjectAabb(NodeId id)
    {
        if (_doc == null) return null;
        var sel = _doc.Selection;
        if (!sel.IsComponentMode || !sel.Components.TryGetValue(id, out var comps)) return ObjectAabb(id);
        var mv = Scene.GetMeshView(id);
        var node = _doc.Find(id);
        if (mv == null || node?.Mesh == null) return null;
        var mesh = node.Mesh;
        var xf = mv.GlobalTransform;
        // 선택된 정점·엣지 양끝·면 정점을 하나의 정점 집합으로 모은다
        var verts = new HashSet<int>();
        var tmp = new List<int>();
        foreach (int v in comps.Verts) verts.Add(v);
        foreach (int e in comps.Edges) { var (a, b) = mesh.EdgeVertices(e); verts.Add(a); verts.Add(b); }
        foreach (int f in comps.Faces) { mesh.GetFaceVertices(f, tmp); foreach (var v in tmp) verts.Add(v); }
        if (verts.Count == 0) return ObjectAabb(id);
        Aabb? box = null;
        foreach (int v in verts)
        {
            var p = xf * mesh.Verts[v].Position.ToGodot();
            box = box == null ? new Aabb(p, Vector3.Zero) : box.Value.Expand(p);
        }
        return box;
    }

    /// <summary>배경 색을 다음 프리셋으로 순환한다(첫 색만 그라디언트, 나머지는 단색).</summary>
    public void CycleBackground()
    {
        _bgIndex = (_bgIndex + 1) % BackgroundCycle.Length;
        var tex = (GradientTexture2D)_background.Texture;
        var g = tex.Gradient;
        var top = BackgroundCycle[_bgIndex];
        g.SetColor(0, top);
        g.SetColor(1, _bgIndex == 0 ? MathConvert.Rgb(0x2a2a2a) : top);
    }

    // ---------------------------------------------------------------- 입력

    /// <summary>이 패널이 마지막으로 받은 마우스 위치(로컬). OS 커서 위치 대신 쓰므로 주입된 입력에서도 맞다.</summary>
    public Vector2 LastMouseLocal { get; private set; }

    /// <summary>현재 툴이 모달(대화형)인지. 셸이 설정한다.</summary>
    public Func<bool>? ModalTool;

    /// <summary>패널 입력 진입점. 활성 패널 표시·포커스를 처리한 뒤 모달 툴 → 내비게이션 → 파이 → 툴 순으로 넘기고, 소비되면 AcceptEvent.</summary>
    public override void _GuiInput(InputEvent e)
    {
        // 마우스 이벤트마다 마지막 위치 기록, 처음 들어오면 활성 패널로. 버튼을 누르면 키보드 포커스도 가져온다
        if (e is InputEventMouse me) { LastMouseLocal = me.Position; if (!IsMouseOver) { IsMouseOver = true; Activated?.Invoke(); } }
        if (e is InputEventMouseButton { Pressed: true }) { GrabFocus(); Activated?.Invoke(); }
        bool modal = ModalTool?.Invoke() == true;
        // 모달 툴: 휠과 Alt 없는 마우스 버튼은 줌/파이보다 먼저 툴로(Blender Bevel의 휠 = 세그먼트, RMB = 취소)
        if (modal && e is InputEventMouseButton { AltPressed: false } && ToolInput != null && ToolInput(e)) { AcceptEvent(); return; }
        // 내비게이션이 처리했거나 내비게이션 드래그 중이면 다른 처리 없이 소비
        if (Navigation.Handle(e)) { AcceptEvent(); return; }
        if (Navigation.IsDragging) { AcceptEvent(); return; }
        if (!modal && HandlePie(e)) { AcceptEvent(); return; }
        if (ToolInput != null && ToolInput(e)) { AcceptEvent(); return; }
    }

    /// <summary>RMB를 누르면(Alt 없이) 파이 메뉴를 열고, 누른 채 이동하면 하이라이트, 떼면 실행/닫기. Space 파이도 같은 경로로 이동/실행된다.</summary>
    private bool HandlePie(InputEvent e)
    {
        if (Pie == null) return false;
        // RMB 누름: 열린 sticky 서브 파이는 닫고, 아니면 (Shift, Ctrl) 조합에 맞는 파이를 연다. RMB 뗌: 하이라이트 항목 실행
        switch (e)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Right } mb:
                if (mb.Pressed)
                {
                    if (Pie.IsOpen && Pie.Sticky) { Pie.Close(); return true; }
                    if (mb.AltPressed || PieItems == null || Pie.IsOpen) return Pie.IsOpen;
                    Pie.Open(PieItems(mb.ShiftPressed, mb.CtrlPressed), mb.Position);
                    return Pie.IsOpen;
                }
                if (Pie.IsOpen)
                {
                    if (Pie.Sticky) return true; // 서브 파이는 LMB 클릭으로 고른다
                    ExecutePie(Pie.Release());
                    return true;
                }
                return false;
            // 파이가 열린 동안: 이동 = 하이라이트 갱신, sticky면 LMB 클릭 = 선택, 그 밖의 버튼은 삼킨다
            case InputEventMouseMotion mm when Pie.IsOpen:
                Pie.UpdatePointer(mm.Position);
                return true;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } when Pie.IsOpen && Pie.Sticky:
                ExecutePie(Pie.Release());
                return true;
            case InputEventMouseButton when Pie.IsOpen:
                return true;
        }
        return false;
    }

    /// <summary>고른 항목 실행. 하위 목록이 있는 항목이면 같은 자리에 서브 파이를 연다(버튼을 뗀 뒤에도 열려 있고 LMB로 고른다).</summary>
    private void ExecutePie(UI.PieItem? chosen)
    {
        if (chosen == null || !chosen.Enabled) return;
        if (chosen.Sub != null) { Pie.Open(chosen.Sub(), Pie.Center, sticky: true, title: chosen.Label); return; }
        PieExecute?.Invoke(chosen);
    }

    /// <summary>키보드(Space)로 여는 파이: 현재 마우스 위치에 연다.</summary>
    public void OpenPieAtMouse(IEnumerable<UI.PieItem> items) => Pie.Open(items, LastMouseLocal);

    /// <summary>열린 파이를 닫고 하이라이트된 항목을 돌려준다(Space 파이를 키를 뗄 때 실행하기 위해). 닫혀 있으면 null.</summary>
    public UI.PieItem? ReleasePie() => Pie.IsOpen ? Pie.Release() : null;

    /// <summary>마우스가 이 패널 위에 있는지(MouseEnter/Exit 알림과 마우스 이벤트로 갱신).</summary>
    public bool IsMouseOver { get; private set; }

    /// <summary>크기 변경 계측, 앱 포커스 상실 시 내비게이션·파이 취소, 마우스 진입/이탈 추적.</summary>
    public override void _Notification(int what)
    {
        if (what == NotificationResized) UiPerf.Count("vpResize");
        if (what == NotificationApplicationFocusOut) { Navigation.Cancel(); Pie?.Close(); }
        if (what == NotificationMouseEnter) { IsMouseOver = true; Activated?.Invoke(); }
        if (what == NotificationMouseExit) IsMouseOver = false;
    }

    /// <summary>핫키 "viewport" 컨텍스트: 마우스가 위에 있거나 포커스를 가진 경우.</summary>
    public bool IsViewportContext => IsMouseOver || HasFocus();

    /// <summary>Maya J 홀드: 회전/스케일 증분 스냅.</summary>
    public bool IsSnapHeld => UI.Shell.Instance?.Hotkeys.HeldKeys.Contains(Key.J) ?? false;
    /// <summary>Maya X 홀드(또는 상태 라인 Snap to Grid 토글): 그리드 스냅.</summary>
    public bool IsGridSnapHeld => (UI.Shell.Instance?.Hotkeys.HeldKeys.Contains(Key.X) ?? false) || CubeApp.Instance.Settings.SnapToGrid;
    /// <summary>Maya V 홀드(또는 상태 라인 Snap to Points 토글): 점(정점) 스냅. 점 스냅이 그리드 스냅보다 우선한다.</summary>
    public bool IsPointSnapHeld => (UI.Shell.Instance?.Hotkeys.HeldKeys.Contains(Key.V) ?? false) || CubeApp.Instance.Settings.SnapToPoints;

    /// <summary>입력을 SubViewport 안으로 전파하지 않는다(3D 노드는 입력을 받지 않고 모든 처리는 <see cref="_GuiInput"/>에서).</summary>
    public override bool _PropagateInputEvent(InputEvent @event) => false;
}
