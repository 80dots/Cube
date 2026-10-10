using Cube.Core.Commands;
using Cube.Core.Scene;
using Cube.Core.Selection;
using Godot;
using NVec3 = System.Numerics.Vector3;

namespace Cube.App.UI.Docks;

/// <summary>
/// Properties 패널(Maya Channel Box + INPUTS 역할).
/// Transformation 그룹: 활성 오브젝트의 Translate/Rotate/Scale(편집은 TransformNodesCommand).
/// History 그룹: 메시 구성 이력 스택(최신이 위). 항목을 고르면 그 연산의 파라미터(Bevel Distance, Translate 등)를 수정할 수 있고
/// 수정은 EditHistoryCommand로 그 항목부터 다시 실행된다.
/// Material 그룹(메시일 때): 할당 머티리얼 선택(AssignMaterialCommand) + MaterialPropsEditor로 할당된 머티리얼 속성 편집.
/// Light 그룹(라이트일 때): Type/Color/Intensity/Range/Cone Angle(SetLightCommand). Range는 Directional이 아닐 때, Cone Angle은 Spot일 때만 보인다.
/// 동기화 규칙: 문서/선택이 바뀌면 <see cref="Refresh"/>가 필드 값을 다시 채우며, 그동안 <see cref="_updating"/>을 켜서
/// 필드의 ValueChanged가 명령을 만들지 않게 한다. 숫자 칸 가운데 버튼 드래그(SpinDrag) 한 번은 Undo 한 단계로 합친다.
/// </summary>
public partial class PropertiesPanel : VBoxContainer
{
    /// <summary>바인딩된 문서.</summary>
    private Document _doc = null!;
    /// <summary>패널 맨 위 제목(대상 노드 이름, 컴포넌트 모드에서 대상이 없으면 "(component mode)").</summary>
    private Label _title = null!;
    /// <summary>트랜스폼 숫자 칸 12개. 인덱스 = 행×3 + 열, 행 0 Translate / 1 Rotate(도) / 2 Scale / 3 Pivot, 열 0 X / 1 Y / 2 Z.</summary>
    private readonly SpinBox[] _fields = new SpinBox[12];
    /// <summary>프로그램이 필드 값을 채우는 중 표시. true면 ValueChanged/ItemSelected 등에서 명령을 만들지 않는다.</summary>
    private bool _updating;
    /// <summary>현재 표시 중인 대상 노드 ID(없으면 None).</summary>
    private NodeId _node;

    /// <summary>구성 이력 목록(표시 순서 = 최신이 위, 즉 목록 인덱스 0 = History의 마지막 항목).</summary>
    private ItemList _history = null!;
    /// <summary>
    /// SpinDrag 병합 상태. _dragId = 마지막 명령을 만든 SpinDrag 드래그 번호(0 = 드래그 아님),
    /// _dragCmd = 그 드래그가 마지막으로 넣은 TransformNodesCommand(다음 변경 때 Undo하고 처음 값부터 다시 만든다).
    /// </summary>
    private int _dragId; private TransformNodesCommand? _dragCmd;
    /// <summary>Light 그룹 컨테이너(라이트 노드일 때만 보임).</summary>
    private Control _lightGroup = null!;
    /// <summary>라이트 종류 드롭다운(항목 순서 = LightType 열거형 순서).</summary>
    private OptionButton _lightType = null!;
    /// <summary>라이트 색 선택 버튼. 팝업이 닫힐 때 한 번만 커밋한다(드래그 중 명령 폭주 방지).</summary>
    private ColorPickerButton _lightColor = null!;
    /// <summary>라이트 세기 / 범위(m) / 스폿 원뿔 각도(도) 숫자 칸.</summary>
    private SpinBox _lightIntensity = null!, _lightRange = null!, _lightAngle = null!;
    /// <summary>Range·Cone Angle 행 컨테이너(라이트 종류에 따라 숨김).</summary>
    private Control _lightRangeRow = null!, _lightAngleRow = null!;
    /// <summary>Material 그룹 컨테이너(메시 노드일 때만 보임).</summary>
    private Control _materialGroup = null!;
    /// <summary>Image Plane 그룹(이미지 플레인 노드일 때만 보임)과 그 칸들.</summary>
    private Control _imagePlaneGroup = null!;
    private LineEdit _ipPath = null!;
    private SpinBox _ipWidth = null!, _ipHeight = null!, _ipOpacity = null!;
    private OptionButton _ipView = null!;
    private CheckBox _ipLocked = null!, _ipKeepAspect = null!;
    private static readonly string[] ImagePlaneViews = { "All Views", "persp", "front", "side", "top", "back", "left", "bottom" };
    /// <summary>할당 머티리얼 드롭다운. 항목 ID = 머티리얼 ID(0 = 내장 lambert1).</summary>
    private OptionButton _materialPick = null!;
    /// <summary>할당된 머티리얼의 속성 편집기(Material Editor와 같은 컴포넌트).</summary>
    private MaterialPropsEditor _materialProps = null!;
    /// <summary>lambert1(편집할 수 없는 기본 머티리얼)일 때 보이는 안내 문구.</summary>
    private Label _materialNote = null!;
    /// <summary>선택한 히스토리 항목의 파라미터 편집 행들이 들어가는 상자.</summary>
    private VBoxContainer _paramBox = null!;
    /// <summary>이력이 없을 때 보이는 "(no construction history)" 라벨.</summary>
    private Label _historyEmpty = null!;
    private int _historyIndex = -1;          // 선택된 히스토리 항목(오래된 것부터 센 인덱스)
    /// <summary>현재 파라미터 상자에 들어 있는 행 컨트롤들(다시 만들 때 QueueFree로 지운다).</summary>
    private readonly List<Control> _paramControls = new();
    /// <summary>파라미터 상자를 만든 기준(노드, 항목 인덱스, 파라미터 이름·종류 서명). 같으면 행을 다시 만들지 않고 값만 고친다(MMB 드래그 중인 칸이 지워지지 않도록).</summary>
    private (NodeId node, int index, string sig)? _paramsKey;
    /// <summary>현재 파라미터 숫자 칸(이름, 성분, 칸). 값만 고칠 때 쓴다.</summary>
    private readonly List<(string name, int comp, SpinBox spin)> _paramSpins = new();
    /// <summary>
    /// 라이트·히스토리 파라미터의 SpinDrag 병합 상태: 마지막 명령을 만든 드래그 번호와 그 명령.
    /// 같은 드래그의 다음 변경은 그 명령을 Undo하고 처음 값부터 다시 만들어 Undo 한 단계로 합친다.
    /// </summary>
    private int _mergeDragId; private ICommand? _mergeCmd;

    /// <summary>
    /// 문서에 연결한다. 머티리얼 편집기를 셸에 연결하고, 선택/모드 변경 → 전체 갱신,
    /// 트랜스폼·이름·리셋·삭제·라이트·머티리얼 변경 → 전체 갱신, 메시 위상/형상·이력 변경 → 이력만 갱신하도록 구독한다.
    /// </summary>
    public void Bind(Document doc)
    {
        _doc = doc;
        _materialProps.Setup(Shell.Instance);
        _materialProps.ShowName = true;
        doc.Selection.Changed += Refresh;
        doc.Selection.ModeChanged += Refresh;
        doc.Changed += c =>
        {
            if (c.Kind is ChangeKind.TransformChanged or ChangeKind.NodeRenamed or ChangeKind.Reset or ChangeKind.NodeRemoved or ChangeKind.LightChanged or ChangeKind.ImagePlaneChanged or ChangeKind.MaterialChanged) Refresh();
            else if (c.Kind is ChangeKind.MeshTopology or ChangeKind.HistoryChanged or ChangeKind.MeshGeometry) RefreshHistory();
        };
        Refresh();
    }

    /// <summary>
    /// 패널 UI 구성(노드 생성 시). 순서: 제목 → Transformation 격자(헤더 행 + Translate/Rotate/Scale/Pivot × XYZ)
    /// → Material 그룹 → Light 그룹 → History 헤더/빈 안내/목록 → 파라미터 상자.
    /// </summary>
    public override void _Ready()
    {
        float s = CubeApp.Instance.UiScale;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _title = new Label { Text = "" };
        AddChild(_title);

        AddChild(Header("Transformation", s));
        // 4열 격자: 첫 열 = 행 이름, 나머지 = X/Y/Z 숫자 칸.
        var grid = new GridContainer { Columns = 4, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        grid.AddChild(new Label { Text = "" });
        foreach (var h in new[] { "X", "Y", "Z" }) grid.AddChild(new Label { Text = h, HorizontalAlignment = HorizontalAlignment.Center, SizeFlagsHorizontal = SizeFlags.ExpandFill });
        string[] rows = { "Translate", "Rotate", "Scale", "Pivot" };
        // 각 숫자 칸은 자기 인덱스(행×3+열)를 캡처해 OnValueChanged로 넘긴다.
        for (int r = 0; r < 4; r++)
        {
            grid.AddChild(new Label { Text = rows[r] });
            for (int c = 0; c < 3; c++)
            {
                var sb = Spin(s);
                int idx = r * 3 + c;
                sb.ValueChanged += v => OnValueChanged(idx, (float)v);
                // MMB 드래그 속도: 이동/스케일/피벗 0.01, 회전 0.5°(증분 0.001 그대로면 100px에 0.5°밖에 안 돌았음)
                sb.SetMeta(SpinDrag.DragUnitMeta, r == 1 ? 0.5 : 0.01);
                _fields[idx] = sb;
                grid.AddChild(sb);
            }
        }
        AddChild(grid);

        // Material 그룹: 할당 선택 + 할당된 머티리얼의 속성 편집(Material Editor와 같은 편집기)
        var matBox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        matBox.AddChild(Header("Material", s));
        _materialPick = new OptionButton { FocusMode = FocusModeEnum.None, SizeFlagsHorizontal = SizeFlags.ExpandFill, TooltipText = "Material assigned to this object" };
        // 다른 머티리얼을 고르면(프로그램 갱신 중이 아니고 실제로 바뀔 때만) 할당 명령을 Undo 스택에 넣는다.
        _materialPick.ItemSelected += i => { if (!_updating && !_node.IsNone) { int id = (int)_materialPick.GetItemId((int)i); if (_doc.Find(_node)?.MaterialId != id) _doc.Undo.Push(new AssignMaterialCommand(new[] { _node }, id)); } };
        matBox.AddChild(LabeledRow("Assigned", _materialPick, s));
        _materialNote = new Label { Text = "lambert1 is the built-in default material.", Modulate = new Color(1, 1, 1, 0.6f), AutowrapMode = TextServer.AutowrapMode.WordSmart };
        matBox.AddChild(_materialNote);
        _materialProps = new MaterialPropsEditor { Name = "MaterialProps" };
        matBox.AddChild(_materialProps);
        _materialGroup = matBox;
        AddChild(matBox);

        // Light 그룹
        var lightBox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        lightBox.AddChild(Header("Light", s));
        // 라이트 속성 편집은 모두 CommitLight로 복제본을 바꿔 비교 후 명령을 만든다.
        _lightType = new OptionButton { FocusMode = FocusModeEnum.None, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        foreach (var t in Enum.GetNames<Core.Scene.LightType>()) _lightType.AddItem(t);
        _lightType.ItemSelected += i => CommitLight(l => l.Type = (Core.Scene.LightType)(int)i);
        lightBox.AddChild(LabeledRow("Type", _lightType, s));
        _lightColor = new ColorPickerButton { EditAlpha = false, SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 22 * s) };
        _lightColor.PopupClosed += () => CommitLight(l => l.Color = new NVec3(_lightColor.Color.R, _lightColor.Color.G, _lightColor.Color.B));
        lightBox.AddChild(LabeledRow("Color", _lightColor, s));
        _lightIntensity = Spin(s); _lightIntensity.MinValue = 0; _lightIntensity.AllowLesser = false; _lightIntensity.Step = 0.01; _lightIntensity.ValueChanged += v => CommitLight(l => l.Intensity = (float)v);
        lightBox.AddChild(LabeledRow("Intensity", _lightIntensity, s));
        _lightRange = Spin(s); _lightRange.MinValue = 0.01; _lightRange.AllowLesser = false; _lightRange.Step = 0.1; _lightRange.ValueChanged += v => CommitLight(l => l.Range = (float)v);
        _lightRangeRow = LabeledRow("Range", _lightRange, s); lightBox.AddChild(_lightRangeRow);
        _lightAngle = Spin(s); _lightAngle.MinValue = 1; _lightAngle.MaxValue = 179; _lightAngle.AllowLesser = false; _lightAngle.AllowGreater = false; _lightAngle.Step = 0.5; _lightAngle.ValueChanged += v => CommitLight(l => l.SpotAngle = (float)v);
        _lightAngleRow = LabeledRow("Cone Angle", _lightAngle, s); lightBox.AddChild(_lightAngleRow);
        // 음수 세기·범위, 1~179° 밖의 원뿔 각도는 받지 않는다(공통 Spin은 범위 밖 입력을 허용하므로 다시 막음)
        _lightGroup = lightBox;
        AddChild(lightBox);

        // Image Plane 그룹(v0.0.71): 이미지 경로(Browse/제거), 크기(비율 유지 옵션), 불투명도, 표시 뷰, 잠금.
        var ipBox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        ipBox.AddChild(Header("Image Plane", s));
        var pathRow = new HBoxContainer();
        _ipPath = new LineEdit { Editable = false, SizeFlagsHorizontal = SizeFlags.ExpandFill, PlaceholderText = "(no image)" };
        pathRow.AddChild(_ipPath);
        var browse = new Button { Text = "Browse...", FocusMode = FocusModeEnum.None };
        browse.Pressed += () => Shell.Instance.PickImageFile("Image Plane Image", path => CommitImagePlane(ip => { ip.ImagePath = path; var img = Image.LoadFromFile(path); if (_ipKeepAspect.ButtonPressed && img != null && img.GetWidth() > 0) ip.Height = ip.Width * img.GetHeight() / img.GetWidth(); }));
        pathRow.AddChild(browse);
        var clear = new Button { Text = "X", FocusMode = FocusModeEnum.None, TooltipText = "Remove image" };
        clear.Pressed += () => CommitImagePlane(ip => ip.ImagePath = "");
        pathRow.AddChild(clear);
        ipBox.AddChild(LabeledRow("Image", pathRow, s));
        _ipWidth = Spin(s); _ipWidth.MinValue = 0.001; _ipWidth.AllowLesser = false; _ipWidth.Step = 0.01;
        _ipWidth.ValueChanged += v => CommitImagePlane(ip => { float aspect = ip.Width > 0 ? ip.Height / ip.Width : 1f; ip.Width = (float)v; if (_ipKeepAspect.ButtonPressed) ip.Height = ip.Width * aspect; });
        ipBox.AddChild(LabeledRow("Width", _ipWidth, s));
        _ipHeight = Spin(s); _ipHeight.MinValue = 0.001; _ipHeight.AllowLesser = false; _ipHeight.Step = 0.01;
        _ipHeight.ValueChanged += v => CommitImagePlane(ip => { float aspect = ip.Height > 0 ? ip.Width / ip.Height : 1f; ip.Height = (float)v; if (_ipKeepAspect.ButtonPressed) ip.Width = ip.Height * aspect; });
        ipBox.AddChild(LabeledRow("Height", _ipHeight, s));
        _ipKeepAspect = new CheckBox { ButtonPressed = true, TooltipText = "Changing width or height keeps the image aspect ratio" };
        ipBox.AddChild(LabeledRow("Keep Aspect", _ipKeepAspect, s));
        _ipOpacity = Spin(s); _ipOpacity.MinValue = 0; _ipOpacity.MaxValue = 1; _ipOpacity.AllowLesser = false; _ipOpacity.AllowGreater = false; _ipOpacity.Step = 0.01;
        _ipOpacity.ValueChanged += v => CommitImagePlane(ip => ip.Opacity = (float)v);
        ipBox.AddChild(LabeledRow("Opacity", _ipOpacity, s));
        _ipView = new OptionButton { FocusMode = FocusModeEnum.None, SizeFlagsHorizontal = SizeFlags.ExpandFill, TooltipText = "Show in all views or only in one preset view (Maya: Looking Through Camera)" };
        foreach (var v in ImagePlaneViews) _ipView.AddItem(v);
        _ipView.ItemSelected += i => CommitImagePlane(ip => ip.OnlyView = i == 0 ? null : ImagePlaneViews[(int)i]);
        ipBox.AddChild(LabeledRow("Display In", _ipView, s));
        _ipLocked = new CheckBox { TooltipText = "Locked: not selectable in the viewport (select it in the Outliner)" };
        _ipLocked.Toggled += on => CommitImagePlane(ip => ip.Locked = on);
        ipBox.AddChild(LabeledRow("Locked", _ipLocked, s));
        _imagePlaneGroup = ipBox;
        AddChild(ipBox);

        AddChild(Header("History", s));
        _historyEmpty = new Label { Text = "(no construction history)", Modulate = new Color(1, 1, 1, 0.6f) };
        AddChild(_historyEmpty);
        // 남는 세로 공간을 모두 History 목록에 준다(고정 높이로 키우면 도크 최소 높이가 창보다 커져 레이아웃이 넘친다)
        _history = new ItemList { CustomMinimumSize = new Vector2(0, 120 * s), SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill, FocusMode = FocusModeEnum.Click };
        // 목록 인덱스는 최신이 0이므로 History 인덱스(오래된 것부터)로 바꿔 저장한다.
        _history.ItemSelected += i => { _historyIndex = HistoryCount - 1 - (int)i; RefreshParams(); };
        AddChild(_history);
        _paramBox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        AddChild(_paramBox);
    }

    /// <summary>어두운 배경의 그룹 헤더(제목 라벨)를 만든다.</summary>
    /// <param name="text">헤더 제목.</param>
    /// <param name="s">UI 배율.</param>
    private static Control Header(string text, float s)
    {
        var box = new PanelContainer();
        box.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = MayaTheme.PanelDark, ContentMarginLeft = 6 * s, ContentMarginTop = 2 * s, ContentMarginBottom = 2 * s });
        box.AddChild(new Label { Text = text });
        return box;
    }

    /// <summary>
    /// 패널 공통 숫자 칸을 만든다: 단계 0.001, 사실상 무제한 범위, 입력 중에는 반영하지 않고(UpdateOnTextChanged=false) Enter/포커스 해제 때 확정.
    /// 오른쪽 클릭 컨텍스트 메뉴를 끈다(뷰포트 파이 메뉴와 혼동 방지).
    /// </summary>
    private SpinBox Spin(float s)
    {
        var sb = new SpinBox { Step = 0.001, MinValue = -1e9, MaxValue = 1e9, AllowGreater = true, AllowLesser = true, CustomMinimumSize = new Vector2(56 * s, 0), UpdateOnTextChanged = false, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        sb.GetLineEdit().ContextMenuEnabled = false;
        // Enter로 확정하면 Maya처럼 포커스를 뷰포트로 돌린다
        sb.GetLineEdit().TextSubmitted += _ => CallDeferred(nameof(ReturnFocus));
        return sb;
    }

    /// <summary>키보드 포커스를 활성 뷰포트로 돌려 단축키가 바로 동작하게 한다(지연 호출).</summary>
    private static void ReturnFocus() => Shell.Instance?.Viewport.GrabFocus();

    /// <summary>"라벨 + 컨트롤" 가로 행을 만든다(라벨 폭 70px×배율, 컨트롤은 남는 폭을 채움).</summary>
    private static Control LabeledRow(string label, Control c, float s)
    {
        var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddChild(new Label { Text = label, CustomMinimumSize = new Vector2(70 * s, 0) });
        c.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        row.AddChild(c);
        return row;
    }

    /// <summary>
    /// 라이트 속성 변경을 명령으로 커밋한다. 현재 라이트를 복제해 <paramref name="change"/>를 적용하고, 값이 하나도 달라지지 않았으면 무시한다
    /// (Refresh가 값을 다시 넣을 때 생기는 이벤트로 빈 Undo 항목이 쌓이지 않도록).
    /// </summary>
    /// <param name="change">복제본에 적용할 변경.</param>
    private void CommitLight(Action<Core.Scene.LightShape> change)
    {
        if (_updating || _node.IsNone) return;
        var node = _doc.Find(_node); if (node?.Light == null) return;
        // 같은 MMB 드래그의 이전 명령은 되돌리고 처음 값부터 다시 만든다(드래그 한 번 = Undo 한 단계)
        UndoMergedDrag();
        var after = node.Light.Clone(); change(after);
        if (after.Type == node.Light.Type && after.Color == node.Light.Color && after.Intensity == node.Light.Intensity && after.Range == node.Light.Range && after.SpotAngle == node.Light.SpotAngle) return;
        PushMerged(new SetLightCommand(_node, after));
    }

    /// <summary>SpinDrag 드래그 중이고 Undo 스택 맨 위가 같은 드래그가 넣은 명령이면 그것을 Undo한다.</summary>
    private void UndoMergedDrag()
    {
        if (SpinDrag.ActiveDrag != 0 && SpinDrag.ActiveDrag == _mergeDragId && _mergeCmd != null && ReferenceEquals(_doc.Undo.LastCommand, _mergeCmd))
        {
            bool was = _updating; _updating = true;   // Undo 통지로 Refresh가 칸 값을 되돌리며 생기는 신호를 무시
            _doc.Undo.Undo();
            _updating = was;
        }
        _mergeCmd = null;
    }

    /// <summary>명령을 넣고, SpinDrag 드래그 중이면 다음 변경에서 합칠 수 있게 기억한다.</summary>
    private void PushMerged(ICommand cmd)
    {
        _doc.Undo.Push(cmd);
        _mergeDragId = SpinDrag.ActiveDrag; _mergeCmd = SpinDrag.ActiveDrag != 0 ? cmd : null;
    }

    /// <summary>Light 그룹을 노드의 라이트 값으로 채우고 종류에 따라 Range/Cone Angle 행을 보이거나 숨긴다. 라이트가 없으면 그룹을 숨긴다.</summary>
    private void RefreshLight(SceneNode? node)
    {
        var l = node?.Light;
        _lightGroup.Visible = l != null;
        if (l == null) return;
        _lightType.Selected = (int)l.Type;
        _lightColor.Color = new Color(l.Color.X, l.Color.Y, l.Color.Z);
        _lightIntensity.Value = l.Intensity; _lightRange.Value = l.Range; _lightAngle.Value = l.SpotAngle;
        _lightRangeRow.Visible = l.Type != Core.Scene.LightType.Directional;
        _lightAngleRow.Visible = l.Type == Core.Scene.LightType.Spot;
    }

    /// <summary>Image Plane 그룹 갱신(이미지 플레인 노드만).</summary>
    private void RefreshImagePlane(SceneNode? node)
    {
        var ip = node?.ImagePlane;
        _imagePlaneGroup.Visible = ip != null;
        if (ip == null) return;
        _ipPath.Text = ip.ImagePath; _ipPath.TooltipText = ip.ImagePath;
        _ipWidth.Value = ip.Width; _ipHeight.Value = ip.Height; _ipOpacity.Value = ip.Opacity;
        int vi = ip.OnlyView == null ? 0 : Array.FindIndex(ImagePlaneViews, v => string.Equals(v, ip.OnlyView, StringComparison.OrdinalIgnoreCase));
        _ipView.Selected = Math.Max(vi, 0);
        _ipLocked.SetPressedNoSignal(ip.Locked);
    }

    /// <summary>이미지 플레인 속성 변경을 명령으로 넣는다(복제본을 바꿔 비교; MMB 드래그는 한 Undo 단계로 합침).</summary>
    private void CommitImagePlane(Action<Core.Scene.ImagePlaneShape> change)
    {
        if (_updating || _node.IsNone) return;
        var node = _doc.Find(_node); if (node?.ImagePlane == null) return;
        UndoMergedDrag();
        var after = node.ImagePlane.Clone(); change(after);
        var cmd = new SetImagePlaneCommand(_node, after);
        _doc.Undo.Push(cmd);
        if (SpinDrag.ActiveDrag != 0) { _mergeDragId = SpinDrag.ActiveDrag; _mergeCmd = cmd; }
    }

    /// <summary>표시 대상 노드: 오브젝트 모드면 활성 오브젝트, 컴포넌트 모드면 활성 오브젝트 또는 컴포넌트가 선택된 노드.</summary>
    private SceneNode? TargetNode()
    {
        var sel = _doc.Selection;
        var n = _doc.Find(sel.ActiveObject);
        if (n == null && sel.IsComponentMode)
        {
            var id = sel.NodesWithComponents(sel.Mode).FirstOrDefault();
            if (!id.IsNone) n = _doc.Find(id);
        }
        return n;
    }

    /// <summary>
    /// 패널 전체 갱신: 대상 노드를 찾아 제목·트랜스폼 필드·머티리얼·라이트 그룹을 채우고 이력 목록을 갱신한다.
    /// 트랜스폼 필드는 오브젝트 모드에서만 편집 가능하다(컴포넌트 모드에서는 읽기 전용 표시).
    /// </summary>
    private void Refresh()
    {
        var sel = _doc.Selection;
        var node = TargetNode();
        _updating = true;
        // 대상이 없으면 필드를 0으로 비우고 편집 불가로 둔다.
        if (node == null)
        {
            _node = NodeId.None;
            _title.Text = sel.IsComponentMode ? "(component mode)" : "";
            foreach (var f in _fields) { f.Editable = false; f.Value = 0; }
            RefreshMaterial(null);
            RefreshLight(null);
            RefreshImagePlane(null);
        }
        else
        {
            _node = node.Id;
            _title.Text = node.Name;
            // 대상의 로컬 트랜스폼(채널 박스 값)을 행별로 채운다.
            var t = node.Local;
            Set(0, t.Translation); Set(1, t.RotationDegrees); Set(2, t.Scale); Set(3, t.Pivot);
            bool editable = sel.Mode == SelectMode.Object;
            foreach (var f in _fields) f.Editable = editable;
            RefreshMaterial(node);
            RefreshLight(node);
            RefreshImagePlane(node);
        }
        _updating = false;
        RefreshHistory();
    }

    /// <summary>
    /// Material 그룹 갱신(메시 노드만). 드롭다운을 lambert1 + 문서 머티리얼로 다시 채우고 할당된 항목을 선택한다.
    /// lambert1이면 속성 편집기 대신 안내 문구를 보인다.
    /// </summary>
    private void RefreshMaterial(SceneNode? node)
    {
        bool show = node?.Mesh != null;
        _materialGroup.Visible = show;
        if (!show) return;
        _materialPick.Clear();
        _materialPick.AddItem("lambert1", 0);
        foreach (var m in _doc.Materials) _materialPick.AddItem($"{m.Name}  [{m.Type}]", m.Id);
        // 할당 ID가 목록에 없으면(삭제된 머티리얼 등) lambert1을 선택한다.
        int idx = _materialPick.GetItemIndex(node!.MaterialId);
        _materialPick.Selected = idx >= 0 ? idx : 0;
        var def = _doc.FindMaterial(node.MaterialId);
        _materialProps.Visible = def != null;
        _materialNote.Visible = def == null;
        _materialProps.SetMaterial(def?.Id ?? 0);
    }

    /// <summary>대상 노드 메시의 이력 항목 수(메시가 없으면 0).</summary>
    private int HistoryCount => _doc.Find(_node)?.MeshShape?.History.Count ?? 0;

    /// <summary>
    /// 이력 목록을 다시 채운다(최신이 위). 편집 가능한 항목은 "이름  (파라미터=값, ...)"으로 표시한다.
    /// 이전 선택 인덱스가 유효하면 유지하고, 아니면 최신 항목을 선택한 뒤 파라미터 상자를 갱신한다.
    /// </summary>
    private void RefreshHistory()
    {
        var entries = _doc.Find(_node)?.MeshShape?.History;
        int prevSel = _historyIndex;
        _history.Clear();
        if (entries == null || entries.Count == 0)
        {
            _history.Visible = false; _historyEmpty.Visible = true; _historyIndex = -1; RefreshParams(); return;
        }
        _history.Visible = true; _historyEmpty.Visible = false;
        // History는 오래된 것부터 저장되어 있으므로 거꾸로 순회해 최신을 위에 둔다.
        for (int i = entries.Count - 1; i >= 0; i--)
        {
            var e = entries[i];
            string label = e.Name + (e.Editable ? "  (" + string.Join(", ", e.Params.Items.Select(p => p.Name + "=" + FormatParam(p))) + ")" : "");
            _history.AddItem(label);
        }
        if (prevSel < 0 || prevSel >= entries.Count) _historyIndex = entries.Count - 1;
        _history.Select(entries.Count - 1 - _historyIndex);
        RefreshParams();
    }

    /// <summary>히스토리 파라미터 값을 목록 표시용 문자열로 만든다(Vector3 = "x,y,z", Int, Bool = on/off, 나머지 Float).</summary>
    private static string FormatParam(HistoryParam p) => p.Kind switch
    {
        HistoryParamKind.Vector3 => $"{p.Value.X:0.###},{p.Value.Y:0.###},{p.Value.Z:0.###}",
        HistoryParamKind.Int => p.Int.ToString(),
        HistoryParamKind.Bool => p.Bool ? "on" : "off",
        _ => p.Float.ToString("0.###"),
    };

    /// <summary>
    /// 선택한 이력 항목의 파라미터 편집 행을 다시 만든다. 파라미터마다 라벨 + 숫자 칸(Vector3면 3개)을 두고
    /// 값이 바뀌면 <see cref="OnParamChanged"/>로 넘긴다. 범위는 파라미터 Min/Max(±1e9 이상이면 무제한 허용).
    /// </summary>
    private void RefreshParams()
    {
        var entries = _doc.Find(_node)?.MeshShape?.History;
        var cur = entries == null || _historyIndex < 0 || _historyIndex >= entries.Count ? null : entries[_historyIndex];
        // 같은 항목·같은 파라미터 구성이면 행을 다시 만들지 않고 값만 고친다(편집 → 재실행 통지마다 칸이 지워지면 MMB 드래그가 끊김)
        if (cur != null && cur.Editable && _paramsKey is { } key && key.node == _node && key.index == _historyIndex && key.sig == ParamSig(cur))
        {
            bool was = _updating; _updating = true;
            foreach (var (name, comp, spin) in _paramSpins)
            {
                if (!IsInstanceValid(spin)) continue;
                var v = cur.Params[name].Value;
                spin.SetValueNoSignal(comp == 0 ? v.X : comp == 1 ? v.Y : v.Z);
            }
            _updating = was;
            return;
        }
        // 이전 행들을 지운다.
        foreach (var c in _paramControls) c.QueueFree();
        _paramControls.Clear();
        _paramSpins.Clear();
        _paramsKey = null;
        if (cur == null) return;
        var entry = cur;
        float s = CubeApp.Instance.UiScale;
        if (!entry.Editable)
        {
            var l = new Label { Text = $"{entry.Name}: no editable parameters", Modulate = new Color(1, 1, 1, 0.6f) };
            _paramBox.AddChild(l); _paramControls.Add(l);
            return;
        }
        // 람다가 캡처할 항목 인덱스(이후 _historyIndex가 바뀌어도 이 행은 같은 항목을 가리킨다).
        int entryIndex = _historyIndex;
        _paramsKey = (_node, entryIndex, ParamSig(entry));
        foreach (var p in entry.Params.Items)
        {
            var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            row.AddChild(new Label { Text = p.Name, CustomMinimumSize = new Vector2(70 * s, 0) });
            int comps = p.Kind == HistoryParamKind.Vector3 ? 3 : 1;
            for (int c = 0; c < comps; c++)
            {
                var sb = Spin(s);
                sb.Step = p.Kind == HistoryParamKind.Int ? 1 : p.Step;
                sb.MinValue = Math.Max(p.Min, -1e9); sb.MaxValue = Math.Min(p.Max, 1e9);
                sb.AllowGreater = p.Max >= 1e9; sb.AllowLesser = p.Min <= -1e9;
                sb.Value = c == 0 ? p.Value.X : c == 1 ? p.Value.Y : p.Value.Z;
                string pname = p.Name; int comp = c;
                sb.ValueChanged += v => OnParamChanged(entryIndex, pname, comp, (float)v);
                row.AddChild(sb);
                _paramSpins.Add((pname, comp, sb));
            }
            _paramBox.AddChild(row); _paramControls.Add(row);
        }
    }

    /// <summary>히스토리 항목의 파라미터 구성 서명(이름·종류). 같으면 파라미터 행을 재사용한다.</summary>
    private static string ParamSig(HistoryEntry e) => e.Name + "|" + string.Join(";", e.Params.Items.Select(p => p.Name + ":" + p.Kind));

    /// <summary>
    /// 히스토리 파라미터 한 성분이 바뀌었을 때: 파라미터 복제본을 고쳐 값이 실제로 달라졌으면
    /// <see cref="EditHistoryCommand"/>를 넣는다(그 항목부터 끝까지 HistoryReplay로 다시 실행됨).
    /// </summary>
    /// <param name="entryIndex">History 인덱스(오래된 것부터).</param>
    /// <param name="name">파라미터 이름.</param>
    /// <param name="comp">성분(0 X, 1 Y, 2 Z; 스칼라는 X에 저장).</param>
    /// <param name="value">새 값.</param>
    private void OnParamChanged(int entryIndex, string name, int comp, float value)
    {
        if (_updating) return;
        var shape = _doc.Find(_node)?.MeshShape;
        if (shape == null || entryIndex < 0 || entryIndex >= shape.History.Count) return;
        // 같은 MMB 드래그의 이전 편집은 되돌리고 원래 값부터 다시 편집한다(드래그 한 번 = Undo 한 단계)
        UndoMergedDrag();
        if (entryIndex >= shape.History.Count) return;
        var entry = shape.History[entryIndex];
        var np = entry.Params.Clone();
        var p = np[name];
        var v = p.Value;
        if (comp == 0) v.X = value; else if (comp == 1) v.Y = value; else v.Z = value;
        p.Value = v;
        if (np.ValuesEqual(entry.Params)) return;
        // 명령 후 RefreshHistory가 같은 항목을 계속 선택하도록 인덱스를 고정한다.
        _historyIndex = entryIndex;
        PushMerged(new EditHistoryCommand(_node, entryIndex, np, $"Edit {entry.Name}"));
    }

    /// <summary>트랜스폼 필드 한 행(X/Y/Z)에 값을 소수 셋째 자리로 반올림해 넣는다.</summary>
    private void Set(int row, NVec3 v)
    {
        _fields[row * 3].Value = Math.Round(v.X, 3); _fields[row * 3 + 1].Value = Math.Round(v.Y, 3); _fields[row * 3 + 2].Value = Math.Round(v.Z, 3);
    }

    /// <summary>
    /// 트랜스폼 숫자 칸 변경 처리. Maya 채널 박스처럼 오브젝트 모드에서는 선택된 모든 오브젝트의 같은 채널을 그 값으로 바꾼다
    /// (표시 대상 노드 + 나머지 선택 오브젝트). 노드마다 해당 행/열 성분만 바꾼 새 로컬 트랜스폼을 바로 적용하고
    /// <see cref="TransformNodesCommand"/> 하나를 alreadyApplied로 넣는다. 피벗 행은 월드 행렬을 유지하며 피벗만 옮긴다.
    /// </summary>
    /// <param name="idx">필드 인덱스(행×3+열).</param>
    /// <param name="value">새 값.</param>
    private void OnValueChanged(int idx, float value)
    {
        if (_updating || _node.IsNone) return;
        var main = _doc.Find(_node); if (main == null) return;
        // 대상: 표시 노드가 먼저, 이어서 다른 선택 오브젝트(중복·없는 노드 제외)
        var targets = new List<SceneNode> { main };
        if (_doc.Selection.Mode == SelectMode.Object)
            foreach (var id in _doc.Selection.Objects)
                if (id != main.Id && _doc.Find(id) is { } n && !targets.Contains(n)) targets.Add(n);
        int row = idx / 3, col = idx % 3;
        // 가운데 버튼 드래그(SpinDrag) 한 번의 변경들은 Undo 한 단계로 합친다: 같은 드래그의 직전 명령을 빼고 처음 값부터의 명령으로 바꾼다
        if (SpinDrag.ActiveDrag != 0 && SpinDrag.ActiveDrag == _dragId && _dragCmd != null && ReferenceEquals(_doc.Undo.LastCommand, _dragCmd)
            && _dragCmd.Ids.Count == targets.Count && targets.All(t => _dragCmd.Ids.Contains(t.Id)))
        {
            bool was = _updating; _updating = true;
            _doc.Undo.Undo();
            _updating = was;
        }
        var ids = new List<NodeId>(); var befores = new List<Transform3>(); var afters = new List<Transform3>();
        foreach (var node in targets)
        {
            var before = node.Local; var after = before;
            // 행/열 분해 후 해당 벡터의 한 성분만 교체.
            NVec3 v = row == 0 ? after.Translation : row == 1 ? after.RotationDegrees : row == 2 ? after.Scale : after.Pivot;
            if (col == 0) v.X = value; else if (col == 1) v.Y = value; else v.Z = value;
            if (row == 0) after.Translation = v; else if (row == 1) after.RotationDegrees = v; else if (row == 2) after.Scale = v;
            else after = before.WithPivotKeepingMatrix(v); // 피벗 편집은 월드를 유지한다(Maya 피벗 이동과 같음)
            if (after == before) continue;
            ids.Add(node.Id); befores.Add(before); afters.Add(after);
        }
        if (ids.Count == 0) { _dragCmd = null; return; }
        // 문서를 직접 갱신하고 통지한 뒤, 이미 적용된 명령으로 Undo 스택에 넣는다.
        for (int i = 0; i < ids.Count; i++)
        {
            _doc.Find(ids[i])!.Local = afters[i];
            _doc.Notify(new DocChange(ChangeKind.TransformChanged, ids[i]));
        }
        var cmd = new TransformNodesCommand("Set Attribute", ids.ToArray(), befores.ToArray(), afters.ToArray());
        _doc.Undo.Push(cmd, alreadyApplied: true);
        // 이번 명령을 기억해 같은 드래그의 다음 변경에서 병합할 수 있게 한다.
        _dragId = SpinDrag.ActiveDrag; _dragCmd = SpinDrag.ActiveDrag != 0 ? cmd : null;
    }
}
