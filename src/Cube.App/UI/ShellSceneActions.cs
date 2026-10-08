using Cube.App.Tools;
using Cube.Core.Commands;
using Cube.Core.Rig;
using Cube.Core.Scene;
using Cube.Core.Selection;
using Godot;
using NVec3 = System.Numerics.Vector3;

namespace Cube.App.UI;

/// <summary>라이트 생성, Create Polygon, Material Editor, 스켈레톤 Mirror/Orient/Insert/축 표시 액션.</summary>
public partial class Shell
{
    /// <summary>Material Editor 패널(머티리얼 목록·생성·삭제·할당·속성). 처음 열 때 EnsureMaterialEditor가 만든다.</summary>
    public MaterialEditorWindow? MaterialEditor { get; private set; }

    /// <summary>
    /// 씬 관련 액션 등록: Create Polygon Tool, 라이트 생성(Directional/Point/Spot), Select All Lights, Material Editor 창,
    /// 스켈레톤 Insert Joint Tool·Mirror/Orient(옵션 창 + 마지막 옵션 즉시 실행), 조인트 표시(축/표시 여부/크기).
    /// </summary>
    private void RegisterSceneActions()
    {
        var doc = Document; var sel = doc.Selection;
        Actions.Register("create.polygonTool", "Create Polygon Tool", () => Tools.SetTool("createPolygon"), isChecked: () => Tools.Current?.Id == "createPolygon");
        Actions.Register("create.lightDirectional", "Directional Light", () => CreateLight(LightType.Directional), repeatable: true);
        Actions.Register("create.lightPoint", "Point Light", () => CreateLight(LightType.Point), repeatable: true);
        Actions.Register("create.lightSpot", "Spot Light", () => CreateLight(LightType.Spot), repeatable: true);
        Actions.Register("select.lights", "All Lights", SelectAllLights, canExecute: () => doc.Nodes.Values.Any(n => n.IsLight));
        Actions.Register("windows.materialEditor", "Material Editor", ToggleMaterialEditor, isChecked: () => MaterialEditor?.IsOpen ?? false);

        // 스켈레톤 액션 공통 조건: 오브젝트 모드에서 조인트가 하나 이상 선택됨
        bool JointSelected() => sel.Mode == SelectMode.Object && sel.Objects.Any(id => doc.Find(id)?.IsJoint == true);
        Actions.Register("skeleton.insertJointTool", "Insert Joint Tool", () => Tools.SetTool("insertJoint"), isChecked: () => Tools.Current?.Id == "insertJoint");
        Actions.Register("skeleton.mirror", "Mirror Joint...", ShowMirrorDialog, canExecute: JointSelected);
        Actions.Register("skeleton.orient", "Orient Joint...", ShowOrientDialog, canExecute: JointSelected);
        Actions.Register("skeleton.orientApply", "Orient Joint", OrientSelected, canExecute: JointSelected, repeatable: true);
        Actions.Register("skeleton.mirrorApply", "Mirror Joint (last options)", MirrorSelected, canExecute: JointSelected, repeatable: true);
        Actions.Register("display.jointAxes", "Joint Local Rotation Axes", () =>
        {
            Settings.ShowJointAxes = !Settings.ShowJointAxes; Settings.Save();
            foreach (var p in Layout.Panels) p.Scene.RefreshJoints();
        }, isChecked: () => Settings.ShowJointAxes);
        Actions.Register("display.joints", "Joints", () =>
        {
            Settings.ShowJoints = !Settings.ShowJoints; Settings.Save();
            RefreshJointDisplay();
            HelpLine.Text = Settings.ShowJoints ? "Joints shown." : "Joints hidden (select them in the Outliner; Display → Joints to show).";
        }, isChecked: () => Settings.ShowJoints);
        Actions.Register("display.jointSize", "Joint Size...", ShowJointSizeDialog);
    }

    /// <summary>조인트 표시 설정이 바뀐 뒤 모든 패널의 조인트 뷰와 HUD를 갱신한다.</summary>
    private void RefreshJointDisplay()
    {
        foreach (var p in Layout.Panels) { p.Scene.RefreshJoints(); p.Hud.Refresh(); }
    }

    /// <summary>Joint Size 대화상자(한 번 만들어 재사용; 다시 부르면 앞으로 가져옴).</summary>
    private Window? _jointSizeDialog;

    /// <summary>Display → Joint Size(Maya): 슬라이더/숫자로 모든 조인트 표시 크기 배율을 바꾸면 바로 반영되고 설정에 저장된다.</summary>
    private void ShowJointSizeDialog()
    {
        if (_jointSizeDialog != null && IsInstanceValid(_jointSizeDialog)) { _jointSizeDialog.Show(); _jointSizeDialog.GrabFocus(); return; }
        float s = CubeApp.Instance.UiScale;
        var dlg = new AcceptDialog { Title = "Joint Size", OkButtonText = "Close", Exclusive = false, Unresizable = false, MinSize = new Vector2I((int)(380 * s), (int)(120 * s)) };
        var box = new VBoxContainer();
        box.AddChild(new Label { Text = "Display size of all joints (multiplies each joint's radius; display only)." });
        var row = new HBoxContainer();
        var slider = new HSlider { MinValue = 0.05, MaxValue = 5, Step = 0.01, Value = Settings.JointDisplayScale, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter, CustomMinimumSize = new Vector2(200 * s, 0) };
        var spin = new SpinBox { MinValue = 0.01, MaxValue = 100, Step = 0.01, Value = Settings.JointDisplayScale, AllowGreater = true, CustomMinimumSize = new Vector2(90 * s, 0) };
        var reset = new Button { Text = "Reset", TooltipText = "Back to 1.0" };
        // sync = 슬라이더와 숫자 칸이 서로의 ValueChanged를 다시 부르는 재귀를 막는 플래그
        bool sync = false;
        // 값 적용: 설정에 저장(0.01~100), 슬라이더(최대 5)와 숫자 칸을 신호 없이 맞추고 조인트 표시 즉시 갱신.
        // 파일 저장(Settings.Save)은 닫을 때 한 번만 한다.
        void Apply(double v)
        {
            if (sync) return;
            sync = true;
            Settings.JointDisplayScale = (float)Math.Clamp(v, 0.01, 100);
            slider.SetValueNoSignal(Math.Min(v, slider.MaxValue)); spin.SetValueNoSignal(v);
            RefreshJointDisplay();
            sync = false;
        }
        slider.ValueChanged += Apply; spin.ValueChanged += Apply;
        reset.Pressed += () => Apply(1);
        row.AddChild(slider); row.AddChild(spin); row.AddChild(reset);
        box.AddChild(row);
        dlg.AddChild(box);
        dlg.Confirmed += () => Settings.Save();
        dlg.CloseRequested += () => { Settings.Save(); dlg.Hide(); };
        AddChild(dlg);
        _jointSizeDialog = dlg;
        dlg.PopupCentered();
    }

    /// <summary>Select → All Lights: 씬의 모든 라이트를 오브젝트 선택(Undo 가능).</summary>
    /// <remarks>선택 스냅샷을 직접 찍고(before/after) SelectionCommand를 alreadyApplied로 넣는다.</remarks>
    private void SelectAllLights()
    {
        var sel = Document.Selection;
        var ids = Document.Nodes.Values.Where(n => n.IsLight).Select(n => n.Id).ToList();
        var before = sel.Capture();
        if (sel.Mode != SelectMode.Object) sel.Mode = SelectMode.Object;
        sel.SelectObjects(ids);
        Document.Undo.Push(new SelectionCommand(before, sel.Capture()), alreadyApplied: true);
        HelpLine.Text = $"Selected {ids.Count} light(s).";
    }

    /// <summary>
    /// 라이트 노드를 만든다. 이름은 Maya식(directionalLight1, pointLight1, spotLight1; 중복 시 UniqueName이 번호 증가).
    /// 점광원은 원점, 방향광/스팟은 (0,3,0)에서 X축 -90°로 아래를 향한다. AddNodeCommand로 Undo 가능.
    /// </summary>
    private void CreateLight(LightType type)
    {
        string baseName = type switch { LightType.Directional => "directionalLight", LightType.Spot => "spotLight", _ => "pointLight" };
        var node = new SceneNode { Name = Document.UniqueName(baseName + "1"), Shape = new LightShape { Type = type } };
        // Maya처럼 원점에 만들되 방향광/스팟은 약간 위에서 아래를 보게
        if (type != LightType.Point) node.Local = new Transform3(new NVec3(0, 3, 0), new NVec3(-90, 0, 0), NVec3.One);
        Document.Undo.Push(new AddNodeCommand("Create " + type + " Light", node));
    }

    /// <summary>Material Editor 패널을 지연 생성하고 DockManager에 등록한다(레이아웃 복원 "materialEditor"에서도 호출).</summary>
    private MaterialEditorWindow EnsureMaterialEditor()
    {
        if (MaterialEditor == null)
        {
            MaterialEditor = new MaterialEditorWindow { Name = "MaterialEditor", Visible = false, PanelId = "materialEditor" };
            AddChild(MaterialEditor);
            MaterialEditor.Setup(this);
            MaterialEditor.Closed += RefreshShelf;
            Dock.Register(MaterialEditor);
        }
        return MaterialEditor;
    }

    /// <summary>Material Editor 열기/닫기 토글.</summary>
    private void ToggleMaterialEditor() => EnsureMaterialEditor().Toggle();

    // ---------------------------------------------------------------- Mirror Joint

    /// <summary>Mirror Joint 옵션 대화상자(처음 열 때 생성, 이후 재사용).</summary>
    private ConfirmationDialog? _mirrorDialog;
    /// <summary>대화상자 위젯: 반사 축(평면) 선택, 평면이 지나는 점(월드 원점/부모/선택 조인트).</summary>
    private OptionButton _mirrorAxis = null!, _mirrorPlane = null!;
    /// <summary>대화상자 위젯: 이름 치환 검색어/바꿀 말(예: L_ → R_).</summary>
    private LineEdit _mirrorSearch = null!, _mirrorReplace = null!;

    /// <summary>Mirror Joint 옵션 창을 띄운다. OK(Mirror)를 누르면 MirrorSelected를 실행한다.</summary>
    private void ShowMirrorDialog()
    {
        if (_mirrorDialog == null)
        {
            _mirrorDialog = new ConfirmationDialog { Title = "Mirror Joint Options", OkButtonText = "Mirror" };
            var box = new VBoxContainer();
            _mirrorAxis = new OptionButton(); foreach (var a in new[] { "YZ plane (mirror X)", "XZ plane (mirror Y)", "XY plane (mirror Z)" }) _mirrorAxis.AddItem(a);
            box.AddChild(Labeled("Mirror across", _mirrorAxis));
            _mirrorPlane = new OptionButton(); foreach (var a in new[] { "World origin", "Parent of selected joint", "Selected joint" }) _mirrorPlane.AddItem(a);
            box.AddChild(Labeled("Plane through", _mirrorPlane));
            _mirrorSearch = new LineEdit { Text = "L_" }; _mirrorReplace = new LineEdit { Text = "R_" };
            box.AddChild(Labeled("Search for", _mirrorSearch));
            box.AddChild(Labeled("Replace with", _mirrorReplace));
            _mirrorDialog.AddChild(box);
            _mirrorDialog.Confirmed += MirrorSelected;
            AddChild(_mirrorDialog);
        }
        _mirrorDialog.PopupCentered();
    }

    /// <summary>옵션 대화상자 한 줄: 고정 폭 라벨 + 남는 폭을 채우는 컨트롤.</summary>
    private static Control Labeled(string label, Control c)
    {
        var row = new HBoxContainer();
        row.AddChild(new Label { Text = label, CustomMinimumSize = new Godot.Vector2(140, 0) });
        c.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill; c.CustomMinimumSize = new Godot.Vector2(180, 0);
        row.AddChild(c);
        return row;
    }

    /// <summary>
    /// 마지막 Mirror Joint 옵션(축, 평면 기준 0 원점/1 부모/2 선택 조인트, 이름 검색/치환).
    /// skeleton.mirrorApply는 대화상자 없이 이 값으로 실행한다.
    /// </summary>
    private Axis _mirrorAxisOpt = Axis.X; private int _mirrorPlaneOpt; private string _mirrorSearchOpt = "L_", _mirrorReplaceOpt = "R_";

    /// <summary>
    /// Mirror Joint 실행: 대화상자가 있으면 그 값을 마지막 옵션으로 저장한 뒤, 선택 조인트 중 조상이 함께 선택되지 않은 것(체인 루트)마다
    /// JointOps.Mirror로 반사된 사본 체인을 만들어 원래 부모 아래에 추가한다(월드 반사 후 오른손계 복원, 이름 치환). 한 Undo 그룹.
    /// </summary>
    private void MirrorSelected()
    {
        if (_mirrorDialog != null) { _mirrorAxisOpt = (Axis)_mirrorAxis.Selected; _mirrorPlaneOpt = _mirrorPlane.Selected; _mirrorSearchOpt = _mirrorSearch.Text; _mirrorReplaceOpt = _mirrorReplace.Text; }
        var axis = _mirrorAxisOpt;
        var roots = Document.Selection.Objects.Select(id => Document.Find(id)).Where(n => n != null && n.IsJoint).Cast<SceneNode>().ToList();
        // 선택 안에서 조상이 함께 선택된 조인트는 건너뜀
        var set = new HashSet<SceneNode>(roots);
        roots = roots.Where(r => { for (var p = r.Parent; p != null && !p.IsRoot; p = p.Parent) if (set.Contains(p)) return false; return true; }).ToList();
        if (roots.Count == 0) return;
        using (Document.Undo.BeginGroup("Mirror Joint"))
            foreach (var r in roots)
            {
                // 반사 평면이 지나는 점: 1 = 부모 조인트 위치(부모가 없으면 원점), 2 = 이 조인트 위치, 0 = 월드 원점
                var planePoint = _mirrorPlaneOpt switch
                {
                    1 => r.Parent != null && !r.Parent.IsRoot ? r.Parent.WorldMatrix.Translation : NVec3.Zero,
                    2 => r.WorldMatrix.Translation,
                    _ => NVec3.Zero,
                };
                var mirrored = JointOps.Mirror(r, axis, planePoint, _mirrorSearchOpt, _mirrorReplaceOpt, Document.UniqueName);
                Document.Undo.Push(new AddNodeCommand("Mirror Joint", mirrored, r.Parent != null && !r.Parent.IsRoot ? r.Parent.Id : default));
            }
        HelpLine.Text = $"Mirror Joint: {roots.Count} chain(s) mirrored.";
    }

    // ---------------------------------------------------------------- Orient Joint

    /// <summary>Orient Joint 옵션 대화상자(처음 열 때 생성).</summary>
    private ConfirmationDialog? _orientDialog;
    /// <summary>대화상자 위젯: 주축(자식을 향할 축), 보조축, 보조축이 향할 월드 축.</summary>
    private OptionButton _orientPrimary = null!, _orientSecondary = null!, _orientWorld = null!;
    /// <summary>대화상자 위젯: 보조축 월드 방향 음수 여부, 선택 조인트의 자식까지 정렬할지 여부.</summary>
    private CheckBox _orientNegative = null!, _orientChildren = null!;
    /// <summary>마지막 Orient Joint 옵션. skeleton.orientApply(Orient Now)는 대화상자 없이 이 값으로 실행한다.</summary>
    private readonly OrientOptions _orientOptions = new();

    /// <summary>Orient Joint 옵션 창을 띄운다(기본 주축 X, 보조축 Y → 월드 +Y, 자식 포함). OK면 옵션 저장 후 OrientSelected.</summary>
    private void ShowOrientDialog()
    {
        if (_orientDialog == null)
        {
            _orientDialog = new ConfirmationDialog { Title = "Orient Joint Options", OkButtonText = "Orient" };
            var box = new VBoxContainer();
            _orientPrimary = new OptionButton(); _orientSecondary = new OptionButton(); _orientWorld = new OptionButton();
            foreach (var a in new[] { "X", "Y", "Z" }) { _orientPrimary.AddItem(a); _orientSecondary.AddItem(a); _orientWorld.AddItem(a); }
            _orientPrimary.Selected = 0; _orientSecondary.Selected = 1; _orientWorld.Selected = 1;
            box.AddChild(Labeled("Primary axis", _orientPrimary));
            box.AddChild(Labeled("Secondary axis", _orientSecondary));
            box.AddChild(Labeled("Secondary axis world", _orientWorld));
            _orientNegative = new CheckBox { Text = "Negative world direction" };
            box.AddChild(_orientNegative);
            _orientChildren = new CheckBox { Text = "Orient children of selected joints", ButtonPressed = true };
            box.AddChild(_orientChildren);
            _orientDialog.AddChild(box);
            _orientDialog.Confirmed += () =>
            {
                _orientOptions.Primary = (Axis)_orientPrimary.Selected; _orientOptions.Secondary = (Axis)_orientSecondary.Selected;
                _orientOptions.SecondaryWorld = (Axis)_orientWorld.Selected; _orientOptions.SecondaryWorldNegative = _orientNegative.ButtonPressed;
                _orientOptions.OrientChildren = _orientChildren.ButtonPressed;
                OrientSelected();
            };
            AddChild(_orientDialog);
        }
        _orientDialog.PopupCentered();
    }

    /// <summary>
    /// Orient Joint 실행: JointOps.Orient가 주축이 자식(평균)을 향하도록 조인트 로컬 회전을 바꾸고 자식 월드는 유지한다(이미 문서에 적용됨).
    /// 반환된 변경 목록을 노드별로 합쳐 TransformNodesCommand(alreadyApplied)로 Undo에 넣고 TransformChanged를 알린다.
    /// </summary>
    private void OrientSelected()
    {
        var joints = Document.Selection.Objects.Select(id => Document.Find(id)).Where(n => n != null && n.IsJoint).Cast<SceneNode>().ToList();
        if (joints.Count == 0) return;
        var changes = JointOps.Orient(joints, _orientOptions);
        if (changes.Count == 0) return;
        // 같은 노드가 여러 번 바뀌면 처음 before, 마지막 after
        var first = new Dictionary<NodeId, Transform3>(); var last = new Dictionary<NodeId, Transform3>(); var order = new List<NodeId>();
        foreach (var (n, b, a) in changes) { if (!first.ContainsKey(n.Id)) { first[n.Id] = b; order.Add(n.Id); } last[n.Id] = a; }
        foreach (var id in order) Document.Notify(new DocChange(ChangeKind.TransformChanged, id));
        var cmd = new TransformNodesCommand("Orient Joint", order.ToArray(), order.Select(i => first[i]).ToArray(), order.Select(i => last[i]).ToArray());
        if (!cmd.IsNoop) Document.Undo.Push(cmd, alreadyApplied: true);
        HelpLine.Text = $"Orient Joint: {order.Count} joint(s) oriented (primary {_orientOptions.Primary}, secondary {_orientOptions.Secondary} → world {(_orientOptions.SecondaryWorldNegative ? "-" : "+")}{_orientOptions.SecondaryWorld}).";
    }
}
