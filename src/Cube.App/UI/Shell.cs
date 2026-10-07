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
    public HBoxContainer PolyShelf { get; private set; } = null!;
    public HBoxContainer UvShelf { get; private set; } = null!;
    public HBoxContainer RigShelf { get; private set; } = null!;
    private readonly List<(Button button, string action)> _shelfButtons = new();
    public VBoxContainer ToolBox { get; private set; } = null!;
    public VBoxContainer OutlinerDock { get; private set; } = null!;
    public PanelContainer OutlinerBody { get; private set; } = null!;
    public Docks.Outliner Outliner { get; private set; } = null!;
    public ViewportLayout Layout { get; private set; } = null!;
    /// <summary>활성 뷰포트(마우스가 들어갔거나 눌린 패널). 툴·핫키·표시 액션의 대상.</summary>
    public ViewportPanel Viewport => Layout.Active;
    public VBoxContainer PropertiesDock { get; private set; } = null!;
    public PanelContainer PropertiesBody { get; private set; } = null!;
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
        Shelf = new TabContainer { Name = "Shelf", CustomMinimumSize = new Vector2(0, 108 * s) };
        PolyShelf = MakeShelfTab("Polygons");
        UvShelf = MakeShelfTab("UV");
        RigShelf = MakeShelfTab("Rigging");
        root.AddChild(Shelf);

        // --- 중앙
        _mainSplit = new HSplitContainer { Name = "MainSplit", SizeFlagsVertical = SizeFlags.ExpandFill };
        root.AddChild(_mainSplit);

        ToolBox = new VBoxContainer { Name = "ToolBox", CustomMinimumSize = new Vector2(40 * s, 0) };
        // 왼쪽 행은 스플리터가 정한 폭만 쓰고(확장 안 함), 그 안에서 Outliner 도크가 남는 폭을 채운다
        var leftRow = new HBoxContainer { Name = "Left", SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.Fill };
        leftRow.AddChild(Wrap(ToolBox, MayaTheme.PanelDark, expandH: false));
        (OutlinerDock, OutlinerBody) = MakeDock("Outliner", 200 * s);
        Outliner = new Docks.Outliner { Name = "Outliner" };
        OutlinerBody.AddChild(Outliner);
        leftRow.AddChild(OutlinerDock);
        _mainSplit.AddChild(leftRow);

        _rightSplit = new HSplitContainer { Name = "RightSplit", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _mainSplit.AddChild(_rightSplit);

        Layout = new ViewportLayout { Name = "ViewportLayout" };
        _rightSplit.AddChild(Layout);

        (PropertiesDock, PropertiesBody) = MakeDock("Properties", 240 * s);
        Properties = new Docks.PropertiesPanel { Name = "Properties" };
        PropertiesBody.AddChild(Properties);
        _rightSplit.AddChild(PropertiesDock);

        HelpLine = new Label { Name = "HelpLine", Text = "Select a tool.", CustomMinimumSize = new Vector2(0, 20 * s) };
        root.AddChild(Wrap(HelpLine, MayaTheme.PanelDark));

        _rightSplit.SplitOffsets = new[] { (int)(10000 * s) };
        _mainSplit.SplitOffsets = new[] { (int)(240 * s) };

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

        Document.Selection.ModeChanged += RefreshModeButtons;
        Document.Undo.Changed += RefreshUndoButtons;
        Document.Changed += _ => UpdateTitle();
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
            new[] { ("mesh.extrude", "Extrude", "shelf_extrude"), ("mesh.merge", "Merge", "shelf_merge"), ("mesh.combine", "Combine", "shelf_combine"), ("mesh.separate", "Separate", "shelf_separate"), ("mesh.bevel", "Bevel", "shelf_bevel"), ("mesh.bridge", "Bridge", "shelf_bridge") });
        Fill(UvShelf,
            new[] { ("windows.uvEditor", "UV Editor", "mode_uv") },
            new[] { ("uv.planarBest", "Planar", "uv_planar"), ("uv.planarX", "Planar X", "uv_planar_x"), ("uv.planarY", "Planar Y", "uv_planar_y"), ("uv.planarZ", "Planar Z", "uv_planar_z"), ("uv.cylindrical", "Cylindrical", "uv_cylindrical"), ("uv.spherical", "Spherical", "uv_spherical") },
            new[] { ("uv.unfold", "Unfold", "uv_unfold"), ("uv.layout", "Layout", "uv_layout"), ("uv.cut", "Cut UV", "uv_cut"), ("uv.sew", "Sew UV", "uv_sew"), ("uv.flipU", "Flip U", "uv_flip_u"), ("uv.flipV", "Flip V", "uv_flip_v") },
            new[] { ("uv.autoSeams", "Auto Seams", "uv_autoseam"), ("uv.autoWrap", "Auto Wrap", "uv_autowrap") });
        Fill(RigShelf,
            new[] { ("skeleton.jointTool", "Joint Tool", "rig_joint"), ("skeleton.insertJointTool", "Insert Joint", "rig_insert_joint"), ("skeleton.mirror", "Mirror Joint", "rig_mirror"), ("skeleton.orient", "Orient Joint", "rig_orient"), ("skeleton.orientApply", "Orient Now", "rig_orient") },
            new[] { ("skin.bind", "Bind Skin", "skin_bind"), ("skin.detach", "Detach Skin", "skin_detach"), ("skin.paintTool", "Paint Weights", "skin_paint"), ("skin.normalize", "Normalize", "skin_normalize"), ("skin.rebind", "Reset Weights", "skin_rebind") });
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
            SceneFiles.ConfirmDiscard(() => { Settings.Save(); GetTree().Quit(); });
    }

    public void ToggleMaximizeViewport()
    {
        _maximized = !_maximized;
        foreach (var n in new Control[] { StatusLine.GetParent<Control>(), Shelf, _mainSplit.GetChild<Control>(0), PropertiesDock, HelpLine.GetParent<Control>() })
            n.Visible = !_maximized;
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

    private static (VBoxContainer dock, PanelContainer body) MakeDock(string title, float width)
    {
        // 도크와 본문이 가로로 확장되어야 스플리터를 넓힐 때 내용(Tree 등)도 함께 넓어진다
        var dock = new VBoxContainer { Name = title.Replace(" ", "") + "Dock", CustomMinimumSize = new Vector2(width, 0), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        var header = new Label { Text = title };
        var hp = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        var sb = new StyleBoxFlat { BgColor = MayaTheme.PanelDark };
        sb.SetContentMarginAll(4);
        hp.AddThemeStyleboxOverride("panel", sb);
        hp.AddChild(header);
        dock.AddChild(hp);
        var body = new PanelContainer { Name = "Body", SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        dock.AddChild(body);
        return (dock, body);
    }
}
