using Cube.Core.Selection;
using Godot;

namespace Cube.App.UI.UvEditor;

/// <summary>
/// UV 편집기(플로팅 패널): 메뉴 바(Edit / Create / Select / Cut-Sew / Modify / Tools / View / Image / Textures / UV Sets, Maya UV Editor 구성)
/// + 아이콘 툴바(모드/투영/편집/Auto Seam·Wrap/Frame/배경) + UvCanvas.
/// </summary>
public partial class UvEditorWindow : FloatingPanel
{
    private Shell _shell = null!;
    public UvCanvas Canvas { get; private set; } = null!;
    private readonly Dictionary<string, Button> _modeButtons = new();
    private readonly List<(Button b, string action)> _actionButtons = new();
    private OptionButton _background = null!;
    private MenuBar _menuBar = null!;

    public void Setup(Shell shell)
    {
        _shell = shell;
        float s = CubeApp.Instance.UiScale;
        Title = "UV Editor";
        // 도크에 붙거나 떨어지면 캔버스 크기가 바뀌므로 레이아웃이 끝난 뒤 다시 맞춘다
        DockChanged += () => GetTree().CreateTimer(0.2).Timeout += () => { if (IsInstanceValid(Canvas)) Canvas.FrameAll(); }; // 도크 폭·행 경계 적용(타이머)이 끝난 뒤
        var host = shell.GetViewport().GetVisibleRect().Size;
        Size = new Vector2(MathF.Min(860 * s, host.X * 0.8f), MathF.Min(720 * s, host.Y * 0.85f));
        MinPanelSize = new Vector2(520 * s, 390 * s);

        Canvas = new UvCanvas();
        Canvas.Setup(shell);

        _menuBar = new MenuBar { Flat = true };
        BuildMenus();
        Content.AddChild(_menuBar);

        int icon = (int)(18 * s);
        var bar = new HBoxContainer();
        bar.AddThemeConstantOverride("separation", (int)(2 * s));
        foreach (var (key, iconName, action, tip) in new[] {
            ("object", "mode_object", "mode.object", "Object Mode"), ("uv", "mode_uv", "mode.uv", "UV Mode (F12)"),
            ("edge", "mode_edge", "mode.edge", "Edge Mode (F10)"), ("face", "mode_face", "mode.face", "Face Mode (F11)"),
            ("island", "mode_island", "mode.uvIsland", "UV Island (Shell) Mode") })
        {
            var b = Icons.IconButton(iconName, tip, icon, toggle: true);
            string a = action; b.Pressed += () => shell.Actions.Invoke(a);
            _modeButtons[key] = b; bar.AddChild(b);
        }
        bar.AddChild(new VSeparator());
        foreach (var (action, iconName) in new[] { ("uv.automaticApply", "uv_automatic"), ("uv.planarBest", "uv_planar"), ("uv.planarX", "uv_planar_x"), ("uv.planarY", "uv_planar_y"), ("uv.planarZ", "uv_planar_z"), ("uv.cylindrical", "uv_cylindrical"), ("uv.spherical", "uv_spherical") })
            bar.AddChild(ActionButton(action, iconName, icon));
        bar.AddChild(new VSeparator());
        foreach (var (action, iconName) in new[] { ("uv.unfold", "uv_unfold"), ("uv.optimize", "uv_optimize"), ("uv.layoutApply", "uv_layout"), ("uv.straightenApply", "uv_straighten"), ("uv.cut", "uv_cut"), ("uv.sew", "uv_sew"), ("uv.flipU", "uv_flip_u"), ("uv.flipV", "uv_flip_v"), ("uv.pin", "uv_pin") })
            bar.AddChild(ActionButton(action, iconName, icon));
        bar.AddChild(new VSeparator());
        foreach (var (action, iconName) in new[] { ("uv.autoSeams", "uv_autoseam"), ("uv.autoWrap", "uv_autowrap") })
            bar.AddChild(ActionButton(action, iconName, icon));
        bar.AddChild(new VSeparator());
        foreach (var (action, iconName) in new[] { ("uv.toolTweak", "uv_tweak"), ("uv.toolGrab", "uv_brush"), ("uv.toolCutSew", "uv_cutsew") })
            bar.AddChild(ActionButton(action, iconName, icon, toggle: true));
        bar.AddChild(new VSeparator());
        var frame = Icons.IconButton("uv_frame", "Frame selection (F) / all (A)", icon);
        frame.Pressed += () => Canvas.FrameSelected();
        bar.AddChild(frame);
        bar.AddChild(new VSeparator());
        _background = new OptionButton { FocusMode = Control.FocusModeEnum.None, TooltipText = "Background" };
        foreach (var name in new[] { "No Background", "Grid", "UV Texture", "Mapped Texture", "Checker Map" }) _background.AddItem(name);
        _background.Selected = (int)UvBackground.UvTexture;
        _background.ItemSelected += i => { Canvas.Background = (UvBackground)(int)i; };
        bar.AddChild(_background);
        Content.AddChild(bar);
        Content.AddChild(Canvas);

        shell.Document.Selection.ModeChanged += RefreshModes;
        Canvas.IslandModeChanged += RefreshModes;
        shell.Document.Undo.Changed += RefreshEnabled;
        shell.Document.Selection.Changed += RefreshEnabled;
        RefreshModes(); RefreshEnabled();
    }

    private void BuildMenus()
    {
        PopupMenu Add(string title)
        {
            var pm = new PopupMenu { Name = "Uv" + title.Replace(" ", "").Replace("/", "") };
            _menuBar.AddChild(pm);
            _menuBar.SetMenuTitle(_menuBar.GetChildCount() - 1, title);
            return pm;
        }
        var M = _shell.Menus;
        M.Build(Add("Edit")).Item("uv.copy").Item("uv.paste").Item("edit.delete", "Delete").Separator().Item("uv.pin").Item("uv.invertPins").Item("uv.unpin").Item("uv.unpinAll");
        M.Build(Add("Create")).Item("display.uvGrid", "Assign Checker Shader").Separator().Op("uv.automatic").Item("uv.cameraBased").Item("uv.cylindrical").Item("uv.planarBest").Item("uv.planarX").Item("uv.planarY").Item("uv.planarZ").Item("uv.spherical").Separator().Item("uv.bestPlane").Item("uv.contourStretch");
        M.Build(Add("Select")).Item("select.all").Item("select.none", "Clear").Item("uv.selectInverse").Separator()
            .Submenu("Components", m => m.Item("mode.vertex").Item("mode.edge").Item("mode.face").Item("mode.uv").Item("mode.uvIsland", "UV Shell"))
            .Separator().Item("uv.selectBackFacing").Item("uv.selectFrontFacing").Item("uv.selectOverlapping").Item("uv.selectNonOverlapping").Item("uv.selectTextureBorders").Item("uv.selectUnmapped").Separator()
            .Item("uv.shortestPath").Item("select.grow").Item("uv.growLoop").Item("select.shrink").Item("uv.shrinkLoop").Separator().Item("uv.containedFaces").Item("uv.connectedFaces").Separator()
            .Submenu("Convert Selection", m => m.Item("select.toVertices").Item("select.toEdges").Item("select.toFaces").Item("select.toUv").Item("select.toUvIsland", "To UV Shell").Item("select.toBoundaryEdges", "To UV Shell Border"));
        M.Build(Add("Cut/Sew")).Item("uv.autoSeams").Item("uv.autoWrap").Separator().Item("uv.createShell").Separator().Item("uv.cut").Item("uv.sew").Item("uv.split").Op("uv.merge").Item("uv.moveAndSew").Separator().Item("uv.deleteUvs").Separator().Item("uv.cutSewTool");
        M.Build(Add("Modify"))
            .Submenu("Align", m => m.Item("uv.alignMinU").Item("uv.alignMaxU").Item("uv.alignMinV").Item("uv.alignMaxV").Item("uv.alignCenterU").Item("uv.alignCenterV").Separator().Item("uv.linearAlign"))
            .Item("uv.cycle").Submenu("Distribute UVs", m => m.Item("uv.distributeU").Item("uv.distributeV")).Submenu("Flip", m => m.Item("uv.flipU").Item("uv.flipV"))
            .Op("uv.matchGrid").Item("uv.matchUvs").Op("uv.normalize")
            .Submenu("Rotate", m => m.Op("uv.rotate").Item("uv.rotateCw").Item("uv.rotateCcw"))
            .Op("uv.symmetrize").Item("uv.unitize").Separator()
            .Submenu("Distribute Shells", m => m.Item("uv.distributeShellsU").Item("uv.distributeShellsV")).Item("uv.gatherShells").Op("uv.layout").Item("uv.orientShells").Item("uv.orientToEdge").Op("uv.randomizeShells")
            .Item("uv.snapAndStack").Item("uv.snapTogether").Item("uv.stackShells").Item("uv.stackSimilar").Item("uv.unstackShells").Separator()
            .Item("uv.flipReversed").Submenu("Map Border", m => m.Item("uv.mapBorderSquare").Item("uv.mapBorderCircle")).Item("uv.optimize").Item("uv.straightenBorder").Item("uv.straightenShell").Op("uv.straighten").Item("uv.unfold");
        M.Build(Add("Tools")).Item("tool.select").Item("tool.move").Item("tool.rotate").Item("tool.scale").Item("uv.toolNone").Separator()
            .Item("uv.toolMoveShell").Item("uv.toolSmooth").Item("uv.toolTweak").Item("uv.toolCutSew").Item("uv.toolGrab").Item("uv.toolPinBrush").Item("uv.toolPinch").Item("uv.toolSmear").Separator().Item("uv.brushOptions");
        M.Build(Add("View")).Item("uv.viewShaded").Item("uv.viewDistortion").Item("uv.viewTextureBorders").Separator().Item("uv.viewGrid").Item("uv.viewTiles").Separator().Item("uv.viewIsolate").Item("uv.viewStats").Separator().Item("uv.frameAll").Item("uv.frameSelected");
        M.Build(Add("Image")).Item("uv.cycleBackground", "Display (cycle background)").Item("uv.imageDim").Item("uv.imageUnfiltered").Item("uv.pixelSnap").Separator().Item("uv.snapshot");
        M.Build(Add("Textures")).Item("uv.checkerMap").Item("uv.checkerSizeUp").Item("uv.checkerSizeDown");
        M.Build(Add("UV Sets")).Item("uv.setEditor").Separator().Item("uv.setCopy").Item("uv.setCreate").Item("uv.setDelete").Item("uv.setNext");
    }

    /// <summary>배경 옵션 순환(파이 메뉴용): None → Grid → UV Texture → Mapped → Checker → ...</summary>
    public void CycleBackground() => SetBackground((UvBackground)(((int)Canvas.Background + 1) % 5));

    public void SetBackground(UvBackground bg) { Canvas.Background = bg; _background.Selected = (int)bg; }

    private Button ActionButton(string action, string iconName, int icon, bool toggle = false)
    {
        var a = _shell.Actions.Get(action);
        var b = Icons.IconButton(iconName, a?.Label ?? action, icon, toggle);
        b.Pressed += () => _shell.Actions.Invoke(action);
        _actionButtons.Add((b, action));
        return b;
    }

    private void RefreshModes()
    {
        var mode = _shell.Document.Selection.Mode;
        bool island = Canvas.IslandMode && mode == SelectMode.Uv;
        _modeButtons["object"].SetPressedNoSignal(mode == SelectMode.Object);
        _modeButtons["uv"].SetPressedNoSignal(mode == SelectMode.Uv && !island);
        _modeButtons["edge"].SetPressedNoSignal(mode == SelectMode.Edge);
        _modeButtons["face"].SetPressedNoSignal(mode == SelectMode.Face);
        _modeButtons["island"].SetPressedNoSignal(island);
    }

    private void RefreshEnabled()
    {
        foreach (var (b, action) in _actionButtons)
        {
            var a = _shell.Actions.Get(action);
            b.Disabled = !(a?.Enabled ?? false);
            if (b.ToggleMode && a?.IsChecked != null) b.SetPressedNoSignal(a.IsChecked());
        }
    }

    /// <summary>UV Snapshot: 저장 다이얼로그 → 캔버스 영역 PNG.</summary>
    public void SaveSnapshot()
    {
        var fd = new FileDialog { FileMode = FileDialog.FileModeEnum.SaveFile, Access = FileDialog.AccessEnum.Filesystem, UseNativeDialog = true, Title = "UV Snapshot" };
        fd.AddFilter("*.png", "PNG");
        fd.CurrentFile = "uv_snapshot.png";
        fd.FileSelected += p => { var err = Canvas.SaveSnapshot(p); _shell.HelpLine.Text = err == Error.Ok ? $"UV Snapshot saved: {p}" : $"UV Snapshot failed: {err}"; fd.QueueFree(); };
        fd.Canceled += fd.QueueFree;
        _shell.AddChild(fd);
        fd.PopupCentered();
    }

    public void Toggle()
    {
        if (Visible) { Close(); return; }
        Open();
        Canvas.Invalidate(); Canvas.CallDeferred(nameof(UvCanvas.FrameAll));
    }
}
