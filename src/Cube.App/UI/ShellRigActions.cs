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
    /// <remarks>
    /// PaintWeightsTool이 활성 영향(조인트)을 바꿀 때 설정하고, ViewportDisplay가 이 값을 읽어 표면을
    /// 흑백 가중치 램프(surface.gdshader의 use_vertex_color)로 그린다. 바꾼 뒤에는 RefreshAllDisplays를 불러야 한다.
    /// </remarks>
    public (NodeId node, Func<int, float> weight)? WeightDisplay { get; set; }

    /// <summary>Paint Skin Weights 패널(영향 목록/모드/값/반지름/Flood/Normalize). 툴이 처음 켜질 때 만든다.</summary>
    private PaintWeightsWindow? _paintWindow;

    /// <summary>모든 뷰포트 패널의 표시(표면 스타일·컴포넌트 오버레이)를 다시 만든다.</summary>
    public void RefreshAllDisplays() { foreach (var p in Layout.Panels) p.Display.RefreshAll(); }

    /// <summary>
    /// Paint Skin Weights 패널을 연다(없으면 만들고 툴과 연결해 DockManager에 등록). 열 때마다 대상 메시/영향 목록을 갱신한다.
    /// 이 패널은 툴에 묶여 있어 레이아웃 복원 대상이 아니다(EnsurePanel 참고).
    /// </summary>
    public void ShowPaintWeightsWindow(PaintWeightsTool tool)
    {
        if (_paintWindow == null)
        {
            _paintWindow = new PaintWeightsWindow { Name = "PaintWeightsWindow", Visible = false };
            AddChild(_paintWindow);
            _paintWindow.Setup(this, tool);
            Dock.Register(_paintWindow);
        }
        _paintWindow.RefreshTarget();
        _paintWindow.Show(this);
    }

    /// <summary>툴을 끝내면 떠 있는 가중치 창은 숨긴다(도크에 붙어 있으면 그대로 둔다).</summary>
    public void HidePaintWeightsWindow() { if (_paintWindow != null && !_paintWindow.Docked && _paintWindow.Visible) _paintWindow.Visible = false; }

    /// <summary>
    /// 리깅 액션 등록: Joint Tool, Bind Skin(메시 + 조인트 선택 필요), Detach Skin, Paint Skin Weights Tool,
    /// Normalize Weights, Reset Weights(같은 조인트로 Smooth Bind 다시). 바인드 관련은 오브젝트 모드에서만 가능.
    /// </summary>
    private void RegisterRigActions()
    {
        var doc = Document; var sel = doc.Selection;
        // 헬퍼: 오브젝트 모드 여부, 선택 노드 열거(삭제된 ID는 제외)
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
        // 깊이 우선으로 조인트만 따라 내려가며 추가(조인트가 아닌 자식에서는 멈춤, 이미 본 노드는 건너뜀)
        void Add(SceneNode n) { if (!n.IsJoint || !seen.Add(n.Id)) return; list.Add(n); foreach (var c in n.Children) Add(c); }
        foreach (var id in Document.Selection.Objects) { var n = Document.Find(id); if (n != null && n.IsJoint) Add(n); }
        return list;
    }

    /// <summary>
    /// SmoothBind 입력용 조인트 정보: (ID, 현재 월드 행렬, 자식 조인트들의 월드 위치). 자식 위치로 본 선분을 만들어
    /// 정점–본 거리(1/d² 가중)를 계산한다.
    /// </summary>
    private static List<JointInfo> JointInfos(IEnumerable<SceneNode> joints)
        => joints.Select(j => new JointInfo(j.Id, j.WorldMatrix, j.Children.Where(c => c.IsJoint).Select(c => c.WorldMatrix.Translation).ToList())).ToList();

    /// <summary>
    /// Bind Skin: 선택된 조인트 계층 전체에 선택된 메시들을 스무스 바인드한다(SkinOps.SmoothBind, 정점당 상위 4개 가중치 정규화).
    /// 메시마다 SetSkinCommand를 한 Undo 그룹에 넣는다. 바인드 포즈 = 현재 조인트/메시 월드 행렬.
    /// </summary>
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

    /// <summary>
    /// Reset Weights: 스킨이 있는 선택 메시마다 그 스킨의 조인트 목록(아직 존재하는 조인트만)으로 Smooth Bind를 다시 계산해 가중치를 초기화한다.
    /// </summary>
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

    /// <summary>Detach Skin: 선택 메시의 스킨을 제거(SetSkinCommand(null))하고, 가중치 페인트 중이면 Select 툴로 돌아간다.</summary>
    private void DetachSkin()
    {
        var meshes = Document.Selection.Objects.Select(id => Document.Find(id)).Where(n => n?.Skin != null).Cast<SceneNode>().ToList();
        using (Document.Undo.BeginGroup("Detach Skin"))
            foreach (var m in meshes) Document.Undo.Push(new SetSkinCommand("Detach Skin", m.Id, null));
        if (Tools.Current?.Id == "paintWeights") Tools.SetTool("select");
    }

    /// <summary>
    /// Normalize Weights: 선택된 스킨 메시(없으면 문서의 모든 스킨 메시)의 정점 가중치 합을 1로 맞춘다.
    /// 정규화를 직접 적용한 뒤 전후 가중치 스냅샷으로 WeightPaintCommand를 만들어 alreadyApplied로 넣고,
    /// SkinChanged를 알려 변형 표시를 갱신한다(변화가 없으면 Undo에 넣지 않음).
    /// </summary>
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
