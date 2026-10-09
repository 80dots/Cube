using Cube.Core.Scene;
using Cube.Core.Selection;

namespace Cube.App.UI;

/// <summary>
/// Select → Non-Manifold(v0.0.58, Blender Select All by Trait → Non Manifold / Maya Cleanup의 Non-manifold geometry 선택에 해당).
/// 경계 엣지·나비넥타이(꼬집힌) 정점·고립 정점을 찾아 정점 또는 엣지 모드로 선택한다. 옵션 쌍이라 Action Popup에서 종류를 바꿀 수 있다.
/// 컴포넌트 모드는 개체 하나만 다루므로 대상은 편집 중인 개체 → 활성 메시 → (선택이 없으면) 비매니폴드 요소가 있는 첫 메시 순.
/// </summary>
public partial class Shell
{
    /// <summary>옵션 창 정의: 선택 형태(정점/엣지)와 찾을 종류 세 가지(기본 모두 켬).</summary>
    private static OptionSpec NonManifoldSpec() => new("Select Non-Manifold Options", v =>
    {
        v.Set("selectAs", 0); v.Set("boundaries", 1); v.Set("bowtie", 1); v.Set("isolated", 1);
    }, new[]
    {
        OptionField.E("selectAs", "Select As", "Vertices", "Edges"),
        OptionField.B("boundaries", "Boundaries", "Open (border) edges: edges with a face on one side only"),
        OptionField.B("bowtie", "Bowtie Vertices", "Pinched vertices where separate face fans meet at a single point"),
        OptionField.B("isolated", "Isolated Vertices", "Vertices that belong to no face (vertex mode only)"),
    }, "Select");

    /// <summary>
    /// select.nonManifoldApply 본체: 대상 메시를 정한 뒤 <see cref="NonManifold.Find"/> 결과를 정점/엣지 모드로 교체 선택한다(한 Undo 단계).
    /// 헬프 라인에 종류별 개수를 쓰고, 다른 선택 오브젝트에도 비매니폴드 요소가 있으면 함께 알린다.
    /// </summary>
    private void SelectNonManifold()
    {
        var doc = Document; var sel = doc.Selection;
        var ov = Options("select.nonManifold");
        var o = new NonManifoldOptions { Boundaries = ov.Bool("boundaries", true), Bowtie = ov.Bool("bowtie", true), Isolated = ov.Bool("isolated", true) };
        bool asEdges = ov.Int("selectAs") == 1;
        // 후보: 편집 중인 개체 → 선택한 메시(활성 먼저) → 문서의 모든 메시
        var candidates = new List<NodeId>();
        if (sel.IsComponentMode && doc.Find(sel.ComponentTarget)?.Mesh != null) candidates.Add(sel.ComponentTarget);
        foreach (var id in sel.Objects.Reverse()) if (doc.Find(id)?.Mesh != null && !candidates.Contains(id)) candidates.Add(id);
        bool fromSelection = candidates.Count > 0;
        if (!fromSelection) candidates.AddRange(doc.Nodes.Values.Where(n => n.Mesh != null && n.Visible).Select(n => n.Id));
        if (candidates.Count == 0) { HelpLine.Text = "Non-Manifold: no mesh in the scene."; return; }

        NodeId target = NodeId.None; HashSet<int>? verts = null, edges = null;
        var others = new List<string>();
        foreach (var id in candidates)
        {
            var (v, e) = NonManifold.Find(doc.Get(id).Mesh!, o);
            if (v.Count == 0 && e.Count == 0) continue;
            if (target.IsNone) { target = id; verts = v; edges = e; }
            else others.Add(doc.Get(id).Name);
            if (!fromSelection) break; // 선택이 없으면 첫 메시만
        }
        if (target.IsNone)
        {
            HelpLine.Text = $"Non-Manifold: none found in {(fromSelection ? "the selected mesh(es)" : "the scene")} — all meshes are closed and manifold for the checked types.";
            return;
        }
        var mesh = doc.Get(target).Mesh!;
        int isolated = verts!.Count(v => mesh.VertexOutgoing(v).Length == 0);
        int boundary = edges!.Count(e => mesh.Edges[e].He1 < 0);
        int bowtie = verts.Count(v => mesh.VertexOutgoing(v).Length > 0 && NonManifold.IsBowtie(mesh, v, mesh.VertexOutgoing(v)));
        var mode = asEdges ? SelectMode.Edge : SelectMode.Vertex;
        var ids = asEdges ? edges : verts;
        RecordSelection(s =>
        {
            s.Mode = SelectMode.Object; s.SelectObjects(new[] { target });
            s.Mode = mode;
            s.SelectComponents(target, mode, ids, replace: true);
        });
        string what = $"{boundary} boundary edge(s), {bowtie} bowtie vertex(es), {isolated} isolated vertex(es)";
        HelpLine.Text = $"Non-Manifold on {doc.Get(target).Name}: {what}."
            + (asEdges && isolated > 0 ? " Isolated vertices have no edges — use Select As Vertices to see them." : "")
            + (others.Count > 0 ? $" Also found in: {string.Join(", ", others)}." : "");
    }

    /// <summary>select.nonManifold(옵션 창)/select.nonManifoldApply(실행) 옵션 쌍. 메시가 하나라도 있으면 실행 가능.</summary>
    private void RegisterNonManifoldActions()
        => RegisterOptionPair("select.nonManifold", "Non-Manifold", NonManifoldSpec(), SelectNonManifold, () => Document.Nodes.Values.Any(n => n.Mesh != null));
}
