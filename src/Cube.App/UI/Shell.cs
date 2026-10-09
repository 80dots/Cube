using Cube.App.Hotkeys;
using Cube.App.Tools;
using Cube.App.Viewport;
using Cube.Core.Scene;
using Cube.Core.Selection;
using Godot;

namespace Cube.App.UI;

/// <summary>
/// Maya 식 셸 레이아웃: 메뉴바 / 상태 라인 / 셸프 / [툴박스 | 아웃라이너 | 뷰포트(1 또는 4분할) | Properties] / 헬프 라인.
/// partial 클래스로 여러 파일(ShellActions/ShellMeshActions/ShellUvActions 등)에 나뉘어 있으며,
/// 이 파일은 셸 전체 레이아웃 생성(_Ready), 상태 라인·툴박스·셸프 구성, 버튼 상태 동기화,
/// 창 닫기 처리와 도킹 패널 복원 진입점을 담당한다. CubeApp.ReloadShell()이 문서는 유지한 채
/// 셸을 통째로 다시 만들 수 있으므로 상태는 Settings/Document에 두고 셸은 표시·라우팅만 한다.
/// 레이아웃은 코드로 구성한다(.tscn은 루트만). 모든 UI는 ActionRegistry의 액션만 호출한다.
/// </summary>
public partial class Shell : Control
{
    /// <summary>현재 살아 있는 셸 인스턴스(싱글턴). _Ready에서 설정되며 셸 재생성 시 새 인스턴스로 바뀐다.</summary>
    public static Shell Instance { get; private set; } = null!;

    /// <summary>상단 메인 메뉴바(File/Edit/Create/... ). BuildMenus가 MenuBuilder로 채운다.</summary>
    public MenuBar MenuBar { get; private set; } = null!;
    /// <summary>Maya 상태 라인: 선택 모드 아이콘, Camera-based 체크, 스냅 토글 버튼이 놓이는 줄.</summary>
    public HBoxContainer StatusLine { get; private set; } = null!;
    /// <summary>셸프 탭 컨테이너(Polygons/UV/Rigging/Light/Render). 각 탭은 MakeShelfTab이 만든 스크롤 + 버튼 줄.</summary>
    public TabContainer Shelf { get; private set; } = null!;
    /// <summary>셸프 줄 전체: [탭 셸프(확장)] | [Bridge 영역(오른쪽 끝)].</summary>
    public HBoxContainer ShelfRow { get; private set; } = null!;
    /// <summary>셸프 줄 오른쪽 끝의 Bridge 영역 버튼 줄(외부 앱 연동 아이콘 버튼).</summary>
    public HBoxContainer BridgeShelf { get; private set; } = null!;
    /// <summary>마지막 작업 파라미터 재조정 팝업(Blender Adjust Last Operation에 해당, 활성 뷰포트 좌하단).</summary>
    public ActionPopup ActionPopup { get; private set; } = null!;
    /// <summary>Polygons 셸프 탭의 버튼 줄(기본 도형 생성·모델링 기능).</summary>
    public HBoxContainer PolyShelf { get; private set; } = null!;
    /// <summary>UV 셸프 탭의 버튼 줄(UV 편집기·투영·Unfold/Layout 등).</summary>
    public HBoxContainer UvShelf { get; private set; } = null!;
    /// <summary>Rigging 셸프 탭의 버튼 줄(조인트·스킨 바인드·가중치 페인트).</summary>
    public HBoxContainer RigShelf { get; private set; } = null!;
    /// <summary>Light 셸프 탭의 버튼 줄(라이트 생성·헤드라이트·그림자·IBL).</summary>
    public HBoxContainer LightShelf { get; private set; } = null!;
    /// <summary>Render 셸프 탭의 버튼 줄(렌더 설정·포스트 이펙트·안티앨리어싱 토글).</summary>
    public HBoxContainer RenderShelf { get; private set; } = null!;
    /// <summary>
    /// ShelfButton이 만든 모든 셸프 버튼과 그 액션 ID 목록. RefreshShelf가 순회하며
    /// 액션의 Enabled(CanExecute)와 IsChecked를 버튼의 Disabled/눌림 상태에 반영한다.
    /// </summary>
    private readonly List<(Button button, string action)> _shelfButtons = new();
    /// <summary>셸프 버튼과 액션 ID 목록(점검용 읽기 전용).</summary>
    public IReadOnlyList<(Button button, string action)> ShelfButtons => _shelfButtons;
    /// <summary>셸프 버튼들의 ActionId(디버그 점검 `actioncheck`용).</summary>
    public IEnumerable<string> ShelfActions => _shelfButtons.Select(b => b.action);
    /// <summary>왼쪽 세로 툴박스(Select/Move/Rotate/Scale, 축 방향 버튼, Preferences/Undo/Redo).</summary>
    public VBoxContainer ToolBox { get; private set; } = null!;
    /// <summary>Outliner를 담은 도킹 가능한 패널(기본: 왼쪽 도크).</summary>
    public FloatingPanel OutlinerWindow { get; private set; } = null!;
    /// <summary>Properties를 담은 도킹 가능한 패널(기본: 오른쪽 도크).</summary>
    public FloatingPanel PropertiesWindow { get; private set; } = null!;
    /// <summary>툴박스 옆 왼쪽 도크(위아래로 쌓인 DockGroup/DockRow를 담는 VSplitContainer).</summary>
    public Docking.DockSide LeftDock { get; private set; } = null!;
    /// <summary>뷰포트 옆 오른쪽 도크.</summary>
    public Docking.DockSide RightDock { get; private set; } = null!;
    /// <summary>패널 도킹/떼어내기/드래그 놓기/레이아웃 저장·복원을 담당하는 관리자 노드.</summary>
    public Docking.DockManager Dock { get; private set; } = null!;
    /// <summary>씬 계층(DAG) 트리 뷰. OutlinerWindow의 Content 안에 들어 있다.</summary>
    public Docks.Outliner Outliner { get; private set; } = null!;
    /// <summary>뷰포트 패널 4개(top/persp/front/side)를 소유하고 단일/4분할 전환과 활성 패널을 관리한다.</summary>
    public ViewportLayout Layout { get; private set; } = null!;
    /// <summary>활성 뷰포트(마우스가 들어갔거나 눌린 패널). 툴·핫키·표시 액션의 대상.</summary>
    public ViewportPanel Viewport => Layout.Active;

    /// <summary>Properties 패널(Maya Channel Box 역할: 트랜스폼·히스토리·머티리얼·라이트 편집).</summary>
    public Docks.PropertiesPanel Properties { get; private set; } = null!;
    /// <summary>하단 헬프 라인 텍스트. 툴이 ToolContext.SetHelp으로 안내 문구를 쓴다.</summary>
    public Label HelpLine { get; private set; } = null!;

    /// <summary>
    /// 모든 액션(ActionId → 실행/활성/체크 람다)의 등록부. 메뉴·셸프·툴박스·핫키·파이는 모두 이 ID로만 호출한다.
    /// </summary>
    public ActionRegistry Actions { get; } = new();
    /// <summary>키 입력 라우터(_Input). 단축키 → 액션, Space 탭/홀드, 홀드 키(X/V/J 등) 상태를 관리한다.</summary>
    public ShellInput Hotkeys { get; private set; } = null!;
    /// <summary>현재 툴과 툴 전환(이전 툴 복귀 포함)을 관리한다. 활성 뷰포트 입력을 현재 툴에 넘긴다.</summary>
    public ToolManager Tools { get; private set; } = null!;
    /// <summary>툴이 공유하는 환경: 문서, 활성 뷰포트, 설정, 축 방향(World/Object/Normal), 헬프 출력 콜백.</summary>
    public ToolContext ToolContext { get; private set; } = null!;
    /// <summary>액션 ID 목록으로 PopupMenu 항목(라벨·단축키 표시·체크)을 만드는 빌더.</summary>
    public MenuBuilder Menus { get; private set; } = null!;
    /// <summary>가져오기/내보내기(glTF/FBX/OBJ) 파일 액션.</summary>
    public IO.FileActions Files { get; private set; } = null!;
    /// <summary>.cube 씬 열기/저장/새로 만들기와 "저장하지 않은 변경 버리기" 확인.</summary>
    public IO.SceneFileActions SceneFiles { get; private set; } = null!;

    /// <summary>상태 라인의 선택 모드 버튼(Object/Vertex/Edge/Face). RefreshModeButtons가 현재 모드만 눌림 표시.</summary>
    private readonly Dictionary<SelectMode, Button> _modeButtons = new();
    /// <summary>툴박스의 툴 버튼(툴 ID → 버튼). RefreshToolButtons가 현재 툴만 눌림 표시.</summary>
    private readonly Dictionary<string, Button> _toolButtons = new();
    /// <summary>툴박스 하단 조작기 축 방향 버튼(World/Local/Normal).</summary>
    private readonly Dictionary<AxisOrientation, Button> _axisButtons = new();
    /// <summary>상태 라인의 Camera-based selection 체크박스(Settings.CameraBasedSelection과 연동).</summary>
    private CheckBox _cameraBased = null!;
    /// <summary>상태 라인 스냅 토글 버튼(그리드/점). 홀드 키(X/V)를 누르는 동안에도 눌림으로 표시된다.</summary>
    private Button _snapGrid = null!, _snapPoint = null!;
    /// <summary>툴박스 맨 아래 Undo/Redo 버튼. 툴팁에 다음 Undo/Redo 명령 이름을 표시한다.</summary>
    private Button _undoBtn = null!, _redoBtn = null!;
    /// <summary>
    /// _mainSplit = [왼쪽 행(툴박스+왼쪽 도크) | _rightSplit], _rightSplit = [뷰포트 레이아웃 | 오른쪽 도크].
    /// 스플리터 오프셋이 도크 폭이 되며 DockManager가 저장·복원한다.
    /// </summary>
    private HSplitContainer _mainSplit = null!, _rightSplit = null!;
    /// <summary>Ctrl+Space 뷰포트 최대화 상태(상태 라인·셸프·왼쪽 행·헬프 라인을 숨김).</summary>
    private bool _maximized;

    /// <summary>현재 문서(CubeApp 소유; 셸이 재생성되어도 유지된다).</summary>
    public Document Document => CubeApp.Instance.Document;
    /// <summary>뷰포트 좌상단 Poly Count HUD 수치(모든 패널이 공유).</summary>
    public Viewport.PolyCount PolyCount { get; } = new();
    /// <summary>Poly Count를 다시 세야 하는지 표시. 문서/선택/모드 변경 시 true → 다음 _Process에서 한 번만 Recompute.</summary>
    private bool _polyCountDirty;

    /// <summary>
    /// 매 프레임: 더러워졌으면 Poly Count를 한 번 다시 세고(변경이 여러 번 와도 프레임당 1회),
    /// 로그 창(Script Editor 로그) 갱신을 처리한다.
    /// </summary>
    public override void _Process(double delta)
    {
        if (_polyCountDirty) { _polyCountDirty = false; PolyCount.Recompute(Document); }
        UpdateLog();
    }
    /// <summary>사용자 설정(user://settings.json). CubeApp 소유.</summary>
    public Settings Settings => CubeApp.Instance.Settings;

    /// <summary>
    /// 셸 전체를 코드로 구성한다. 순서:
    /// ① 테마(UiScale 반영)와 배경 ② 메뉴바·상태 라인·셸프 줄(탭 셸프 + Bridge 영역)
    /// ③ 중앙 스플릿(툴박스+왼쪽 도크 | 뷰포트 레이아웃 | 오른쪽 도크) ④ Outliner/Properties 도킹 패널
    /// ⑤ 헬프 라인·타임 슬라이더 ⑥ DockManager 설정 ⑦ ToolContext/ToolManager/툴 등록, 패널별 입력·파이 콜백 연결
    /// ⑧ ShellInput(핫키) ⑨ 액션 등록 → 메뉴·상태 라인·툴박스·셸프 생성 ⑩ Action Popup·SpinDrag
    /// ⑪ 문서/선택/Undo 이벤트 구독 ⑫ 표시 설정 적용, 기본 툴 선택, 버튼 상태 동기화, 도킹 레이아웃 복원.
    /// 액션은 메뉴/셸프보다 먼저 등록되어야 한다(메뉴 빌드 시 액션 라벨·단축키를 조회하므로).
    /// </summary>
    public override void _Ready()
    {
        Instance = this;
        // s = UI 배율(화면 DPI × Preferences UI Scale). 모든 픽셀 상수에 곱한다.
        float s = CubeApp.Instance.UiScale;
        Theme = MayaTheme.Build(s);
        SetAnchorsPreset(LayoutPreset.FullRect);

        // 전체 배경 패널(입력은 통과)과 세로 루트 컨테이너
        var bg = new Panel { Name = "Background", MouseFilter = MouseFilterEnum.Ignore };
        bg.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(bg);

        var root = new VBoxContainer { Name = "Root" };
        root.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(root);

        // 메뉴바와 상태 라인(어두운 배경으로 감쌈)
        MenuBar = new MenuBar { Name = "MenuBar", Flat = true };
        // 메뉴 항목의 단축키 표시(accelerator)는 표시 전용이다. 키는 ShellInput만 처리한다(텍스트 칸 포커스 무시, viewport 컨텍스트).
        // 예전에는 MenuBar가 처리되지 않은 키를 accelerator로 실행해, 숫자 칸에 입력 중 F9/F/4 등이 그대로 실행되고 viewport 전용 키(F/A/1~8)가 뷰포트 밖에서도 동작했다.
        MenuBar.SetDisableShortcuts(true);
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
        // Bridge 영역: 어두운 패널 안에 'Bridge' 제목 라벨과 아이콘 버튼 줄(BridgeShelf)
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

        // 오른쪽 스플릿: 뷰포트 레이아웃(남는 폭 전부) | 오른쪽 도크
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

        // 헬프 라인(하단): 텍스트 + 로그 버튼 등은 BuildHelpRow가 구성
        HelpLine = new Label { Name = "HelpLine", Text = "Select a tool.", CustomMinimumSize = new Vector2(0, 20 * s) };
        root.AddChild(Wrap(BuildHelpRow(s), MayaTheme.PanelDark));
        // Time Slider(가져온 애니메이션 재생): HelpLine 바로 위
        BuildTimeSlider(root, root.GetChildCount() - 1, s);

        // 도킹 관리자: 두 도크와 스플리터를 넘겨 폭 조절·놓기 판정을 맡기고, 기본 패널 둘을 등록
        Dock = new Docking.DockManager { Name = "DockManager" };
        AddChild(Dock);
        Dock.Setup(this, LeftDock, RightDock, _mainSplit, _rightSplit);
        Dock.Register(OutlinerWindow);
        Dock.Register(PropertiesWindow);

        // 문서 미러링 시작(뷰포트 SceneView, Outliner 트리, Properties)
        Layout.Bind(Document);
        Outliner.Bind(Document);
        Properties.Bind(Document);

        // --- 서비스
        ToolContext = new ToolContext { Doc = Document, Viewport = Layout.Active, Settings = Settings, SetHelp = t => HelpLine.Text = t };
        ToolContext.AxisOrientation = Enum.TryParse<AxisOrientation>(Settings.AxisOrientation, out var ao) ? ao : AxisOrientation.World;
        ToolContext.AxisOrientationChanged += _ => { Settings.AxisOrientation = ToolContext.AxisOrientation.ToString(); SyncAxisButtons(); };
        Tools = new ToolManager(ToolContext);
        RegisterTools();
        // 패널마다 콜백 연결: 활성 패널일 때만 툴이 입력을 받고, 모달 툴이면 휠/RMB를 툴에 먼저 주며,
        // 파이 메뉴는 Ctrl = Select 파이, Shift = Edit(Context) 파이, 수식어 없음 = 모드 파이.
        // 파이 항목은 동적 항목(Run)이면 직접 실행, 아니면 액션 ID로 호출.
        foreach (var p in Layout.Panels)
        {
            var panel = p;
            panel.ToolInput = e => Layout.Active == panel && Tools.HandleInput(e);
            panel.ModalTool = () => Tools.Current is IModalTool;
            panel.PieItems = (shift, ctrl) => ctrl ? PieMenus.SelectMenu(this) : shift ? PieMenus.ContextMenu(this) : PieMenus.ModeMenu(this);
            panel.PieExecute = item => { if (item.Run != null) item.Run(); else Actions.Invoke(item.ActionId); };
        }
        // 활성 패널이 바뀌면 툴 컨텍스트의 뷰포트도 바꿔 기즈모가 그 패널로 옮겨지게 한다
        Layout.ActiveChanged += p => ToolContext.Viewport = p;

        // 핫키 라우터: 마우스가 어느 뷰포트 위에 있을 때만 viewport 컨텍스트 단축키가 동작
        Hotkeys = new ShellInput { Name = "ShellInput", Actions = Actions, IsViewportContext = () => Layout.AnyHovered, IsBusy = () => Tools.Current is Tools.TransformToolBase { IsDragging: true } };
        Hotkeys.SpaceDown += OnSpaceDown;
        Hotkeys.SpaceUp += OnSpaceUp;
        AddChild(Hotkeys);

        // 액션 등록 → 메뉴/상태 라인/툴박스/셸프 생성(모두 액션 ID를 참조하므로 등록이 먼저)
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

        // --- 이벤트 구독: 버튼 상태·창 제목(dirty 표시)·Poly Count 갱신
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
        // 저장된 표시 설정(Wireframe on Shaded, 그리드)을 모든 패널에 적용
        foreach (var p in Layout.Panels)
        {
            p.Display.WireOnShaded = Settings.WireOnShaded;
            p.Display.ShowGrid = Settings.ShowGrid;
            p.Display.RefreshAll();
        }
        // 그리드 간격, 4분할 여부, 기본 툴(Select)과 버튼 초기 상태
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
        // 창 닫기는 _Notification(WMCloseRequest)에서 저장 확인 후 직접 종료한다
        GetTree().AutoAcceptQuit = false;
        Viewport.GrabFocus();
    }

    // ---------------------------------------------------------------- Space: 탭 = 1/4분할 토글, 홀드 = 뷰 파이 메뉴

    /// <summary>Space를 누른 시각(ms). 뗄 때 300ms 미만이면 탭(레이아웃 토글)으로 판정한다.</summary>
    private ulong _spaceDownMs;
    /// <summary>Space 홀드로 뷰 파이를 연 패널(마우스가 올라가 있던 패널). 없으면 null.</summary>
    private ViewportPanel? _spacePanel;

    /// <summary>Space 누름: 시각을 기록하고 마우스 아래 패널에 뷰 전환 파이 메뉴(PieMenus.ViewMenu)를 연다.</summary>
    private void OnSpaceDown()
    {
        _spaceDownMs = Time.GetTicksMsec();
        _spacePanel = Layout.Hovered;
        _spacePanel?.OpenPieAtMouse(PieMenus.ViewMenu(this));
    }

    /// <summary>
    /// Space 뗌: 파이에서 고른 항목이 있으면 그 액션을 실행하고, 고른 것 없이 짧게 눌렀다 뗐으면(탭)
    /// 단일 ↔ 4분할 레이아웃을 토글한다(view.toggleLayout).
    /// </summary>
    private void OnSpaceUp()
    {
        bool tap = Time.GetTicksMsec() - _spaceDownMs < 300;
        var chosen = _spacePanel?.ReleasePie();
        _spacePanel = null;
        if (chosen != null && chosen.Enabled) { Actions.Invoke(chosen.ActionId); return; }
        if (tap) Actions.Invoke("view.toggleLayout");
    }

    // ---------------------------------------------------------------- 상태 라인 / 툴박스 / 셸프

    /// <summary>
    /// 상태 라인을 채운다: 선택 모드 토글 버튼 4개(액션 mode.*), Camera-based 선택 체크박스(설정 직접 연동),
    /// 그리드/점 스냅 토글 버튼(snap.grid/snap.point). 홀드 키 변경 이벤트에 SyncStatusLine을 연결한다.
    /// </summary>
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

    /// <summary>
    /// 툴박스를 채운다: 툴 버튼(Select/Lasso(미구현이라 비활성)/Move/Rotate/Scale → tool.* 액션),
    /// 조작기 축 방향 버튼(ToolContext.AxisOrientation 직접 설정), 아래쪽 여백 뒤 Preferences/Undo/Redo 버튼.
    /// </summary>
    private void BuildToolBox(float s)
    {
        int icon = (int)(22 * s);
        foreach (var (id, iconName, tip) in new[] { ("select", "select", "Select Tool (Q)"), ("lasso", "lasso", "Lasso Tool"), ("move", "move", "Move Tool (W)"), ("rotate", "rotate", "Rotate Tool (E)"), ("scale", "scale", "Scale Tool (R)") })
        {
            var b = Icons.IconButton(iconName, tip, icon, toggle: true);
            string tool = id;
            b.Pressed += () => Actions.Invoke("tool." + tool);
            _toolButtons[id] = b;
            ToolBox.AddChild(b);
        }
        ToolBox.AddChild(new HSeparator());
        // 축 방향 버튼: 누르면 ToolContext.AxisOrientation 설정 → AxisOrientationChanged로 설정 저장·버튼 동기화
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
        // 탭 상태별 스타일박스 생성기: 배경색 + 좌우 넉넉한 여백, 선택 탭은 위쪽 강조 테두리(top)
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

    /// <summary>
    /// 모든 셸프 탭(Polygons/UV/Bridge 영역/Rigging/Light/Render)에 버튼을 채운다.
    /// 각 그룹은 (액션 ID, 라벨, 아이콘 이름) 배열이며 그룹 사이에 세로 구분선을 넣는다.
    /// 옵션 명령은 항상 '*Apply'(마지막 옵션으로 즉시 실행) 액션을 쓴다.
    /// 끝으로 선택/모드/툴 변경 시 RefreshShelf가 버튼 활성·체크 상태를 갱신하도록 구독한다.
    /// </summary>
    private void BuildShelf(float s)
    {
        // 버튼 줄 하나를 그룹 목록으로 채운다(첫 그룹 앞에는 구분선 없음)
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
            new[] { ("mesh.addDivisionsApply", "Add Divisions", "shelf_adddiv"), ("mesh.connect", "Connect", "shelf_connect"), ("mesh.pokeApply", "Poke", "shelf_poke"), ("mesh.fillHole", "Fill Hole", "shelf_fillhole"), ("mesh.mirrorApply", "Mirror", "shelf_mirror"), ("mesh.arrayApply", "Array", "shelf_array") },
            new[] { ("mesh.booleanUnionApply", "Union", "shelf_bool_union"), ("mesh.booleanDifferenceApply", "Difference", "shelf_bool_difference"), ("mesh.booleanIntersectionApply", "Intersect", "shelf_bool_intersection") },
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
            new[] { ("render.fxaa", "FXAA", "render_fxaa"), ("render.smaa", "SMAA", "render_smaa"), ("render.taa", "TAA", "render_taa"), ("render.debanding", "Debanding", "render_debanding"), ("render.postReset", "FX Off", "render_off") });
        // 선택·모드·툴이 바뀌면 CanExecute/IsChecked가 달라지므로 버튼 상태를 다시 맞춘다
        Document.Selection.Changed += RefreshShelf;
        Document.Selection.ModeChanged += RefreshShelf;
        Tools.ToolChanged += _ => RefreshShelf();
        // 메뉴·단축키·HUD로 토글 액션(렌더 설정, 셰이딩 모드 등)을 실행해도 셸프 토글 표시가 따라가도록(전에는 셸프 버튼을 직접 눌렀을 때만 갱신)
        Actions.Invoked += _ => RefreshShelf();
        // 셰이딩 모드 같은 패널별 체크 상태는 활성 패널이 바뀌면 달라진다
        Layout.ActiveChanged += _ => RefreshShelf();
        RefreshShelf();
    }

    /// <summary>셸프 버튼 활성/체크 상태를 액션의 CanExecute/IsChecked로 맞춘다.</summary>
    public void RefreshShelf()
    {
        foreach (var (b, id) in _shelfButtons)
        {
            if (!IsInstanceValid(b)) continue;
            var a = Actions.Get(id); if (a == null) continue;
            b.Disabled = !a.Enabled;
            if (a.IsChecked != null) b.SetPressedNoSignal(a.IsChecked());
        }
    }

    /// <summary>셸프 버튼: 아이콘 위, 텍스트 아래.</summary>
    private Button ShelfButton(string action, string label, string icon, float s)
    {
        // 액션이 등록되지 않았으면(a == null) 버튼은 비활성으로 남긴다. IsChecked가 있으면 토글 버튼.
        var a = Actions.Get(action);
        var b = new Button { Text = label, FocusMode = FocusModeEnum.None, TooltipText = a?.Label ?? action, CustomMinimumSize = new Vector2(56 * s, 52 * s), IconAlignment = HorizontalAlignment.Center, VerticalIconAlignment = VerticalAlignment.Top, ExpandIcon = false };
        b.AddThemeFontSizeOverride("font_size", (int)(11 * s));
        var tex = Icons.Get(icon, (int)(22 * s));
        if (tex != null) b.Icon = tex;
        if (a?.IsChecked != null) b.ToggleMode = true;
        // 클릭 → 액션 실행 후 즉시 상태 갱신(토글 액션의 눌림 표시를 바로 맞추기 위해)
        b.Pressed += () => { Actions.Invoke(action); RefreshShelf(); };
        if (a == null) b.Disabled = true;
        _shelfButtons.Add((b, action));
        return b;
    }

    /// <summary>현재 선택 모드에 해당하는 상태 라인 모드 버튼만 눌림 표시한다(신호 없이).</summary>
    private void RefreshModeButtons()
    {
        var mode = Document.Selection.Mode;
        foreach (var (m, b) in _modeButtons) b.SetPressedNoSignal(m == mode);
    }

    /// <summary>Undo/Redo 버튼의 활성 여부와 툴팁(다음에 되돌릴/다시 할 명령 이름)을 갱신하고 셸프도 갱신한다.</summary>
    private void RefreshUndoButtons()
    {
        _undoBtn.Disabled = !Document.Undo.CanUndo;
        _redoBtn.Disabled = !Document.Undo.CanRedo;
        _undoBtn.TooltipText = Document.Undo.UndoName != null ? $"Undo {Document.Undo.UndoName}" : "Undo";
        _redoBtn.TooltipText = Document.Undo.RedoName != null ? $"Redo {Document.Undo.RedoName}" : "Redo";
        RefreshShelf();
    }

    /// <summary>현재 툴에 해당하는 툴박스 버튼만 눌림 표시한다.</summary>
    private void RefreshToolButtons()
    {
        foreach (var (id, b) in _toolButtons) b.SetPressedNoSignal(Tools.Current?.Id == id);
    }

    /// <summary>현재 ToolContext.AxisOrientation에 해당하는 축 방향 버튼만 눌림 표시한다.</summary>
    public void SyncAxisButtons()
    {
        foreach (var (a, b) in _axisButtons) b.SetPressedNoSignal(ToolContext.AxisOrientation == a);
    }

    /// <summary>
    /// 창 닫기 요청(WMCloseRequest): 저장하지 않은 변경이 있으면 확인 창을 띄우고,
    /// 확정되면 도킹 레이아웃과 설정을 저장한 뒤 종료한다(AutoAcceptQuit = false라 직접 Quit).
    /// </summary>
    public override void _Notification(int what)
    {
        if (what == NotificationWMCloseRequest) RequestQuit();
    }

    /// <summary>
    /// 종료 요청(창 닫기·File → Exit 공통): 저장하지 않은 변경이 있으면 확인 후, 도킹 레이아웃과 설정을 저장하고 종료한다.
    /// (전에는 File → Exit이 확인 없이 바로 종료해 변경을 잃고 레이아웃도 저장하지 않았다.)
    /// </summary>
    public void RequestQuit() => SceneFiles.ConfirmDiscard(() => { Dock.SaveLayout(); Settings.Save(); GetTree().Quit(); });

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

    /// <summary>
    /// 뷰포트 최대화 토글(Ctrl+Space): 상태 라인·셸프 줄·왼쪽 행(툴박스+왼쪽 도크)·헬프 라인을 숨기거나 보이고,
    /// 오른쪽 도크는 DockManager.SetMaximized가 처리한다. 타임 슬라이더 표시 여부도 다시 계산한다.
    /// </summary>
    public void ToggleMaximizeViewport()
    {
        _maximized = !_maximized;
        foreach (var n in new Control[] { StatusLine.GetParent<Control>(), ShelfRow, _mainSplit.GetChild<Control>(0), HelpLine.GetParent().GetParent<Control>() })
            n.Visible = !_maximized;
        Dock.SetMaximized(_maximized);
        UpdateTimeSliderVisibility();
    }

    // ---------------------------------------------------------------- 헬퍼

    /// <summary>
    /// 컨트롤을 단색 배경(여백 2px) PanelContainer로 감싸 돌려준다. expandH면 가로로 꽉 채운다.
    /// </summary>
    /// <param name="inner">감쌀 내용 컨트롤.</param>
    /// <param name="bg">배경색.</param>
    /// <param name="expandH">true면 내용과 패널 모두 가로 ExpandFill.</param>
    /// <returns>내용을 담은 PanelContainer(이름 = 내용 이름 + "Panel").</returns>
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
        // 떠 있을 때 최소 크기(UI 배율 반영)
        p.MinPanelSize = new Vector2(180, 160) * CubeApp.Instance.UiScale;
        return p;
    }
}
