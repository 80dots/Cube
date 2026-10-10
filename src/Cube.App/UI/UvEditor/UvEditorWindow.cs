using Cube.Core.Selection;
using Godot;

namespace Cube.App.UI.UvEditor;

/// <summary>
/// UV 편집기(플로팅 패널): 메뉴 바(Edit / Create / Select / Cut-Sew / Modify / Tools / View / Image / Textures / UV Sets, Maya UV Editor 구성)
/// + 아이콘 툴바(모드/투영/편집/Auto Seam·Wrap/Frame/배경) + UvCanvas.
/// 동작: 메뉴·툴바 버튼은 모두 ActionRegistry의 액션 ID만 호출한다(<c>uv.*</c>는 ShellUvActions/ShellUvActions2에 등록).
/// 메뉴 줄과 툴바는 HFlowContainer라서 패널이 좁아지면 여러 줄로 넘어간다(v0.0.35). 모드 버튼은 선택 모드/Island 모드 변경에,
/// 액션 버튼은 Undo·선택 변경에 맞춰 눌림/활성 상태를 다시 계산한다. 도크에 붙거나 뗄 때 캔버스를 다시 프레임한다.
/// </summary>
public partial class UvEditorWindow : FloatingPanel
{
    /// <summary>소유 셸(액션 레지스트리·메뉴 빌더·문서·헬프 라인 접근).</summary>
    private Shell _shell = null!;
    /// <summary>UV를 그리고 편집하는 캔버스. 셸의 UV 액션들이 이 속성으로 캔버스에 접근한다.</summary>
    public UvCanvas Canvas { get; private set; } = null!;
    /// <summary>모드 토글 버튼(키: object/uv/edge/face/island). <see cref="RefreshModes"/>가 눌림 상태를 맞춘다.</summary>
    private readonly Dictionary<string, Button> _modeButtons = new();
    /// <summary>툴바의 액션 버튼과 액션 ID 쌍. <see cref="RefreshEnabled"/>가 활성/체크 상태를 갱신한다.</summary>
    private readonly List<(Button b, string action)> _actionButtons = new();
    /// <summary>배경 선택 드롭다운(항목 순서 = <see cref="UvBackground"/> 값 순서).</summary>
    private OptionButton _background = null!;
    /// <summary>메뉴 줄: 패널이 좁으면 다음 줄로 넘어가도록 MenuBar 대신 메뉴 버튼들을 흐름 컨테이너에 둔다.</summary>
    private HFlowContainer _menuBar = null!;

    /// <summary>
    /// 패널을 구성한다: 크기·최소 크기, 캔버스 생성, 메뉴 줄, 아이콘 툴바(모드 | 투영 | 편집 | Auto Seam/Wrap | 캔버스 툴 | Frame | 배경), 캔버스 순서로 Content에 추가.
    /// 이후 선택 모드·Island 모드·Undo·선택 변경 이벤트를 구독해 버튼 상태를 맞춘다.
    /// </summary>
    /// <param name="shell">소유 셸.</param>
    public void Setup(Shell shell)
    {
        _shell = shell;
        float s = CubeApp.Instance.UiScale;
        Title = "UV Editor";
        // 도크에 붙거나 떨어지면 캔버스 크기가 바뀌므로 레이아웃이 끝난 뒤 다시 맞춘다
        DockChanged += () => Canvas.FrameOnResize(); // 자리 잡는 동안 크기가 바뀔 때마다 바로 프레임(타이머 없음)
        // 초기 크기는 기본값과 화면의 80%×85% 중 작은 값.
        var host = shell.GetViewport().GetVisibleRect().Size;
        Size = new Vector2(MathF.Min(860 * s, host.X * 0.8f), MathF.Min(720 * s, host.Y * 0.85f));
        MinPanelSize = new Vector2(260 * s, 300 * s); // 툴바·메뉴가 줄바꿈되므로 좁게 줄일 수 있다

        // 캔버스는 메뉴 빌드 전에 만든다(메뉴/액션이 Canvas를 참조할 수 있음).
        Canvas = new UvCanvas();
        Canvas.Setup(shell);

        // 메뉴 줄(간격 0).
        _menuBar = new HFlowContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _menuBar.AddThemeConstantOverride("h_separation", 0);
        _menuBar.AddThemeConstantOverride("v_separation", 0);
        BuildMenus();
        Content.AddChild(_menuBar);

        // 아이콘 크기(px, 배율 적용).
        int icon = (int)(18 * s);
        // 툴바: 패널 폭이 모자라면 버튼이 다음 줄(2줄, 3줄…)로 넘어가 모두 보인다
        var bar = new HFlowContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        bar.AddThemeConstantOverride("h_separation", (int)(2 * s));
        bar.AddThemeConstantOverride("v_separation", (int)(2 * s));
        // 선택 모드 버튼(토글). 누르면 모드 액션을 호출하고, 실제 눌림 상태는 RefreshModes가 결정한다.
        foreach (var (key, iconName, action, tip) in new[] {
            ("object", "mode_object", "mode.object", "Object Mode"), ("uv", "mode_uv", "mode.uv", "UV Mode (F12)"),
            ("edge", "mode_edge", "mode.edge", "Edge Mode (F10)"), ("face", "mode_face", "mode.face", "Face Mode (F11)"),
            ("island", "mode_island", "mode.uvIsland", "UV Island (Shell) Mode") })
        {
            var b = Icons.IconButton(iconName, tip, icon, toggle: true);
            string a = action; b.Pressed += () => shell.Actions.Invoke(a);
            _modeButtons[key] = b; bar.AddChild(b);
        }
        // 투영 버튼들.
        bar.AddChild(new VSeparator());
        foreach (var (action, iconName) in new[] { ("uv.automaticApply", "uv_automatic"), ("uv.planarBest", "uv_planar"), ("uv.planarX", "uv_planar_x"), ("uv.planarY", "uv_planar_y"), ("uv.planarZ", "uv_planar_z"), ("uv.cylindrical", "uv_cylindrical"), ("uv.spherical", "uv_spherical") })
            bar.AddChild(ActionButton(action, iconName, icon));
        // 편집 버튼들(Unfold/Optimize/Layout/Straighten/Cut/Sew/Flip/Pin).
        bar.AddChild(new VSeparator());
        foreach (var (action, iconName) in new[] { ("uv.unfold", "uv_unfold"), ("uv.optimize", "uv_optimize"), ("uv.layoutApply", "uv_layout"), ("uv.straightenApply", "uv_straighten"), ("uv.cut", "uv_cut"), ("uv.sew", "uv_sew"), ("uv.flipU", "uv_flip_u"), ("uv.flipV", "uv_flip_v"), ("uv.pin", "uv_pin") })
            bar.AddChild(ActionButton(action, iconName, icon));
        // 자동 심/자동 랩.
        bar.AddChild(new VSeparator());
        foreach (var (action, iconName) in new[] { ("uv.autoSeams", "uv_autoseam"), ("uv.autoWrap", "uv_autowrap") })
            bar.AddChild(ActionButton(action, iconName, icon));
        // 캔버스 툴(토글 — IsChecked로 현재 툴 표시).
        bar.AddChild(new VSeparator());
        foreach (var (action, iconName) in new[] { ("uv.toolTweak", "uv_tweak"), ("uv.toolGrab", "uv_brush"), ("uv.toolCutSew", "uv_cutsew") })
            bar.AddChild(ActionButton(action, iconName, icon, toggle: true));
        // 프레임 버튼(선택 영역 프레임).
        bar.AddChild(new VSeparator());
        var frame = Icons.IconButton("uv_frame", "Frame selection (F) / all (A)", icon);
        frame.Pressed += () => Canvas.FrameSelected();
        bar.AddChild(frame);
        // 배경 드롭다운: 기본은 UV Texture.
        bar.AddChild(new VSeparator());
        _background = new OptionButton { FocusMode = Control.FocusModeEnum.None, TooltipText = "Background" };
        foreach (var name in new[] { "No Background", "Grid", "UV Texture", "Mapped Texture", "Checker Map" }) _background.AddItem(name);
        _background.Selected = (int)UvBackground.UvTexture;
        _background.ItemSelected += i => { Canvas.Background = (UvBackground)(int)i; };
        bar.AddChild(_background);
        // UV Symmetry(Maya UV Toolkit): Off/U/V 드롭다운 + 축선 위치(ShellUvSymmetry.cs).
        bar.AddChild(new VSeparator());
        _symmetry = new OptionButton { FocusMode = Control.FocusModeEnum.None, TooltipText = "UV Symmetry: mirror selections and edits across u = center (U) or v = center (V)" };
        foreach (var name in new[] { "Sym: Off", "Sym: U", "Sym: V" }) _symmetry.AddItem(name);
        _symmetry.ItemSelected += i => shell.Actions.Invoke(i switch { 1 => "uv.symmetryU", 2 => "uv.symmetryV", _ => "uv.symmetryOff" });
        bar.AddChild(_symmetry);
        _symCenter = new SpinBox { MinValue = -10, MaxValue = 10, Step = 0.001, CustomArrowStep = 0.05, TooltipText = "Symmetry center (u or v)", Alignment = HorizontalAlignment.Center };
        _symCenter.CustomMinimumSize = new Vector2(78 * s, 0);
        _symCenter.ValueChanged += v => { if (!_syncingSym) shell.SetUvSymmetryCenter((float)v); };
        bar.AddChild(_symCenter);
        shell.UvSymmetryChanged += SyncSymmetry;
        SyncSymmetry();
        Content.AddChild(bar);
        Content.AddChild(Canvas);

        // 버튼 상태 동기화 구독.
        shell.Document.Selection.ModeChanged += RefreshModes;
        Canvas.IslandModeChanged += RefreshModes;
        shell.Document.Undo.Changed += RefreshEnabled;
        shell.Document.Selection.Changed += RefreshEnabled;
        RefreshModes(); RefreshEnabled();
    }

    /// <summary>
    /// Maya UV Editor 구성의 메뉴들을 만든다. 각 메뉴는 <c>MenuButton</c>(SwitchOnHover로 메뉴 사이 이동)의 팝업에
    /// <c>MenuBuilder</c>로 액션 항목을 채운다. <c>Op(id)</c>는 실행 항목과 "Options..." 항목을 함께 넣는다.
    /// </summary>
    private void BuildMenus()
    {
        // 메뉴 버튼 하나를 메뉴 줄에 추가하고 그 팝업을 돌려주는 로컬 함수(노드 이름은 "Uv" + 공백/슬래시 제거한 제목).
        PopupMenu Add(string title)
        {
            var mb = new MenuButton { Text = title, Flat = true, FocusMode = Control.FocusModeEnum.None, SwitchOnHover = true, Name = "Uv" + title.Replace(" ", "").Replace("/", "") };
            mb.SetDisableShortcuts(true); // accelerator는 표시 전용(키는 셸 단축키가 처리; Shell.MenuBar와 같은 이유)
            _menuBar.AddChild(mb);
            return mb.GetPopup();
        }
        var M = _shell.Menus;
        M.Build(Add("Edit")).Item("uv.copy").Item("uv.paste").Item("edit.delete", "Delete").Separator().Item("uv.pin").Item("uv.invertPins").Item("uv.unpin").Item("uv.unpinAll");
        M.Build(Add("Create")).Item("display.uvGrid", "Assign Checker Shader").Separator().Op("uv.automatic").Item("uv.cameraBased").Item("uv.cylindrical").Item("uv.planarBest").Item("uv.planarX").Item("uv.planarY").Item("uv.planarZ").Item("uv.spherical").Separator().Item("uv.bestPlane").Item("uv.contourStretch");
        M.Build(Add("Select")).Item("uv.selectAll").Item("select.none", "Clear").Item("uv.selectInverse").Separator()
            .Submenu("Components", m => m.Item("mode.vertex").Item("mode.edge").Item("mode.face").Item("mode.uv").Item("mode.uvIsland", "UV Shell"))
            .Separator().Item("uv.selectBackFacing").Item("uv.selectFrontFacing").Item("uv.selectOverlapping").Item("uv.selectNonOverlapping").Op("uv.selectIdentical").Op("uv.selectSimilar").Item("uv.selectTextureBorders").Item("uv.selectUnmapped").Separator()
            .Item("uv.shortestPath").Item("select.grow").Item("uv.growLoop").Item("select.shrink").Item("uv.shrinkLoop").Separator().Item("uv.containedFaces").Item("uv.connectedFaces").Separator()
            .Submenu("Convert Selection", m => m.Item("select.toVertices").Item("select.toEdges").Item("select.toFaces").Item("select.toUv").Item("select.toUvIsland", "To UV Shell").Item("select.toBoundaryEdges", "To UV Shell Border"));
        M.Build(Add("Cut/Sew")).Item("uv.autoSeams").Item("uv.autoWrap").Separator().Item("uv.createShell").Item("uv.createShellGrid").Separator().Item("uv.cut").Item("uv.sew").Item("uv.split").Op("uv.merge").Item("uv.moveAndSew").Separator().Item("uv.deleteUvs").Separator().Item("uv.cutSewTool");
        M.Build(Add("Modify"))
            .Submenu("Align", m => m.Item("uv.alignMinU").Item("uv.alignMaxU").Item("uv.alignMinV").Item("uv.alignMaxV").Item("uv.alignCenterU").Item("uv.alignCenterV").Separator().Item("uv.linearAlign"))
            .Item("uv.cycle").Submenu("Distribute UVs", m => m.Item("uv.distributeU").Item("uv.distributeV")).Submenu("Flip", m => m.Item("uv.flipU").Item("uv.flipV"))
            .Op("uv.matchGrid").Item("uv.matchUvs").Op("uv.normalize")
            .Submenu("Rotate", m => m.Op("uv.rotate").Item("uv.rotateCw").Item("uv.rotateCcw"))
            .Op("uv.symmetrize").Item("uv.unitize").Separator()
            .Submenu("Symmetry", m => m.Item("uv.symmetryOff").Item("uv.symmetryU").Item("uv.symmetryV").Separator().Item("uv.symmetryCenterSelection")).Separator()
            .Submenu("Distribute Shells", m => m.Item("uv.distributeShellsU").Item("uv.distributeShellsV")).Item("uv.gatherShells").Op("uv.layout").Item("uv.orientShells").Item("uv.orientToEdge").Op("uv.randomizeShells")
            .Item("uv.snapAndStack").Item("uv.snapTogether").Item("uv.stackShells").Item("uv.stackSimilar").Item("uv.unstackShells").Op("uv.cloneShell").Separator()
            .Item("uv.flipReversed").Submenu("Map Border", m => m.Item("uv.mapBorderSquare").Item("uv.mapBorderCircle")).Item("uv.optimize").Item("uv.straightenBorder").Item("uv.straightenShell").Op("uv.straighten").Item("uv.unfold");
        M.Build(Add("Tools")).Item("tool.select").Item("tool.move").Item("tool.rotate").Item("tool.scale").Item("uv.toolNone").Separator()
            .Item("uv.toolMoveShell").Item("uv.toolSmooth").Item("uv.toolTweak").Item("uv.toolCutSew").Item("uv.toolGrab").Item("uv.toolPinBrush").Item("uv.toolPinch").Item("uv.toolSmear").Separator().Item("uv.brushOptions");
        M.Build(Add("View")).Item("uv.viewShaded").Item("uv.viewDistortion").Item("uv.viewTextureBorders").Separator().Item("uv.viewGrid").Item("uv.viewTiles").Separator().Item("uv.viewIsolate").Item("uv.viewStats").Separator().Item("uv.frameAll").Item("uv.frameSelected");
        M.Build(Add("Image")).Item("uv.cycleBackground", "Display (cycle background)").Item("uv.imageDim").Item("uv.imageUnfiltered").Item("uv.pixelSnap").Separator().Item("uv.snapshot");
        M.Build(Add("Textures")).Item("uv.checkerMap").Item("uv.checkerSizeUp").Item("uv.checkerSizeDown");
        M.Build(Add("UV Sets")).Item("uv.setEditor").Separator().Item("uv.setCopy").Item("uv.setCreate").Item("uv.setDelete").Item("uv.setNext");
    }

    private OptionButton _symmetry = null!;
    private SpinBox _symCenter = null!;
    private bool _syncingSym;

    /// <summary>툴바의 Symmetry 드롭다운·중심 스핀박스를 설정과 맞춘다(켜져 있으면 강조색).</summary>
    private void SyncSymmetry()
    {
        if (_symmetry == null) return;
        var st = CubeApp.Instance.Settings;
        _syncingSym = true;
        int m = Math.Clamp(st.UvSymmetry, 0, 2);
        if (_symmetry.Selected != m) _symmetry.Selected = m;
        if (MathF.Abs((float)_symCenter.Value - st.UvSymmetryCenter) > 1e-6f) _symCenter.Value = st.UvSymmetryCenter;
        _symCenter.Editable = m > 0;
        if (m > 0) _symmetry.AddThemeColorOverride("font_color", MayaTheme.Accent); else _symmetry.RemoveThemeColorOverride("font_color");
        _syncingSym = false;
    }

    /// <summary>배경 옵션 순환(파이 메뉴용): None → Grid → UV Texture → Mapped → Checker → ...</summary>
    /// <remarks>배경 종류는 5개이므로 (현재 + 1) mod 5. 드롭다운 표시도 함께 맞춘다.</remarks>
    public void CycleBackground() => SetBackground((UvBackground)(((int)Canvas.Background + 1) % 5));

    /// <summary>캔버스 배경을 설정하고 드롭다운 선택을 같은 값으로 맞춘다(액션/파이에서 호출).</summary>
    public void SetBackground(UvBackground bg) { Canvas.Background = bg; _background.Selected = (int)bg; }

    /// <summary>
    /// 액션을 실행하는 아이콘 버튼을 만들고 상태 갱신 목록에 등록한다. 툴팁은 액션 라벨(없으면 ID).
    /// </summary>
    /// <param name="action">액션 ID.</param>
    /// <param name="iconName">내장 아이콘 이름.</param>
    /// <param name="icon">아이콘 크기(px).</param>
    /// <param name="toggle">토글 버튼 여부(IsChecked 표시).</param>
    /// <returns>만든 버튼.</returns>
    private Button ActionButton(string action, string iconName, int icon, bool toggle = false)
    {
        var a = _shell.Actions.Get(action);
        var b = Icons.IconButton(iconName, a?.Label ?? action, icon, toggle);
        b.Pressed += () => _shell.Actions.Invoke(action);
        _actionButtons.Add((b, action));
        return b;
    }

    /// <summary>
    /// 모드 버튼의 눌림 상태를 현재 선택 모드에 맞춘다. Island는 UV 모드 + 캔버스 IslandMode인 경우이며 이때 UV 버튼은 눌리지 않는다.
    /// SetPressedNoSignal로 Pressed 이벤트(=액션 재호출)를 일으키지 않는다.
    /// </summary>
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

    /// <summary>툴바 액션 버튼을 액션의 Enabled(CanExecute)로 활성화하고, 토글 버튼은 IsChecked로 눌림 상태를 맞춘다.</summary>
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
        // 네이티브 저장 다이얼로그(파일 시스템 전체 접근). 결과는 헬프 라인에 표시하고 다이얼로그는 닫힐 때 해제한다.
        var fd = new FileDialog { FileMode = FileDialog.FileModeEnum.SaveFile, Access = FileDialog.AccessEnum.Filesystem, UseNativeDialog = true, Title = "UV Snapshot" };
        fd.AddFilter("*.png", "PNG");
        fd.CurrentFile = "uv_snapshot.png";
        fd.FileSelected += p => { var err = Canvas.SaveSnapshot(p); _shell.HelpLine.Text = err == Error.Ok ? $"UV Snapshot saved: {p}" : $"UV Snapshot failed: {err}"; fd.QueueFree(); };
        fd.Canceled += fd.QueueFree;
        _shell.AddChild(fd);
        fd.PopupCentered();
    }

    /// <summary>
    /// 패널 열기/닫기 토글. 열 때 캔버스 캐시를 무효화하고, 레이아웃이 잡힌 다음 프레임에 전체 프레임(FrameAll)을 지연 호출한다.
    /// </summary>
    public void Toggle()
    {
        if (Visible) { Close(); return; }
        // 처음 열 때는 메뉴·툴바 흐름 컨테이너가 줄바꿈되며 캔버스 크기가 몇 프레임 동안 바뀌므로, 그동안 크기가 바뀔 때마다 다시 프레임한다
        // (전에는 첫 크기로 한 번만 프레임해 0..1 타일이 캔버스 밖으로 넘친 채 열렸다)
        Canvas.FrameOnResize();
        Open();
        Canvas.Invalidate(); Canvas.CallDeferred(nameof(UvCanvas.FrameAll));
    }
}
