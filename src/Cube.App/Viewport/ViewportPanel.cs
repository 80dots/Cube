using Cube.App.Bridge;
using Cube.Core.Camera;
using Cube.Core.Scene;
using Godot;

namespace Cube.App.Viewport;

/// <summary>
/// 3D 뷰포트 위젯. SubViewport에 자체 World3D를 두고 카메라·헤드라이트·그리드·SceneView를 담는다.
/// 입력은 이 컨테이너의 _GuiInput에서만 받아 내비게이션 → 툴 순으로 넘긴다.
/// </summary>
public partial class ViewportPanel : SubViewportContainer
{
    public SubViewport Viewport { get; private set; } = null!;
    public Camera3D Camera { get; private set; } = null!;
    public ViewportCamera CameraController { get; private set; } = null!;
    public NavigationHandler Navigation { get; private set; } = null!;
    public DirectionalLight3D HeadLight { get; private set; } = null!;
    public GridView Grid { get; private set; } = null!;
    public SceneView Scene { get; private set; } = null!;
    public ViewportOverlay Overlay { get; private set; } = null!;
    public Node3D GizmoRoot { get; private set; } = null!;

    /// <summary>내비게이션이 소비하지 않은 이벤트를 받는다(툴 라우팅). true를 반환하면 소비.</summary>
    public Func<InputEvent, bool>? ToolInput;

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

        // 배경 그라디언트: Environment Canvas 모드로 SubViewport의 2D 캔버스를 배경으로 그린다
        var gradient = new Gradient();
        gradient.SetColor(0, BackgroundCycle[0]);
        gradient.SetColor(1, MathConvert.Rgb(0x2a2a2a));
        var tex = new GradientTexture2D { Gradient = gradient, Fill = GradientTexture2D.FillEnum.Linear, FillFrom = new Vector2(0, 0), FillTo = new Vector2(0, 1), Width = 4, Height = 256 };
        _background = new TextureRect { Name = "Background", Texture = tex, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = MouseFilterEnum.Ignore };
        _background.SetAnchorsPreset(LayoutPreset.FullRect);
        Viewport.AddChild(_background);

        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Canvas,
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color(1, 1, 1),
            AmbientLightEnergy = 0.18f,
            TonemapMode = Godot.Environment.ToneMapper.Linear,
        };
        Viewport.AddChild(new WorldEnvironment { Name = "Env", Environment = env });

        Camera = new Camera3D { Name = "Camera", Fov = 45, Near = 0.05f, Far = 10000f, Current = true };
        Viewport.AddChild(Camera);
        CameraController = new ViewportCamera(Camera);
        Navigation = new NavigationHandler(this);

        HeadLight = new DirectionalLight3D { Name = "HeadLight", LightEnergy = 1.0f, ShadowEnabled = false };
        Camera.AddChild(HeadLight); // 카메라를 따라가는 헤드라이트(Maya 기본 라이팅)

        Grid = new GridView { Name = "Grid" };
        Viewport.AddChild(Grid);

        Scene = new SceneView { Name = "Scene" };
        Viewport.AddChild(Scene);

        GizmoRoot = new Node3D { Name = "Gizmos" };
        Viewport.AddChild(GizmoRoot);

        Overlay = new ViewportOverlay { Name = "Overlay", Camera = Camera };
        AddChild(Overlay);
        CameraController.Changed += () => Overlay.CameraLabel = CameraController.Label;

        Display = new ViewportDisplay(this);

        if (_doc != null) { Scene.Bind(_doc); Display.Bind(_doc); }
    }

    public ViewportDisplay Display { get; private set; } = null!;
    private Picker? _picker;
    public Picker Picker => _picker ??= new Picker(this);

    public void Bind(Document doc)
    {
        _doc = doc;
        if (Scene != null) { Scene.Bind(doc); Display.Bind(doc); }
    }

    public Document? Document => _doc;

    public float Aspect => Size.Y > 0 ? Size.X / Size.Y : 1f;

    public void SetView(ViewKind kind) => CameraController.SetView(kind);

    /// <summary>F: 선택을 프레임, 선택이 없으면 전체(A).</summary>
    public void FrameSelected()
    {
        if (_doc == null) return;
        var ids = _doc.Selection.Objects.ToList();
        if (_doc.Selection.IsComponentMode)
            ids.AddRange(_doc.Selection.NodesWithComponents(_doc.Selection.Mode));
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

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseButton { Pressed: true }) GrabFocus();
        if (Navigation.Handle(e)) { AcceptEvent(); return; }
        if (Navigation.IsDragging) { AcceptEvent(); return; }
        if (ToolInput != null && ToolInput(e)) { AcceptEvent(); return; }
    }

    public bool IsMouseOver { get; private set; }

    public override void _Notification(int what)
    {
        if (what == NotificationApplicationFocusOut) Navigation.Cancel();
        if (what == NotificationMouseEnter) IsMouseOver = true;
        if (what == NotificationMouseExit) IsMouseOver = false;
    }

    /// <summary>핫키 "viewport" 컨텍스트: 마우스가 위에 있거나 포커스를 가진 경우.</summary>
    public bool IsViewportContext => IsMouseOver || HasFocus();

    public override bool _PropagateInputEvent(InputEvent @event) => false;
}
