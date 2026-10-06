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

        Shelf = new TabContainer { Name = "Shelf", CustomMinimumSize = new Vector2(0, 64 * s) };
        var polyScroll = new ScrollContainer { Name = "Polygons", HorizontalScrollMode = ScrollContainer.ScrollMode.Auto, VerticalScrollMode = ScrollContainer.ScrollMode.Disabled };
        PolyShelf = new HBoxContainer { Name = "Items" };
        polyScroll.AddChild(PolyShelf);
        Shelf.AddChild(polyScroll);
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
            panel.PieItems = shift => shift ? PieMenus.ContextMenu(this) : PieMenus.ModeMenu(this);
            panel.PieExecute = item => Actions.Invoke(item.ActionId);
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
        StatusLine.AddChild(new Label { Text = " Select: " });
        foreach (var (mode, label, action) in new[] { (SelectMode.Object, "Obj", "mode.object"), (SelectMode.Vertex, "Vtx", "mode.vertex"), (SelectMode.Edge, "Edge", "mode.edge"), (SelectMode.Face, "Face", "mode.face") })
        {
            var b = new Button { Text = label, ToggleMode = true, FocusMode = FocusModeEnum.None, TooltipText = Actions.Get(action)?.Label };
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
        _undoBtn = new Button { Text = "Undo", FocusMode = FocusModeEnum.None };
        _undoBtn.Pressed += () => Actions.Invoke("edit.undo");
        _redoBtn = new Button { Text = "Redo", FocusMode = FocusModeEnum.None };
        _redoBtn.Pressed += () => Actions.Invoke("edit.redo");
        StatusLine.AddChild(_undoBtn);
        StatusLine.AddChild(_redoBtn);
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
    }

    private void BuildShelf(float s)
    {
        foreach (var (action, label) in new[] { ("create.cube", "Cube"), ("create.sphere", "Sphere"), ("create.cylinder", "Cyl"), ("create.cone", "Cone"), ("create.plane", "Plane"), ("create.torus", "Torus") })
            PolyShelf.AddChild(ShelfButton(action, label, s));
        PolyShelf.AddChild(new VSeparator());
        foreach (var (action, label) in new[] { ("mesh.extrude", "Extrude"), ("mesh.merge", "Merge"), ("mesh.combine", "Combine"), ("mesh.separate", "Separate"), ("mesh.bevel", "Bevel"), ("mesh.bridge", "Bridge") })
            PolyShelf.AddChild(ShelfButton(action, label, s));
    }

    private Button ShelfButton(string action, string label, float s)
    {
        var a = Actions.Get(action);
        var b = new Button { Text = label, FocusMode = FocusModeEnum.None, TooltipText = a?.Label ?? action, CustomMinimumSize = new Vector2(44 * s, 40 * s) };
        b.Pressed += () => Actions.Invoke(action);
        if (a == null) b.Disabled = true;
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
        foreach (var b in PolyShelf.GetChildren().OfType<Button>())
        {
            var a = Actions.All.FirstOrDefault(x => x.Label == b.TooltipText);
            if (a != null) b.Disabled = !a.Enabled;
        }
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
