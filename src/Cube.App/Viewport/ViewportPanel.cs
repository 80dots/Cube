using Cube.App.Bridge;
using Cube.Core.Camera;
using Cube.Core.Scene;
using Godot;

namespace Cube.App.Viewport;

/// <summary>
/// 3D 뷰포트 위젯. SubViewport에 자체 World3D를 두고 카메라·헤드라이트·그리드·SceneView를 담는다.
/// 입력은 이 컨테이너의 _GuiInput에서만 받아 내비게이션 → 파이 메뉴 → 툴 순으로 넘긴다.
/// </summary>
public partial class ViewportPanel : SubViewportContainer
{
    public ViewKind InitialView = ViewKind.Persp;

    public SubViewport Viewport { get; private set; } = null!;
    public Camera3D Camera { get; private set; } = null!;
    public ViewportCamera CameraController { get; private set; } = null!;
    public NavigationHandler Navigation { get; private set; } = null!;
    public DirectionalLight3D HeadLight { get; private set; } = null!;
    public GridView Grid { get; private set; } = null!;
    public SceneView Scene { get; private set; } = null!;
    public ViewportOverlay Overlay { get; private set; } = null!;
    public ViewportHud Hud { get; private set; } = null!;
    public Node3D GizmoRoot { get; private set; } = null!;
    public ViewportDisplay Display { get; private set; } = null!;
    public UI.PieMenu Pie { get; private set; } = null!;

    private Picker? _picker;
    public Picker Picker => _picker ??= new Picker(this);

    /// <summary>내비게이션/파이가 소비하지 않은 이벤트를 받는다(툴 라우팅). true를 반환하면 소비.</summary>
    public Func<InputEvent, bool>? ToolInput;
    /// <summary>RMB 파이 메뉴 항목 공급자(shift 여부 → 항목). Shell이 설정한다.</summary>
    /// <summary>(shift, ctrl) → 파이 항목. RMB = 기본(모드), Shift+RMB = Edit, Ctrl+RMB = Select.</summary>
    public Func<bool, bool, IEnumerable<UI.PieItem>>? PieItems;
    public Action<UI.PieItem>? PieExecute;
    /// <summary>마우스가 들어오거나 버튼이 눌리면 발생(활성 패널 전환).</summary>
    public event Action? Activated;

    private Document? _doc;
    private TextureRect _background = null!;

    public static readonly Color[] BackgroundCycle =
    {
        MathConvert.Rgb(0x3a3a3a), MathConvert.Rgb(0x161616), MathConvert.Rgb(0x4a4a4a), MathConvert.Rgb(0x8a8a8a),
    };
    private int _bgIndex;

    public override void _Ready()
    {
        Stretch = true;
        MouseFilter = MouseFilterEnum.Stop;
        FocusMode = FocusModeEnum.All;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;
        CustomMinimumSize = new Vector2(200, 150);

        Viewport = new SubViewport
        {
            Name = "SubViewport",
            OwnWorld3D = true,
            HandleInputLocally = false,
            Msaa3D = Godot.Viewport.Msaa.Msaa4X,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
        };
        AddChild(Viewport);

        var gradient = new Gradient();
        gradient.SetColor(0, BackgroundCycle[0]);
        gradient.SetColor(1, MathConvert.Rgb(0x2a2a2a));
        var tex = new GradientTexture2D { Gradient = gradient, Fill = GradientTexture2D.FillEnum.Linear, FillFrom = new Vector2(0, 0), FillTo = new Vector2(0, 1), Width = 4, Height = 256 };
        _background = new TextureRect { Name = "Background", Texture = tex, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = MouseFilterEnum.Ignore };
        _background.SetAnchorsPreset(LayoutPreset.FullRect);
        Viewport.AddChild(_background);

        _env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Canvas,
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color(1, 1, 1),
            AmbientLightEnergy = 0.18f,
            TonemapMode = Godot.Environment.ToneMapper.Linear,
        };
        Viewport.AddChild(new WorldEnvironment { Name = "Env", Environment = _env });

        Camera = new Camera3D { Name = "Camera", Fov = 45, Near = 0.05f, Far = 10000f, Current = true };
        Viewport.AddChild(Camera);
        CameraController = new ViewportCamera(Camera, InitialView);
        Navigation = new NavigationHandler(this);

        HeadLight = new DirectionalLight3D { Name = "HeadLight", LightEnergy = 1.0f, ShadowEnabled = false };
        Camera.AddChild(HeadLight);

        Grid = new GridView { Name = "Grid" };
        Viewport.AddChild(Grid);
        Scene = new SceneView { Name = "Scene" };
        // 숨겨진 패널(단일 뷰의 나머지 3개)은 재생 중 스킨 변형·업로드를 건너뛰고 다시 보일 때 한 번 갱신한다
        Scene.IsShown = IsVisibleInTree;
        VisibilityChanged += () => { if (IsVisibleInTree()) Scene.FlushSkins(); };
        Viewport.AddChild(Scene);
        GizmoRoot = new Node3D { Name = "Gizmos" };
        Viewport.AddChild(GizmoRoot);

        Overlay = new ViewportOverlay { Name = "Overlay", Camera = Camera };
        AddChild(Overlay);
        Pie = new UI.PieMenu { Name = "PieMenu" };
        AddChild(Pie);

        Display = new ViewportDisplay(this);
        CameraController.Changed += () => { Overlay.CameraLabel = CameraController.Label; UpdateGridOrientation(); };
        Overlay.CameraLabel = CameraController.Label;
        UpdateGridOrientation();

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

    private Godot.Environment _env = null!;
    private Sky? _sky;
    private ShaderMaterial? _skyMat;

    /// <summary>Settings.Render(IBL HDRI/세기/회전/배경, 톤 매핑/노출, SSAO, MSAA/FXAA, 헤드라이트/그림자)를 이 패널에 적용한다.</summary>
    public void ApplyRenderSettings()
    {
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
        _env.AmbientLightSource = ibl ? Godot.Environment.AmbientSource.Sky : Godot.Environment.AmbientSource.Color;
        _env.AmbientLightSkyContribution = 1f;
        _env.AmbientLightEnergy = ibl ? r.IblIntensity : 0.18f;
        _env.ReflectedLightSource = ibl ? Godot.Environment.ReflectionSource.Sky : Godot.Environment.ReflectionSource.Disabled;
        bool showSky = ibl && r.ShowBackground;
        _env.BackgroundMode = showSky ? Godot.Environment.BGMode.Sky : Godot.Environment.BGMode.Canvas;
        _env.BackgroundEnergyMultiplier = ibl ? MathF.Max(r.IblIntensity, 0.01f) : 1f;
        _env.SkyRotation = new Vector3(0, Mathf.DegToRad(r.IblRotation), 0);
        _background.Visible = !showSky;
        _env.TonemapMode = (Godot.Environment.ToneMapper)Math.Clamp(r.Tonemap, 0, 4);
        _env.TonemapExposure = r.Exposure;
        _env.SsaoEnabled = r.Ssao;
        Viewport.Msaa3D = r.Msaa switch { 0 => Godot.Viewport.Msaa.Disabled, 1 => Godot.Viewport.Msaa.Msaa2X, 2 => Godot.Viewport.Msaa.Msaa4X, _ => Godot.Viewport.Msaa.Msaa8X };
        Viewport.ScreenSpaceAA = r.Fxaa ? Godot.Viewport.ScreenSpaceAAEnum.Fxaa : Godot.Viewport.ScreenSpaceAAEnum.Disabled;
        HeadLight.Visible = r.Headlight && Display.Mode != ShadingMode.Lit;
        HeadLight.ShadowEnabled = r.Shadows;
        foreach (var lv in Scene.LightViews.Values) lv.Refresh();
    }

    /// <summary>front/side/back/left 같은 측면 프리셋 뷰에서는 그리드를 뷰 평면에 세운다(Maya와 동일). 텀블하면 바닥으로 돌아간다.</summary>
    private void UpdateGridOrientation()
    {
        Grid.Transform = CameraController.Kind switch
        {
            ViewKind.Front or ViewKind.Back => new Transform3D(Basis.FromEuler(new Vector3(Mathf.Pi / 2, 0, 0)), Vector3.Zero),
            ViewKind.Side or ViewKind.Left => new Transform3D(Basis.FromEuler(new Vector3(0, 0, Mathf.Pi / 2)), Vector3.Zero),
            _ => Transform3D.Identity,
        };
    }

    public override void _Process(double delta) => CameraController?.Update((float)delta);

    public void Bind(Document doc)
    {
        _doc = doc;
        if (Scene != null) { Scene.Bind(doc); Display.Bind(doc); }
    }

    public Document? Document => _doc;
    public float Aspect => Size.Y > 0 ? Size.X / Size.Y : 1f;

    public void SetView(ViewKind kind)
    {
        CameraController.SetView(kind);
        Hud?.Refresh();
    }

    /// <summary>F: 선택을 프레임, 선택이 없으면 전체(A).</summary>
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

    public void FrameAll()
    {
        if (_doc == null) return;
        Aabb? total = null;
        foreach (var n in _doc.MeshNodes())
        {
            var aabb = ObjectAabb(n.Id);
            if (aabb == null) continue;
            total = total == null ? aabb : total.Value.Merge(aabb.Value);
        }
        CameraController.Frame(total ?? new Aabb(new Vector3(-6, 0, -6), new Vector3(12, 0.01f, 12)), Aspect);
    }

    private Aabb? ObjectAabb(NodeId id)
    {
        var mv = Scene.GetMeshView(id);
        if (mv == null || mv.Render.PointCount == 0) return null;
        var xf = mv.GlobalTransform;
        Aabb? box = null;
        for (int i = 0; i < mv.Render.PointCount; i++)
        {
            var p = xf * mv.Render.PointPositions[i].ToGodot();
            box = box == null ? new Aabb(p, Vector3.Zero) : box.Value.Expand(p);
        }
        return box;
    }

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

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouse me) { LastMouseLocal = me.Position; if (!IsMouseOver) { IsMouseOver = true; Activated?.Invoke(); } }
        if (e is InputEventMouseButton { Pressed: true }) { GrabFocus(); Activated?.Invoke(); }
        bool modal = ModalTool?.Invoke() == true;
        // 모달 툴: 휠과 Alt 없는 마우스 버튼은 줌/파이보다 먼저 툴로(Blender Bevel의 휠 = 세그먼트, RMB = 취소)
        if (modal && e is InputEventMouseButton { AltPressed: false } && ToolInput != null && ToolInput(e)) { AcceptEvent(); return; }
        if (Navigation.Handle(e)) { AcceptEvent(); return; }
        if (Navigation.IsDragging) { AcceptEvent(); return; }
        if (!modal && HandlePie(e)) { AcceptEvent(); return; }
        if (ToolInput != null && ToolInput(e)) { AcceptEvent(); return; }
    }

    /// <summary>RMB를 누르면(Alt 없이) 파이 메뉴를 열고, 누른 채 이동하면 하이라이트, 떼면 실행/닫기. Space 파이도 같은 경로로 이동/실행된다.</summary>
    private bool HandlePie(InputEvent e)
    {
        if (Pie == null) return false;
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

    public UI.PieItem? ReleasePie() => Pie.IsOpen ? Pie.Release() : null;

    public bool IsMouseOver { get; private set; }

    public override void _Notification(int what)
    {
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

    public override bool _PropagateInputEvent(InputEvent @event) => false;
}
