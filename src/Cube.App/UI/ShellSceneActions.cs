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
    public MaterialEditorWindow? MaterialEditor { get; private set; }

    private void RegisterSceneActions()
    {
        var doc = Document; var sel = doc.Selection;
        Actions.Register("create.polygonTool", "Create Polygon Tool", () => Tools.SetTool("createPolygon"), isChecked: () => Tools.Current?.Id == "createPolygon");
        Actions.Register("create.lightDirectional", "Directional Light", () => CreateLight(LightType.Directional), repeatable: true);
        Actions.Register("create.lightPoint", "Point Light", () => CreateLight(LightType.Point), repeatable: true);
        Actions.Register("create.lightSpot", "Spot Light", () => CreateLight(LightType.Spot), repeatable: true);
        Actions.Register("windows.materialEditor", "Material Editor", ToggleMaterialEditor, isChecked: () => MaterialEditor?.IsOpen ?? false);

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
    }

    private void CreateLight(LightType type)
    {
        string baseName = type switch { LightType.Directional => "directionalLight", LightType.Spot => "spotLight", _ => "pointLight" };
        var node = new SceneNode { Name = Document.UniqueName(baseName + "1"), Shape = new LightShape { Type = type } };
        // Maya처럼 원점에 만들되 방향광/스팟은 약간 위에서 아래를 보게
        if (type != LightType.Point) node.Local = new Transform3(new NVec3(0, 3, 0), new NVec3(-90, 0, 0), NVec3.One);
        Document.Undo.Push(new AddNodeCommand("Create " + type + " Light", node));
    }

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

    private void ToggleMaterialEditor() => EnsureMaterialEditor().Toggle();

    // ---------------------------------------------------------------- Mirror Joint

    private ConfirmationDialog? _mirrorDialog;
    private OptionButton _mirrorAxis = null!, _mirrorPlane = null!;
    private LineEdit _mirrorSearch = null!, _mirrorReplace = null!;

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

    private static Control Labeled(string label, Control c)
    {
        var row = new HBoxContainer();
        row.AddChild(new Label { Text = label, CustomMinimumSize = new Godot.Vector2(140, 0) });
        c.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill; c.CustomMinimumSize = new Godot.Vector2(180, 0);
        row.AddChild(c);
        return row;
    }

    private Axis _mirrorAxisOpt = Axis.X; private int _mirrorPlaneOpt; private string _mirrorSearchOpt = "L_", _mirrorReplaceOpt = "R_";

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

    private ConfirmationDialog? _orientDialog;
    private OptionButton _orientPrimary = null!, _orientSecondary = null!, _orientWorld = null!;
    private CheckBox _orientNegative = null!, _orientChildren = null!;
    private readonly OrientOptions _orientOptions = new();

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
