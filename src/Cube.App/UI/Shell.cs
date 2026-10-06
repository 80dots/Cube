using Cube.App.Viewport;
using Cube.Core.Scene;
using Godot;

namespace Cube.App.UI;

/// <summary>
/// Maya 식 셸 레이아웃: 메뉴바 / 상태 라인 / 셸프 / [툴박스 | 아웃라이너 | 뷰포트 | 채널 박스] / 헬프 라인.
/// 레이아웃은 코드로 구성한다(.tscn은 루트만). 도크의 동작은 이후 단계에서 채운다.
/// </summary>
public partial class Shell : Control
{
    public static Shell Instance { get; private set; } = null!;

    public MenuBar MenuBar { get; private set; } = null!;
    public HBoxContainer StatusLine { get; private set; } = null!;
    public TabContainer Shelf { get; private set; } = null!;
    public VBoxContainer ToolBox { get; private set; } = null!;
    public VBoxContainer OutlinerDock { get; private set; } = null!;
    public ViewportPanel Viewport { get; private set; } = null!;
    public VBoxContainer ChannelBoxDock { get; private set; } = null!;
    public Label HelpLine { get; private set; } = null!;

    public Document Document => CubeApp.Instance.Document;

    public override void _Ready()
    {
        Instance = this;
        float s = CubeApp.Instance.UiScale;
        Theme = MayaTheme.Build(s);
        SetAnchorsPreset(LayoutPreset.FullRect);
        DisplayServer.WindowSetTitle("Cube");

        var bg = new Panel { Name = "Background" };
        bg.SetAnchorsPreset(LayoutPreset.FullRect);
        bg.MouseFilter = MouseFilterEnum.Ignore;
        AddChild(bg);

        var root = new VBoxContainer { Name = "Root" };
        root.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(root);

        MenuBar = new MenuBar { Name = "MenuBar", Flat = true };
        foreach (var title in new[] { "File", "Edit", "Create", "Select", "Mesh", "Edit Mesh", "Mesh Tools", "UV", "Skeleton", "Skin", "Display", "Windows", "Help" })
        {
            var pm = new PopupMenu { Name = title };
            MenuBar.AddChild(pm);
            MenuBar.SetMenuTitle(MenuBar.GetChildCount() - 1, title);
        }
        root.AddChild(MenuBar);

        StatusLine = new HBoxContainer { Name = "StatusLine", CustomMinimumSize = new Vector2(0, 28 * s) };
        root.AddChild(Wrap(StatusLine, MayaTheme.PanelDark));

        Shelf = new TabContainer { Name = "Shelf", CustomMinimumSize = new Vector2(0, 64 * s) };
        var polyShelf = new ScrollContainer { Name = "Polygons", HorizontalScrollMode = ScrollContainer.ScrollMode.Auto, VerticalScrollMode = ScrollContainer.ScrollMode.Disabled };
        polyShelf.AddChild(new HBoxContainer { Name = "Items" });
        Shelf.AddChild(polyShelf);
        root.AddChild(Shelf);

        var mainSplit = new HSplitContainer { Name = "MainSplit", SizeFlagsVertical = SizeFlags.ExpandFill };
        root.AddChild(mainSplit);

        ToolBox = new VBoxContainer { Name = "ToolBox", CustomMinimumSize = new Vector2(40 * s, 0) };
        var leftRow = new HBoxContainer { Name = "Left", SizeFlagsVertical = SizeFlags.ExpandFill };
        leftRow.AddChild(Wrap(ToolBox, MayaTheme.PanelDark));
        OutlinerDock = MakeDock("Outliner", 200 * s);
        leftRow.AddChild(OutlinerDock);
        mainSplit.AddChild(leftRow);

        var rightSplit = new HSplitContainer { Name = "RightSplit", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        mainSplit.AddChild(rightSplit);

        Viewport = new ViewportPanel { Name = "Viewport" };
        rightSplit.AddChild(Viewport);

        ChannelBoxDock = MakeDock("Channel Box", 240 * s);
        rightSplit.AddChild(ChannelBoxDock);

        HelpLine = new Label { Name = "HelpLine", Text = "Select a tool.", CustomMinimumSize = new Vector2(0, 20 * s) };
        root.AddChild(Wrap(HelpLine, MayaTheme.PanelDark));

        // 뷰포트가 처음엔 넓게
        rightSplit.SplitOffsets = new[] { (int)(10000 * s) };
        mainSplit.SplitOffsets = new[] { (int)(240 * s) };

        Viewport.Bind(Document);
    }

    private static Control Wrap(Control inner, Color bg)
    {
        var pc = new PanelContainer { Name = inner.Name + "Panel" };
        var sb = new StyleBoxFlat { BgColor = bg };
        sb.SetContentMarginAll(2);
        pc.AddThemeStyleboxOverride("panel", sb);
        inner.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        pc.AddChild(inner);
        return pc;
    }

    private static VBoxContainer MakeDock(string title, float width)
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
        return dock;
    }
}
