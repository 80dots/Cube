using System.Numerics;
using Cube.App.Tools;
using Cube.Core.Commands;
using Cube.Core.Rig;
using Cube.Core.Scene;
using Cube.Core.Selection;

namespace Cube.App.UI;

/// <summary>M3 리깅/스키닝 액션: skeleton.* / skin.*.</summary>
public partial class Shell
{
    /// <summary>가중치 표시(Paint Skin Weights 중): (메시 노드, 정점 → 가중치). null이면 표시 안 함.</summary>
    public (NodeId node, Func<int, float> weight)? WeightDisplay { get; set; }

    private PaintWeightsWindow? _paintWindow;

    public void RefreshAllDisplays() { foreach (var p in Layout.Panels) p.Display.RefreshAll(); }

    public void ShowPaintWeightsWindow(PaintWeightsTool tool)
    {
        if (_paintWindow == null)
        {
            _paintWindow = new PaintWeightsWindow { Name = "PaintWeightsWindow", Visible = false };
            AddChild(_paintWindow);
            _paintWindow.Setup(this, tool);
        }
        _paintWindow.RefreshTarget();
        _paintWindow.Show(this);
    }

    public void HidePaintWeightsWindow() { if (_paintWindow != null && _paintWindow.Visible) _paintWindow.Visible = false; }

    private void RegisterRigActions()
    {
        var doc = Document; var sel = doc.Selection;
        bool ObjMode() => sel.Mode == SelectMode.Object;
        IEnumerable<SceneNode> SelectedNodes() => sel.Objects.Select(id => doc.Find(id)).Where(n => n != null)!;

        Actions.Register("skeleton.jointTool", "Joint Tool", () => Tools.SetTool("joint"), isChecked: () => Tools.Current?.Id == "joint");
        Actions.Register("skin.bind", "Bind Skin", BindSkin,
            canExecute: () => ObjMode() && SelectedNodes().Any(n => n.Mesh != null) && SelectedNodes().Any(n => n.IsJoint));
        Actions.Register("skin.detach", "Detach Skin", DetachSkin, canExecute: () => ObjMode() && SelectedNodes().Any(n => n.Skin != null));
        Actions.Register("skin.paintTool", "Paint Skin Weights Tool", () => Tools.SetTool("paintWeights"),
            canExecute: () => doc.SkinnedNodes().Any(), isChecked: () => Tools.Current?.Id == "paintWeights");
        Actions.Register("skin.normalize", "Normalize Weights", NormalizeWeights, canExecute: () => doc.SkinnedNodes().Any(), repeatable: true);
        Actions.Register("skin.rebind", "Reset Weights (Smooth Bind)", RebindSkin, canExecute: () => ObjMode() && SelectedNodes().Any(n => n.Skin != null), repeatable: true);
    }

    /// <summary>선택된 조인트와 그 하위 조인트 전체(문서 순서, 중복 없음).</summary>
    private List<SceneNode> SelectedJointHierarchy()
    {
        var list = new List<SceneNode>();
        var seen = new HashSet<NodeId>();
        void Add(SceneNode n) { if (!n.IsJoint || !seen.Add(n.Id)) return; list.Add(n); foreach (var c in n.Children) Add(c); }
        foreach (var id in Document.Selection.Objects) { var n = Document.Find(id); if (n != null && n.IsJoint) Add(n); }
        return list;
    }

    private static List<JointInfo> JointInfos(IEnumerable<SceneNode> joints)
        => joints.Select(j => new JointInfo(j.Id, j.WorldMatrix, j.Children.Where(c => c.IsJoint).Select(c => c.WorldMatrix.Translation).ToList())).ToList();

    private void BindSkin()
    {
        var joints = SelectedJointHierarchy();
        var meshes = Document.Selection.Objects.Select(id => Document.Find(id)).Where(n => n?.Mesh != null).Cast<SceneNode>().ToList();
        if (joints.Count == 0 || meshes.Count == 0) { HelpLine.Text = "Bind Skin: select a mesh and a joint (hierarchy)."; return; }
        var infos = JointInfos(joints);
        using (Document.Undo.BeginGroup("Bind Skin"))
            foreach (var m in meshes)
            {
                var skin = SkinOps.SmoothBind(m.Mesh!, m.WorldMatrix, infos);
                Document.Undo.Push(new SetSkinCommand("Bind Skin", m.Id, skin));
            }
        HelpLine.Text = $"Bind Skin: {meshes.Count} mesh(es) bound to {joints.Count} joint(s).";
    }

    private void RebindSkin()
    {
        var meshes = Document.Selection.Objects.Select(id => Document.Find(id)).Where(n => n?.Skin != null).Cast<SceneNode>().ToList();
        using (Document.Undo.BeginGroup("Reset Weights"))
            foreach (var m in meshes)
            {
                var joints = m.Skin!.Joints.Select(id => Document.Find(id)).Where(j => j != null && j.IsJoint).Cast<SceneNode>().ToList();
                if (joints.Count == 0) continue;
                Document.Undo.Push(new SetSkinCommand("Reset Weights", m.Id, SkinOps.SmoothBind(m.Mesh!, m.WorldMatrix, JointInfos(joints))));
            }
    }

    private void DetachSkin()
    {
        var meshes = Document.Selection.Objects.Select(id => Document.Find(id)).Where(n => n?.Skin != null).Cast<SceneNode>().ToList();
        using (Document.Undo.BeginGroup("Detach Skin"))
            foreach (var m in meshes) Document.Undo.Push(new SetSkinCommand("Detach Skin", m.Id, null));
        if (Tools.Current?.Id == "paintWeights") Tools.SetTool("select");
    }

    private void NormalizeWeights()
    {
        var targets = Document.Selection.Objects.Select(id => Document.Find(id)).Where(n => n?.Skin != null).Cast<SceneNode>().ToList();
        if (targets.Count == 0) targets = Document.SkinnedNodes().ToList();
        using (Document.Undo.BeginGroup("Normalize Weights"))
            foreach (var n in targets)
            {
                var m = n.Mesh!; var skin = n.Skin!;
                var verts = Enumerable.Range(0, m.VertexCount).Where(v => m.Verts[v].Alive).ToArray();
                var before = verts.Select(v => skin.CopyWeights(v)).ToArray();
                SkinOps.NormalizeAll(m, skin);
                var after = verts.Select(v => skin.CopyWeights(v)).ToArray();
                var cmd = new WeightPaintCommand(n.Id, verts, before, after);
                if (!cmd.IsNoop) Document.Undo.Push(cmd, alreadyApplied: true);
                Document.Notify(new DocChange(ChangeKind.SkinChanged, n.Id));
            }
    }
}
