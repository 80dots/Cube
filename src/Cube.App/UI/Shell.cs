using Cube.App.Hotkeys;
using Cube.App.Tools;
using Cube.App.Viewport;
using Cube.Core.Scene;
using Cube.Core.Selection;
using Godot;

namespace Cube.App.UI;

/// <summary>
/// Maya 식 셸 레이아웃: 메뉴바 / 상태 라인 / 셸프 / [툴박스 | 아웃라이너 | 뷰포트 | 채널 박스] / 헬프 라인.
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
    public Docks.ChannelBox ChannelBox { get; private set; } = null!;
    public ViewportPanel Viewport { get; private set; } = null!;
    public VBoxContainer ChannelBoxDock { get; private set; } = null!;
    public PanelContainer ChannelBoxBody { get; private set; } = null!;
    public Label HelpLine { get; private set; } = null!;

    public ActionRegistry Actions { get; } = new();
    public ShellInput Hotkeys { get; private set; } = null!;
    public ToolManager Tools { get; private set; } = null!;
    public ToolContext ToolContext { get; private set; } = null!;
    public MenuBuilder Menus { get; private set; } = null!;

    private readonly Dictionary<SelectMode, Button> _modeButtons = new();
    private readonly Dictionary<string, Button> _toolButtons = new();
    private OptionButton _axisOrientation = null!;
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
        DisplayServer.WindowSetTitle("Cube");

        var bg = new Panel { Name = "Background", MouseFilter = MouseFilterEnum.Ignore };
        bg.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(bg);

        var root = new VBoxContainer { Name = "Root" };
        root.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(root);

        // --- 메뉴바
        MenuBar = new MenuBar { Name = "MenuBar", Flat = true };
        root.AddChild(MenuBar);

        // --- 상태 라인
        StatusLine = new HBoxContainer { Name = "StatusLine", CustomMinimumSize = new Vector2(0, 28 * s) };
        root.AddChild(Wrap(StatusLine, MayaTheme.PanelDark));

        // --- 셸프
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
        var leftRow = new HBoxContainer { Name = "Left", SizeFlagsVertical = SizeFlags.ExpandFill };
        leftRow.AddChild(Wrap(ToolBox, MayaTheme.PanelDark, expandH: false));
        (OutlinerDock, OutlinerBody) = MakeDock("Outliner", 200 * s);
        Outliner = new Docks.Outliner { Name = "Outliner" };
        OutlinerBody.AddChild(Outliner);
        leftRow.AddChild(OutlinerDock);
        _mainSplit.AddChild(leftRow);

        _rightSplit = new HSplitContainer { Name = "RightSplit", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _mainSplit.AddChild(_rightSplit);

        Viewport = new ViewportPanel { Name = "Viewport" };
        _rightSplit.AddChild(Viewport);

        (ChannelBoxDock, ChannelBoxBody) = MakeDock("Channel Box", 240 * s);
        ChannelBox = new Docks.ChannelBox { Name = "ChannelBox" };
        ChannelBoxBody.AddChild(ChannelBox);
        _rightSplit.AddChild(ChannelBoxDock);

        HelpLine = new Label { Name = "HelpLine", Text = "Select a tool.", CustomMinimumSize = new Vector2(0, 20 * s) };
        root.AddChild(Wrap(HelpLine, MayaTheme.PanelDark));

        _rightSplit.SplitOffsets = new[] { (int)(10000 * s) };
        _mainSplit.SplitOffsets = new[] { (int)(240 * s) };

        Viewport.Bind(Document);
        Outliner.Bind(Document);
        ChannelBox.Bind(Document);

        // --- 서비스
        ToolContext = new ToolContext { Doc = Document, Viewport = Viewport, Settings = Settings, SetHelp = t => HelpLine.Text = t };
        ToolContext.AxisOrientation = Enum.TryParse<AxisOrientation>(Settings.AxisOrientation, out var ao) ? ao : AxisOrientation.World;
        Tools = new ToolManager(ToolContext);
        RegisterTools();
        Viewport.ToolInput = e => Tools.HandleInput(e);

        Hotkeys = new ShellInput { Name = "ShellInput", Actions = Actions, IsViewportContext = () => Viewport.IsViewportContext };
        AddChild(Hotkeys);

        RegisterActions();
        Menus = new MenuBuilder(Actions, Hotkeys.Map);
        BuildMenus();
        BuildStatusLine(s);
        BuildToolBox(s);
        BuildShelf(s);

        Document.Selection.ModeChanged += RefreshModeButtons;
        Document.Undo.Changed += RefreshUndoButtons;
        Tools.ToolChanged += _ => RefreshToolButtons();
        Viewport.Display.WireOnShaded = Settings.WireOnShaded;
        Viewport.Display.ShowGrid = Settings.ShowGrid;
        Viewport.Display.RefreshAll();
        Tools.SetTool("select");
        RefreshModeButtons();
        RefreshUndoButtons();
        RefreshToolButtons();
        Viewport.GrabFocus();
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
        StatusLine.AddChild(new Label { Text = " Axis: " });
        _axisOrientation = new OptionButton { FocusMode = FocusModeEnum.None };
        foreach (var o in Enum.GetNames<AxisOrientation>()) _axisOrientation.AddItem(o);
        _axisOrientation.Selected = (int)ToolContext.AxisOrientation;
        _axisOrientation.ItemSelected += i => { ToolContext.AxisOrientation = (AxisOrientation)i; Settings.AxisOrientation = ToolContext.AxisOrientation.ToString(); };
        StatusLine.AddChild(_axisOrientation);
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
        foreach (var (id, label, tip) in new[] { ("select", "Sel", "Select Tool (Q)"), ("lasso", "Las", "Lasso Tool"), ("move", "Mv", "Move Tool (W)"), ("rotate", "Rot", "Rotate Tool (E)"), ("scale", "Scl", "Scale Tool (R)") })
        {
            var b = new Button { Text = label, ToggleMode = true, FocusMode = FocusModeEnum.None, TooltipText = tip, CustomMinimumSize = new Vector2(36 * s, 36 * s) };
            string tool = id;
            b.Pressed += () => Actions.Invoke("tool." + tool);
            if (id == "lasso") b.Disabled = true;
            _toolButtons[id] = b;
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
        else b.VisibilityChanged += () => b.Disabled = !a.Enabled;
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

    public void ToggleMaximizeViewport()
    {
        _maximized = !_maximized;
        foreach (var n in new Control[] { MenuBar.GetParent<Control>() == this ? MenuBar : MenuBar, StatusLine.GetParent<Control>(), Shelf, _mainSplit.GetChild<Control>(0), ChannelBoxDock, HelpLine.GetParent<Control>() })
            if (n != MenuBar) n.Visible = !_maximized;
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
        var dock = new VBoxContainer { Name = title.Replace(" ", "") + "Dock", CustomMinimumSize = new Vector2(width, 0) };
        var header = new Label { Text = title };
        var hp = new PanelContainer();
        var sb = new StyleBoxFlat { BgColor = MayaTheme.PanelDark };
        sb.SetContentMarginAll(4);
        hp.AddThemeStyleboxOverride("panel", sb);
        hp.AddChild(header);
        dock.AddChild(hp);
        var body = new PanelContainer { Name = "Body", SizeFlagsVertical = SizeFlags.ExpandFill };
        dock.AddChild(body);
        return (dock, body);
    }
}
