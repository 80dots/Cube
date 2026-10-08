using Cube.App.Hotkeys;
using Cube.App.Tools;
using Cube.App.Viewport;
using Cube.Core.Scene;
using Cube.Core.Selection;
using Godot;

namespace Cube.App.UI;

/// <summary>
/// Maya 식 셸 레이아웃: 메뉴바 / 상태 라인 / 셸프 / [툴박스 | 아웃라이너 | 뷰포트(1 또는 4분할) | Properties] / 헬프 라인.
/// 레이아웃은 코드로 구성한다(.tscn은 루트만). 모든 UI는 ActionRegistry의 액션만 호출한다.
/// </summary>
public partial class Shell : Control
{
    public static Shell Instance { get; private set; } = null!;

    public MenuBar MenuBar { get; private set; } = null!;
    public HBoxContainer StatusLine { get; private set; } = null!;
    public TabContainer Shelf { get; private set; } = null!;
    /// <summary>셸프 줄 전체: [탭 셸프(확장)] | [Bridge 영역(오른쪽 끝)].</summary>
    public HBoxContainer ShelfRow { get; private set; } = null!;
    public HBoxContainer BridgeShelf { get; private set; } = null!;
    public ActionPopup ActionPopup { get; private set; } = null!;
    public HBoxContainer PolyShelf { get; private set; } = null!;
    public HBoxContainer UvShelf { get; private set; } = null!;
    public HBoxContainer RigShelf { get; private set; } = null!;
    public HBoxContainer LightShelf { get; private set; } = null!;
    public HBoxContainer RenderShelf { get; private set; } = null!;
    private readonly List<(Button button, string action)> _shelfButtons = new();
    public VBoxContainer ToolBox { get; private set; } = null!;
    /// <summary>Outliner를 담은 도킹 가능한 패널(기본: 왼쪽 도크).</summary>
    public FloatingPanel OutlinerWindow { get; private set; } = null!;
    /// <summary>Properties를 담은 도킹 가능한 패널(기본: 오른쪽 도크).</summary>
    public FloatingPanel PropertiesWindow { get; private set; } = null!;
    public Docking.DockSide LeftDock { get; private set; } = null!;
    public Docking.DockSide RightDock { get; private set; } = null!;
    public Docking.DockManager Dock { get; private set; } = null!;
    public Docks.Outliner Outliner { get; private set; } = null!;
    public ViewportLayout Layout { get; private set; } = null!;
    /// <summary>활성 뷰포트(마우스가 들어갔거나 눌린 패널). 툴·핫키·표시 액션의 대상.</summary>
    public ViewportPanel Viewport => Layout.Active;

    public Docks.PropertiesPanel Properties { get; private set; } = null!;
    public Label HelpLine { get; private set; } = null!;

    public ActionRegistry Actions { get; } = new();
    public ShellInput Hotkeys { get; private set; } = null!;
    public ToolManager Tools { get; private set; } = null!;
    public ToolContext ToolContext { get; private set; } = null!;
    public MenuBuilder Menus { get; private set; } = null!;
    public IO.FileActions Files { get; private set; } = null!;
    public IO.SceneFileActions SceneFiles { get; private set; } = null!;

    private readonly Dictionary<SelectMode, Button> _modeButtons = new();
    private readonly Dictionary<string, Button> _toolButtons = new();
    private readonly Dictionary<AxisOrientation, Button> _axisButtons = new();
    private CheckBox _cameraBased = null!;
    private Button _snapGrid = null!, _snapPoint = null!;
    private Button _undoBtn = null!, _redoBtn = null!;
    private HSplitContainer _mainSplit = null!, _rightSplit = null!;
    private bool _maximized;

    public Document Document => CubeApp.Instance.Document;
    /// <summary>뷰포트 좌상단 Poly Count HUD 수치(모든 패널이 공유).</summary>
    public Viewport.PolyCount PolyCount { get; } = new();
    private bool _polyCountDirty;

    public override void _Process(double delta)
    {
        if (_polyCountDirty) { _polyCountDirty = false; PolyCount.Recompute(Document); }
        UpdateLog();
    }
    public Settings Settings => CubeApp.Instance.Settings;

    public override void _Ready()
    {
        Instance = this;
        float s = CubeApp.Instance.UiScale;
        Theme = MayaTheme.Build(s);
        SetAnchorsPreset(LayoutPreset.FullRect);

        var bg = new Panel { Name = "Background", MouseFilter = MouseFilterEnum.Ignore };
        bg.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(bg);

        var root = new VBoxContainer { Name = "Root" };
        root.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(root);

        MenuBar = new MenuBar { Name = "MenuBar", Flat = true };
        root.AddChild(MenuBar);

        StatusLine = new HBoxContainer { Name = "StatusLine", CustomMinimumSize = new Vector2(0, 28 * s) };
        root.AddChild(Wrap(StatusLine, MayaTheme.PanelDark));

        // 셸프: Maya처럼 탭(Polygons / UV / Rigging)마다 아이콘+텍스트 버튼 줄
        // 셸프 줄을 둘로 나눈다: 왼쪽은 탭 셸프(남는 폭), 오른쪽 끝은 Bridge 영역(외부 앱 연동 기능 전부, 아이콘 버튼)
        ShelfRow = new HBoxContainer { Name = "ShelfRow", CustomMinimumSize = new Vector2(0, 108 * s) };
        ShelfRow.AddThemeConstantOverride("separation", 0);
        root.AddChild(ShelfRow);
        Shelf = new TabContainer { Name = "Shelf", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        StyleShelfTabs(s);
        PolyShelf = MakeShelfTab("Polygons");
        UvShelf = MakeShelfTab("UV");
        RigShelf = MakeShelfTab("Rigging");
        LightShelf = MakeShelfTab("Light");
        RenderShelf = MakeShelfTab("Render");
        ShelfRow.AddChild(Shelf);
        ShelfRow.AddChild(new VSeparator());
        var bridgePanel = new PanelContainer { Name = "BridgePanel" };
        bridgePanel.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = MayaTheme.PanelDark, ContentMarginLeft = 4 * s, ContentMarginRight = 4 * s, ContentMarginTop = 2 * s });
        var bridgeBox = new VBoxContainer();
        bridgeBox.AddThemeConstantOverride("separation", (int)(2 * s));
        var bridgeTitle = new Label { Text = "Bridge" };
        bridgeTitle.AddThemeColorOverride("font_color", MayaTheme.TextDim);
        bridgeBox.AddChild(bridgeTitle);
        BridgeShelf = new HBoxContainer { Name = "BridgeItems" };
        bridgeBox.AddChild(BridgeShelf);
        bridgePanel.AddChild(bridgeBox);
        ShelfRow.AddChild(bridgePanel);

        // --- 중앙
        _mainSplit = new HSplitContainer { Name = "MainSplit", SizeFlagsVertical = SizeFlags.ExpandFill };
        root.AddChild(_mainSplit);

        ToolBox = new VBoxContainer { Name = "ToolBox", CustomMinimumSize = new Vector2(40 * s, 0) };
        // 왼쪽 행 = 툴박스 + 왼쪽 도크(스플리터가 폭을 정함), 오른쪽 = 뷰포트 + 오른쪽 도크.
        // 도크에는 Outliner/Properties와 떠 있는 패널(UV Editor 등)을 탭으로 붙일 수 있다(UI/Docking/DockManager.cs).
        var leftRow = new HBoxContainer { Name = "Left", SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.Fill };
        leftRow.AddChild(Wrap(ToolBox, MayaTheme.PanelDark, expandH: false));
        LeftDock = new Docking.DockSide { Name = "LeftDock", SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        leftRow.AddChild(LeftDock);
        _mainSplit.AddChild(leftRow);

        _rightSplit = new HSplitContainer { Name = "RightSplit", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _mainSplit.AddChild(_rightSplit);

        Layout = new ViewportLayout { Name = "ViewportLayout", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _rightSplit.AddChild(Layout);
        RightDock = new Docking.DockSide { Name = "RightDock", SizeFlagsHorizontal = SizeFlags.Fill, SizeFlagsVertical = SizeFlags.ExpandFill };
        _rightSplit.AddChild(RightDock);

        // Outliner / Properties도 떼어 낼 수 있는 패널이다(처음 자리는 레이아웃 복원이 정함)
        OutlinerWindow = MakeDockablePanel("outliner", "Outliner", new Vector2(260, 520) * s);
        Outliner = new Docks.Outliner { Name = "Outliner" };
        OutlinerWindow.Content.AddChild(Outliner);
        OutlinerWindow.LastDock = new Docking.DockSlot(Docking.DockSideKind.Left, 0);
        PropertiesWindow = MakeDockablePanel("properties", "Properties", new Vector2(300, 560) * s);
        Properties = new Docks.PropertiesPanel { Name = "Properties" };
        PropertiesWindow.Content.AddChild(Properties);
        PropertiesWindow.LastDock = new Docking.DockSlot(Docking.DockSideKind.Right, 0);

        HelpLine = new Label { Name = "HelpLine", Text = "Select a tool.", CustomMinimumSize = new Vector2(0, 20 * s) };
        root.AddChild(Wrap(BuildHelpRow(s), MayaTheme.PanelDark));
        // Time Slider(가져온 애니메이션 재생): HelpLine 바로 위
        BuildTimeSlider(root, root.GetChildCount() - 1, s);

        Dock = new Docking.DockManager { Name = "DockManager" };
        AddChild(Dock);
        Dock.Setup(this, LeftDock, RightDock, _mainSplit, _rightSplit);
        Dock.Register(OutlinerWindow);
        Dock.Register(PropertiesWindow);

        Layout.Bind(Document);
        Outliner.Bind(Document);
        Properties.Bind(Document);

        // --- 서비스
        ToolContext = new ToolContext { Doc = Document, Viewport = Layout.Active, Settings = Settings, SetHelp = t => HelpLine.Text = t };
        ToolContext.AxisOrientation = Enum.TryParse<AxisOrientation>(Settings.AxisOrientation, out var ao) ? ao : AxisOrientation.World;
        ToolContext.AxisOrientationChanged += _ => { Settings.AxisOrientation = ToolContext.AxisOrientation.ToString(); SyncAxisButtons(); };
        Tools = new ToolManager(ToolContext);
        RegisterTools();
        foreach (var p in Layout.Panels)
        {
            var panel = p;
            panel.ToolInput = e => Layout.Active == panel && Tools.HandleInput(e);
            panel.ModalTool = () => Tools.Current is IModalTool;
            panel.PieItems = (shift, ctrl) => ctrl ? PieMenus.SelectMenu(this) : shift ? PieMenus.ContextMenu(this) : PieMenus.ModeMenu(this);
            panel.PieExecute = item => { if (item.Run != null) item.Run(); else Actions.Invoke(item.ActionId); };
        }
        Layout.ActiveChanged += p => ToolContext.Viewport = p;

        Hotkeys = new ShellInput { Name = "ShellInput", Actions = Actions, IsViewportContext = () => Layout.AnyHovered };
        Hotkeys.SpaceDown += OnSpaceDown;
        Hotkeys.SpaceUp += OnSpaceUp;
        AddChild(Hotkeys);

        RegisterActions();
        Menus = new MenuBuilder(Actions, Hotkeys.Map);
        BuildMenus();
        BuildStatusLine(s);
        BuildToolBox(s);
        BuildShelf(s);
        // Action Popup: 마지막 기능의 파라미터를 활성 뷰포트 좌하단에서 다시 조정(Blender Adjust Last Operation)
        ActionPopup = new ActionPopup { Name = "ActionPopup" };
        AddChild(ActionPopup);
        ActionPopup.Setup(this);
        // 숫자 입력칸: 가운데 버튼 드래그로 값 조절(Shift = 세밀)
        AddChild(new SpinDrag { Name = "SpinDrag" });

        Document.Selection.ModeChanged += RefreshModeButtons;
        Document.Undo.Changed += RefreshUndoButtons;
        // 재생 포즈(PoseChanged)는 문서 데이터가 아니므로 제목(dirty 표시)·Poly Count를 건드리지 않는다(재생 중 매 프레임 호출됨)
        Document.Changed += c => { if (c.Kind != ChangeKind.PoseChanged) UpdateTitle(); };
        // Poly Count HUD: 문서/선택이 바뀔 때마다 다시 센다(프레임마다 세지 않음)
        Document.Changed += c => { if (c.Kind != ChangeKind.PoseChanged) _polyCountDirty = true; };
        Document.Selection.Changed += () => _polyCountDirty = true;
        Document.Selection.ModeChanged += () => _polyCountDirty = true;
        _polyCountDirty = true;
        foreach (var p in Layout.Panels) p.Overlay.Stats = PolyCount;
        Document.Undo.Changed += UpdateTitle;
        Tools.ToolChanged += _ => RefreshToolButtons();
        foreach (var p in Layout.Panels)
        {
            p.Display.WireOnShaded = Settings.WireOnShaded;
            p.Display.ShowGrid = Settings.ShowGrid;
            p.Display.RefreshAll();
        }
        ApplyGridSettings();
        if (Settings.QuadView) Layout.SetQuad(true);
        Tools.SetTool("select");
        RefreshModeButtons();
        RefreshUndoButtons();
        RefreshToolButtons();
        SyncAxisButtons();
        UpdateTitle();
        // 도킹 레이아웃 복원(기본: Outliner 왼쪽, Properties 오른쪽; 저장된 탭/그룹/폭/떠 있는 패널)
        Dock.RestoreLayout(EnsurePanel);
        GetTree().AutoAcceptQuit = false;
        Viewport.GrabFocus();
    }

    // ---------------------------------------------------------------- Space: 탭 = 1/4분할 토글, 홀드 = 뷰 파이 메뉴

    private ulong _spaceDownMs;
    private ViewportPanel? _spacePanel;

    private void OnSpaceDown()
    {
        _spaceDownMs = Time.GetTicksMsec();
        _spacePanel = Layout.Hovered;
        _spacePanel?.OpenPieAtMouse(PieMenus.ViewMenu(this));
    }

    private void OnSpaceUp()
    {
        bool tap = Time.GetTicksMsec() - _spaceDownMs < 300;
        var chosen = _spacePanel?.ReleasePie();
        _spacePanel = null;
        if (chosen != null && chosen.Enabled) { Actions.Invoke(chosen.ActionId); return; }
        if (tap) Actions.Invoke("view.toggleLayout");
    }

    // ---------------------------------------------------------------- 상태 라인 / 툴박스 / 셸프

    private void BuildStatusLine(float s)
    {
        int icon = (int)(18 * s);
        foreach (var (mode, iconName, action, tip) in new[] { (SelectMode.Object, "mode_object", "mode.object", "Object Mode (F8)"), (SelectMode.Vertex, "mode_vertex", "mode.vertex", "Vertex Mode (F9)"), (SelectMode.Edge, "mode_edge", "mode.edge", "Edge Mode (F10)"), (SelectMode.Face, "mode_face", "mode.face", "Face Mode (F11)") })
        {
            var b = Icons.IconButton(iconName, tip, icon, toggle: true);
            string a = action;
            b.Pressed += () => Actions.Invoke(a);
            _modeButtons[mode] = b;
            StatusLine.AddChild(b);
        }
        StatusLine.AddChild(new VSeparator());
        _cameraBased = new CheckBox { Text = "Camera-based", ButtonPressed = Settings.CameraBasedSelection, FocusMode = FocusModeEnum.None, TooltipText = "Camera-based selection (occluded components are not selected)" };
        _cameraBased.Toggled += on => Settings.CameraBasedSelection = on;
        StatusLine.AddChild(_cameraBased);
        StatusLine.AddChild(new VSeparator());
        _snapGrid = Icons.IconButton("snap_grid", "Snap to Grid (toggle; or hold X)", icon, toggle: true);
        _snapGrid.Pressed += () => Actions.Invoke("snap.grid");
        StatusLine.AddChild(_snapGrid);
        _snapPoint = Icons.IconButton("snap_point", "Snap to Points (toggle; or hold V)", icon, toggle: true);
        _snapPoint.Pressed += () => Actions.Invoke("snap.point");
        StatusLine.AddChild(_snapPoint);
        // X/V를 누르고 있는 동안에도 눌린 상태로 표시
        Hotkeys.HeldKeysChanged += SyncStatusLine;
    }

    /// <summary>Preferences의 그리드 간격(cm)을 모든 패널 그리드에 적용한다.</summary>
    public void ApplyGridSettings()
    {
        float spacing = MathF.Max(Settings.GridSpacingCm, 1f) / 100f;
        foreach (var p in Layout.Panels) { p.Grid.Spacing = spacing; p.Grid.Build(); }
    }

    private void BuildToolBox(float s)
    {
        int icon = (int)(22 * s);
        foreach (var (id, iconName, tip) in new[] { ("select", "select", "Select Tool (Q)"), ("lasso", "lasso", "Lasso Tool"), ("move", "move", "Move Tool (W)"), ("rotate", "rotate", "Rotate Tool (E)"), ("scale", "scale", "Scale Tool (R)") })
        {
            var b = Icons.IconButton(iconName, tip, icon, toggle: true);
            string tool = id;
            b.Pressed += () => Actions.Invoke("tool." + tool);
            if (id == "lasso") b.Disabled = true;
            _toolButtons[id] = b;
            ToolBox.AddChild(b);
        }
        ToolBox.AddChild(new HSeparator());
        foreach (var (axis, iconName, tip) in new[] { (AxisOrientation.World, "axis_world", "Axis: World"), (AxisOrientation.Object, "axis_local", "Axis: Local (Object)"), (AxisOrientation.Normal, "axis_normal", "Axis: Normal") })
        {
            var b = Icons.IconButton(iconName, tip, icon, toggle: true);
            var a = axis;
            b.Pressed += () => ToolContext.AxisOrientation = a;
            _axisButtons[axis] = b;
            ToolBox.AddChild(b);
        }
        // 맨 아래: Undo / Redo
        var spacer = new Control { SizeFlagsVertical = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore };
        ToolBox.AddChild(spacer);
        ToolBox.AddChild(new HSeparator());
        var prefs = Icons.IconButton("preferences", "Preferences (Ctrl+,)", icon);
        prefs.Pressed += () => Actions.Invoke("edit.preferences");
        ToolBox.AddChild(prefs);
        _undoBtn = Icons.IconButton("undo", "Undo (Ctrl+Z)", icon);
        _undoBtn.Pressed += () => Actions.Invoke("edit.undo");
        _redoBtn = Icons.IconButton("redo", "Redo (Ctrl+Y)", icon);
        _redoBtn.Pressed += () => Actions.Invoke("edit.redo");
        ToolBox.AddChild(_undoBtn);
        ToolBox.AddChild(_redoBtn);
    }

    /// <summary>셸프 탭 머리: 이름 좌우 여백과 탭 사이 간격을 넉넉하게(기본 테마의 탭은 좁아 붙어 보였음, v0.0.44).</summary>
    private void StyleShelfTabs(float s)
    {
        StyleBoxFlat Tab(Color bg, Color? top = null)
        {
            var sb = new StyleBoxFlat
            {
                BgColor = bg, ContentMarginLeft = 18 * s, ContentMarginRight = 18 * s, ContentMarginTop = 5 * s, ContentMarginBottom = 5 * s,
                CornerRadiusTopLeft = (int)(3 * s), CornerRadiusTopRight = (int)(3 * s),
            };
            if (top is { } c) { sb.BorderColor = c; sb.BorderWidthTop = (int)Math.Max(2, 2 * s); }
            return sb;
        }
        Shelf.AddThemeStyleboxOverride("tab_selected", Tab(MayaTheme.Panel, MayaTheme.Accent));
        Shelf.AddThemeStyleboxOverride("tab_unselected", Tab(MayaTheme.PanelDark));
        Shelf.AddThemeStyleboxOverride("tab_hovered", Tab(MayaTheme.ButtonHover));
        Shelf.AddThemeConstantOverride("tab_separation", (int)(4 * s));
        Shelf.AddThemeFontSizeOverride("font_size", (int)(13 * s));
    }

    /// <summary>셸프 탭 하나(가로 스크롤 + 버튼 줄). 탭 제목은 ScrollContainer의 이름.</summary>
    private HBoxContainer MakeShelfTab(string title)
    {
        var scroll = new ScrollContainer { Name = title, HorizontalScrollMode = ScrollContainer.ScrollMode.Auto, VerticalScrollMode = ScrollContainer.ScrollMode.Disabled };
        var row = new HBoxContainer { Name = "Items" };
        scroll.AddChild(row);
        Shelf.AddChild(scroll);
        return row;
    }

    private void BuildShelf(float s)
    {
        void Fill(HBoxContainer row, params (string action, string label, string icon)[][] groups)
        {
            bool first = true;
            foreach (var g in groups)
            {
                if (!first) row.AddChild(new VSeparator());
                first = false;
                foreach (var (action, label, icon) in g) row.AddChild(ShelfButton(action, label, icon, s));
            }
        }
        Fill(PolyShelf,
            new[] { ("create.cube", "Cube", "shelf_cube"), ("create.sphere", "Sphere", "shelf_sphere"), ("create.cylinder", "Cylinder", "shelf_cylinder"), ("create.cone", "Cone", "shelf_cone"), ("create.plane", "Plane", "shelf_plane"), ("create.torus", "Torus", "shelf_torus") },
            new[] { ("mesh.extrudeApply", "Extrude", "shelf_extrude"), ("mesh.mergeApply", "Merge", "shelf_merge"), ("mesh.combine", "Combine", "shelf_combine"), ("mesh.separate", "Separate", "shelf_separate"), ("mesh.bevelApply", "Bevel", "shelf_bevel"), ("mesh.bridge", "Bridge", "shelf_bridge") },
            new[] { ("mesh.addDivisionsApply", "Add Divisions", "shelf_adddiv"), ("mesh.connect", "Connect", "shelf_connect"), ("mesh.pokeApply", "Poke", "shelf_poke"), ("mesh.fillHole", "Fill Hole", "shelf_fillhole"), ("mesh.mirrorApply", "Mirror", "shelf_mirror") },
            new[] { ("mesh.multiCut", "Multi-Cut", "shelf_multicut"), ("mesh.targetWeld", "Target Weld", "shelf_targetweld"), ("mesh.insertLoop", "Insert Loop", "uv_cut"), ("mesh.creaseTool", "Crease", "shelf_crease") });
        Fill(UvShelf,
            new[] { ("windows.uvEditor", "UV Editor", "mode_uv") },
            new[] { ("uv.planarBest", "Planar", "uv_planar"), ("uv.planarX", "Planar X", "uv_planar_x"), ("uv.planarY", "Planar Y", "uv_planar_y"), ("uv.planarZ", "Planar Z", "uv_planar_z"), ("uv.cylindrical", "Cylindrical", "uv_cylindrical"), ("uv.spherical", "Spherical", "uv_spherical") },
            new[] { ("uv.unfold", "Unfold", "uv_unfold"), ("uv.layoutApply", "Layout", "uv_layout"), ("uv.cut", "Cut UV", "uv_cut"), ("uv.sew", "Sew UV", "uv_sew"), ("uv.flipU", "Flip U", "uv_flip_u"), ("uv.flipV", "Flip V", "uv_flip_v") },
            new[] { ("uv.autoSeams", "Auto Seams", "uv_autoseam"), ("uv.autoWrap", "Auto Wrap", "uv_autowrap") },
            new[] { ("uv.automaticApply", "Automatic", "uv_automatic"), ("uv.optimize", "Optimize", "uv_optimize"), ("uv.straightenApply", "Straighten", "uv_straighten"), ("uv.pin", "Pin", "uv_pin"), ("uv.cutSewTool", "3D Cut/Sew", "uv_cutsew"), ("uv.setEditor", "UV Sets", "uv_sets") });
        Fill(BridgeShelf,
            new[] { ("bridge.blenderAll", "All → Blender", "bridge_blender_all"), ("bridge.blenderSelected", "Sel → Blender", "bridge_blender_sel"), ("bridge.rizom", "RizomUV", "bridge_rizom"), ("bridge.marmoset", "Marmoset", "bridge_marmoset"), ("bridge.cascadeur", "Cascadeur", "bridge_cascadeur"), ("bridge.tripo", "Tripo Editor", "bridge_tripo") },
            new[] { ("bridge.openFolder", "Folder", "bridge_folder") });
        Fill(RigShelf,
            new[] { ("skeleton.jointTool", "Joint Tool", "rig_joint"), ("skeleton.insertJointTool", "Insert Joint", "rig_insert_joint"), ("skeleton.mirror", "Mirror Joint", "rig_mirror"), ("skeleton.orient", "Orient Joint", "rig_orient"), ("skeleton.orientApply", "Orient Now", "rig_orient") },
            new[] { ("skin.bind", "Bind Skin", "skin_bind"), ("skin.detach", "Detach Skin", "skin_detach"), ("skin.paintTool", "Paint Weights", "skin_paint"), ("skin.normalize", "Normalize", "skin_normalize"), ("skin.rebind", "Reset Weights", "skin_rebind") });
        Fill(LightShelf,
            new[] { ("create.lightDirectional", "Directional", "light_directional"), ("create.lightPoint", "Point", "light_point"), ("create.lightSpot", "Spot", "light_spot") },
            new[] { ("display.lit", "All Lights", "view_lit"), ("render.headlight", "Headlight", "light_headlight"), ("render.shadows", "Shadows", "light_shadows") },
            new[] { ("render.ibl", "IBL", "light_ibl"), ("render.background", "HDRI BG", "light_background"), ("render.nextHdri", "Next HDRI", "light_next_hdri") },
            new[] { ("select.lights", "Select Lights", "light_select"), ("windows.renderSettings", "Render Settings", "render_settings") });
        Fill(RenderShelf,
            new[] { ("windows.renderSettings", "Settings", "render_settings"), ("render.ibl", "IBL", "light_ibl"), ("render.background", "HDRI BG", "light_background"), ("render.nextHdri", "Next HDRI", "light_next_hdri"), ("render.headlight", "Headlight", "light_headlight"), ("render.shadows", "Shadows", "light_shadows") },
            new[] { ("render.ssao", "SSAO", "render_ssao"), ("render.glow", "Glow", "render_glow"), ("render.ssr", "SSR", "render_ssr"), ("render.ssil", "SSIL", "render_ssil"), ("render.sdfgi", "SDFGI", "render_sdfgi") },
            new[] { ("render.fog", "Fog", "render_fog"), ("render.volumetricFog", "Vol. Fog", "render_volfog"), ("render.adjust", "Color Adj.", "render_adjust"), ("render.dof", "DOF", "render_dof"), ("render.autoExposure", "Auto Exp.", "render_autoexp") },
            new[] { ("render.fxaa", "FXAA", "render_fxaa"), ("render.smaa", "SMAA", "render_smaa"), ("render.taa", "TAA", "render_taa"), ("render.debanding", "Debanding", "render_debanding"), ("render.postReset", "All Off", "render_off") });
        Document.Selection.Changed += RefreshShelf;
        Document.Selection.ModeChanged += RefreshShelf;
        Tools.ToolChanged += _ => RefreshShelf();
        RefreshShelf();
    }

    /// <summary>셸프 버튼 활성/체크 상태를 액션의 CanExecute/IsChecked로 맞춘다.</summary>
    private void RefreshShelf()
    {
        foreach (var (b, id) in _shelfButtons)
        {
            var a = Actions.Get(id); if (a == null) continue;
            b.Disabled = !a.Enabled;
            if (a.IsChecked != null) b.SetPressedNoSignal(a.IsChecked());
        }
    }

    /// <summary>셸프 버튼: 아이콘 위, 텍스트 아래.</summary>
    private Button ShelfButton(string action, string label, string icon, float s)
    {
        var a = Actions.Get(action);
        var b = new Button { Text = label, FocusMode = FocusModeEnum.None, TooltipText = a?.Label ?? action, CustomMinimumSize = new Vector2(56 * s, 52 * s), IconAlignment = HorizontalAlignment.Center, VerticalIconAlignment = VerticalAlignment.Top, ExpandIcon = false };
        b.AddThemeFontSizeOverride("font_size", (int)(11 * s));
        var tex = Icons.Get(icon, (int)(22 * s));
        if (tex != null) b.Icon = tex;
        if (a?.IsChecked != null) b.ToggleMode = true;
        b.Pressed += () => { Actions.Invoke(action); RefreshShelf(); };
        if (a == null) b.Disabled = true;
        _shelfButtons.Add((b, action));
        return b;
    }

    private void RefreshModeButtons()
    {
        var mode = Document.Selection.Mode;
        foreach (var (m, b) in _modeButtons) b.SetPressedNoSignal(m == mode);
    }

    private void RefreshUndoButtons()
    {
        _undoBtn.Disabled = !Document.Undo.CanUndo;
        _redoBtn.Disabled = !Document.Undo.CanRedo;
        _undoBtn.TooltipText = Document.Undo.UndoName != null ? $"Undo {Document.Undo.UndoName}" : "Undo";
        _redoBtn.TooltipText = Document.Undo.RedoName != null ? $"Redo {Document.Undo.RedoName}" : "Redo";
        RefreshShelf();
    }

    private void RefreshToolButtons()
    {
        foreach (var (id, b) in _toolButtons) b.SetPressedNoSignal(Tools.Current?.Id == id);
    }

    public void SyncAxisButtons()
    {
        foreach (var (a, b) in _axisButtons) b.SetPressedNoSignal(ToolContext.AxisOrientation == a);
    }

    public override void _Notification(int what)
    {
        if (what == NotificationWMCloseRequest)
            SceneFiles.ConfirmDiscard(() => { Dock.SaveLayout(); Settings.Save(); GetTree().Quit(); });
    }

    /// <summary>레이아웃 복원용: 패널 ID → 패널(없으면 만든다). 복원하지 않는 패널은 null.</summary>
    private FloatingPanel? EnsurePanel(string id) => id switch
    {
        "outliner" => OutlinerWindow,
        "properties" => PropertiesWindow,
        "uvEditor" => EnsureUvEditor(),
        "uvSetEditor" => EnsureUvSetEditor(),
        "materialEditor" => EnsureMaterialEditor(),
        "animationData" => EnsureAnimationData(),
        "log" => EnsureLog(),
        "componentEditor" => EnsureComponentEditor(),
        "renderSettings" => EnsureRenderSettings(),
        "tripo" => EnsureTripo(),
        "bridgeSettings" => EnsureBridgeSettings(),
        _ => null, // paintWeights 등 툴에 묶인 패널은 복원하지 않는다
    };

    /// <summary>Windows 메뉴의 Outliner/Properties: 열려 있으면 닫고, 닫혀 있으면 마지막 자리(도크)에 다시 연다.</summary>
    private void TogglePanel(FloatingPanel p)
    {
        if (p.IsOpen) p.Close(); else p.Open();
        Dock.SaveLayout();
    }

    public void ToggleMaximizeViewport()
    {
        _maximized = !_maximized;
        foreach (var n in new Control[] { StatusLine.GetParent<Control>(), ShelfRow, _mainSplit.GetChild<Control>(0), HelpLine.GetParent().GetParent<Control>() })
            n.Visible = !_maximized;
        Dock.SetMaximized(_maximized);
        UpdateTimeSliderVisibility();
    }

    // ---------------------------------------------------------------- 헬퍼

    private static Control Wrap(Control inner, Color bg, bool expandH = true)
    {
        var pc = new PanelContainer { Name = inner.Name + "Panel" };
        var sb = new StyleBoxFlat { BgColor = bg };
        sb.SetContentMarginAll(2);
        pc.AddThemeStyleboxOverride("panel", sb);
        if (expandH) { inner.SizeFlagsHorizontal = SizeFlags.ExpandFill; pc.SizeFlagsHorizontal = SizeFlags.ExpandFill; }
        pc.AddChild(inner);
        return pc;
    }


    /// <summary>도킹 가능한 일반 패널(내용은 Content에). 셸에 숨겨 두고 레이아웃 복원이 도크에 붙인다.</summary>
    private FloatingPanel MakeDockablePanel(string id, string title, Vector2 floatSize)
    {
        var p = new FloatingPanel { Name = title.Replace(" ", "") + "Window", PanelId = id, Title = title, Visible = false };
        AddChild(p);
        p.Size = floatSize; p.FloatSize = floatSize;
        p.MinPanelSize = new Vector2(180, 160) * CubeApp.Instance.UiScale;
        return p;
    }
}
