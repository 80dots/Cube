using System.Numerics;
using Cube.Core.Commands;
using Cube.Core.Mesh;
using Cube.Core.Scene;
using Cube.Core.Selection;
using Cube.Core.Uv;

namespace Cube.App.UI;

/// <summary>UV 편집기 액션(uv.*). 대상은 현재 선택: 면 모드면 선택 면, 아니면 선택 오브젝트 전체.</summary>
public partial class Shell
{
    public UvEditor.UvEditorWindow? UvEditorWindow { get; private set; }

    private void RegisterUvActions()
    {
        var doc = Document; var sel = doc.Selection;
        bool HasTargets() => UvTargetNodes().Any();

        Actions.Register("windows.uvEditor", "UV Editor", ToggleUvEditor, isChecked: () => UvEditorWindow?.Visible ?? false);
        Actions.Register("uv.planarBest", "Planar Mapping (Best Plane)", () => Project("Planar", (m, f) => UvOps.PlanarProjectBestFit(m, f)), canExecute: HasTargets, repeatable: true);
        Actions.Register("uv.planarX", "Planar Mapping (X)", () => Project("Planar X", (m, f) => UvOps.PlanarProject(m, f, Vector3.UnitX)), canExecute: HasTargets, repeatable: true);
        Actions.Register("uv.planarY", "Planar Mapping (Y)", () => Project("Planar Y", (m, f) => UvOps.PlanarProject(m, f, Vector3.UnitY)), canExecute: HasTargets, repeatable: true);
        Actions.Register("uv.planarZ", "Planar Mapping (Z)", () => Project("Planar Z", (m, f) => UvOps.PlanarProject(m, f, Vector3.UnitZ)), canExecute: HasTargets, repeatable: true);
        Actions.Register("uv.cylindrical", "Cylindrical Mapping", () => Project("Cylindrical", UvOps.CylindricalProject), canExecute: HasTargets, repeatable: true);
        Actions.Register("uv.spherical", "Spherical Mapping", () => Project("Spherical", UvOps.SphericalProject), canExecute: HasTargets, repeatable: true);
        Actions.Register("uv.unfold", "Unfold", UvUnfold, canExecute: HasTargets, repeatable: true);
        Actions.Register("uv.layout", "Layout", UvLayout, canExecute: HasTargets, repeatable: true);
        Actions.Register("uv.cut", "Cut UV Edges", () => CutSew(true), canExecute: () => sel.IsComponentMode && sel.NodesWithComponents(sel.Mode).Any(), repeatable: true);
        Actions.Register("uv.sew", "Sew UV Edges", () => CutSew(false), canExecute: () => sel.IsComponentMode && sel.NodesWithComponents(sel.Mode).Any(), repeatable: true);
        Actions.Register("uv.frameSelected", "Frame Selected (UV)", () => UvEditorWindow?.Canvas.FrameSelected(), canExecute: () => UvEditorWindow?.Visible ?? false);
        Actions.Register("uv.frameAll", "Frame All (UV)", () => UvEditorWindow?.Canvas.FrameAll(), canExecute: () => UvEditorWindow?.Visible ?? false);
        Actions.Register("uv.cycleBackground", "Cycle Background (UV)", () => UvEditorWindow?.CycleBackground(), canExecute: () => UvEditorWindow?.Visible ?? false);
        Actions.Register("uv.flipU", "Flip U", () => Flip(true), canExecute: HasTargets, repeatable: true);
        Actions.Register("uv.flipV", "Flip V", () => Flip(false), canExecute: HasTargets, repeatable: true);
        Actions.Register("uv.autoSeams", "Auto Seam Select", AutoSeamSelect, canExecute: () => sel.Objects.Any(id => doc.Find(id)?.Mesh != null), repeatable: true);
        Actions.Register("uv.autoWrap", "Auto Wrap", AutoWrap, canExecute: () => sel.Objects.Any(id => doc.Find(id)?.Mesh != null), repeatable: true);
    }

    private void ToggleUvEditor()
    {
        if (UvEditorWindow == null)
        {
            UvEditorWindow = new UvEditor.UvEditorWindow { Name = "UvEditor", Visible = false };
            AddChild(UvEditorWindow);
            UvEditorWindow.Setup(this);
            UvEditorWindow.Closed += RefreshShelf;
        }
        UvEditorWindow.Toggle();
    }

    /// <summary>UV 작업 대상 노드와 면 집합: 면 모드면 선택 면, 그 외(오브젝트/UV/엣지)는 관련 노드의 전체 면.</summary>
    private IEnumerable<(SceneNode node, List<int> faces)> UvTargetNodes()
    {
        var sel = Document.Selection;
        var ids = new HashSet<NodeId>(sel.Objects);
        foreach (var (id, c) in sel.Components) if (!c.IsEmpty) ids.Add(id);
        foreach (var id in ids)
        {
            var n = Document.Find(id); if (n?.Mesh == null) continue;
            var faces = TargetFaces(n, sel);
            if (faces.Count > 0) yield return (n, faces);
        }
    }

    /// <summary>
    /// 선택에 한정한 대상 면: 면 모드 = 선택 면, 엣지/정점/UV(Island) 모드 = 선택 요소의 UV 점을 모두 포함하는 면
    /// (하나도 없으면 선택 UV를 하나라도 포함하는 면), 오브젝트 모드 = 전체.
    /// </summary>
    private List<int> TargetFaces(SceneNode n, SelectionState sel)
    {
        var m = n.Mesh!;
        List<int> All() => Enumerable.Range(0, m.FaceCount).Where(f => m.Faces[f].Alive).ToList();
        if (sel.Mode == SelectMode.Object || !sel.Components.TryGetValue(n.Id, out var comps)) return All();
        if (sel.Mode == SelectMode.Face) return comps.Faces.Count > 0 ? comps.Faces.ToList() : All();
        var topo = UvTopology.Build(m);
        var selPts = new HashSet<int>();
        switch (sel.Mode)
        {
            case SelectMode.Uv: selPts.UnionWith(comps.Uvs.Where(p => p < topo.Points.Count)); break;
            case SelectMode.Edge:
                foreach (int e in comps.Edges) { if (e >= m.EdgeCount || !m.Edges[e].Alive) continue; var ed = m.Edges[e]; foreach (int he in new[] { ed.He0, ed.He1 }) { if (he < 0) continue; selPts.Add(topo.HeToPoint[he]); selPts.Add(topo.HeToPoint[m.Hes[he].Next]); } }
                break;
            case SelectMode.Vertex:
                for (int p = 0; p < topo.Points.Count; p++) if (comps.Verts.Contains(topo.Points[p].Vertex)) selPts.Add(p);
                break;
        }
        if (selPts.Count == 0) return All();
        var full = new List<int>(); var partial = new List<int>();
        var hes = new List<int>();
        for (int f = 0; f < m.FaceCount; f++)
        {
            if (!m.Faces[f].Alive) continue;
            m.GetFaceHalfEdges(f, hes);
            int hit = 0; foreach (int he in hes) if (selPts.Contains(topo.HeToPoint[he])) hit++;
            if (hit == hes.Count) full.Add(f); else if (hit > 0) partial.Add(f);
        }
        return full.Count > 0 ? full : partial;
    }

    /// <summary>Auto Seam Select: 선택 오브젝트의 최적 심 엣지를 찾아 엣지 모드로 선택한다(심 적용은 Cut으로).</summary>
    private void AutoSeamSelect()
    {
        var targets = Document.Selection.Objects.Select(id => Document.Find(id)).Where(n => n?.Mesh != null).Cast<SceneNode>().ToList();
        if (targets.Count == 0) return;
        int total = 0;
        RecordSelection(s =>
        {
            s.Mode = SelectMode.Edge;
            bool first = true;
            foreach (var n in targets)
            {
                var seams = AutoSeams.Select(n.Mesh!);
                total += seams.Count;
                s.SelectComponents(n.Id, SelectMode.Edge, seams, replace: first); first = false;
            }
        });
        HelpLine.Text = $"Auto Seam Select: {total} edge(s) selected. Use Cut UV Edges to apply, or Auto Wrap to cut, unfold and layout.";
    }

    /// <summary>Auto Wrap: 자동 심 → Cut → 섬별 투영 → Unfold → Layout.</summary>
    private void AutoWrap()
    {
        var targets = Document.Selection.Objects.Select(id => Document.Find(id)).Where(n => n?.Mesh != null).Cast<SceneNode>().ToList();
        if (targets.Count == 0) return;
        int seams = 0;
        using (Document.Undo.BeginGroup("Auto Wrap"))
            foreach (var n in targets)
                Document.Undo.Push(new UvEditCommand("Auto Wrap", n.Id, m => seams += UvOps.AutoWrap(m)));
        UvEditorWindow?.Canvas.Invalidate();
        HelpLine.Text = $"Auto Wrap: {seams} seam edge(s), islands unfolded and laid out.";
    }

    private void Project(string name, Action<PolyMesh, IEnumerable<int>> op)
    {
        var targets = UvTargetNodes().ToList();
        if (targets.Count == 0) return;
        using (Document.Undo.BeginGroup(name + " Mapping"))
            foreach (var (node, faces) in targets)
                Document.Undo.Push(new UvEditCommand(name, node.Id, m => op(m, faces)));
        UvEditorWindow?.Canvas.Invalidate();
    }

    private IEnumerable<int> ShellsForNode(SceneNode node, UvTopology topo, List<int> faces)
    {
        var sel = Document.Selection;
        var m = node.Mesh!;
        var shells = new HashSet<int>();
        if (sel.Mode == SelectMode.Uv && sel.Components.TryGetValue(node.Id, out var comps) && comps.Uvs.Count > 0)
        {
            foreach (int p in comps.Uvs) if (p < topo.Points.Count) shells.Add(topo.Points[p].Shell);
        }
        else
        {
            foreach (int f in faces)
            {
                int start = m.Faces[f].HalfEdge, he = start;
                do { shells.Add(topo.Points[topo.HeToPoint[he]].Shell); he = m.Hes[he].Next; } while (he != start);
            }
        }
        return shells;
    }

    private void UvUnfold()
    {
        var targets = UvTargetNodes().ToList();
        using (Document.Undo.BeginGroup("Unfold"))
            foreach (var (node, faces) in targets)
            {
                Document.Undo.Push(new UvEditCommand("Unfold", node.Id, m =>
                {
                    var topo = UvTopology.Build(m);
                    UvOps.UnfoldRelax(m, topo, ShellsForNode(node, topo, faces));
                }));
            }
        UvEditorWindow?.Canvas.Invalidate();
    }

    private void UvLayout()
    {
        var targets = UvTargetNodes().ToList();
        using (Document.Undo.BeginGroup("Layout"))
            foreach (var (node, faces) in targets)
            {
                Document.Undo.Push(new UvEditCommand("Layout", node.Id, m =>
                {
                    var topo = UvTopology.Build(m);
                    UvOps.Layout(m, topo, ShellsForNode(node, topo, faces));
                }));
            }
        UvEditorWindow?.Canvas.Invalidate();
    }

    /// <summary>현재 모드의 선택을 Cut/Sew 대상 엣지로: 엣지 그대로, 면은 바깥 경계 엣지, 정점/UV는 양 끝이 선택된 엣지(없으면 닿는 엣지).</summary>
    private int[] UvCutTargetEdges(PolyMesh mesh, ComponentSet comps, SelectMode mode)
    {
        switch (mode)
        {
            case SelectMode.Edge: return comps.Edges.ToArray();
            case SelectMode.Face: return SelectionOps.BoundaryEdgesOfFaces(mesh, comps.Faces).ToArray();
            case SelectMode.Vertex: return SelectionOps.EdgesOfVertices(mesh, comps.Verts).ToArray();
            case SelectMode.Uv:
                {
                    var topo = UvTopology.Build(mesh);
                    var verts = comps.Uvs.Where(p => p < topo.Points.Count).Select(p => topo.Points[p].Vertex);
                    return SelectionOps.EdgesOfVertices(mesh, verts).ToArray();
                }
            default: return Array.Empty<int>();
        }
    }

    private void CutSew(bool cut)
    {
        var sel = Document.Selection;
        var mode = sel.Mode;
        using (Document.Undo.BeginGroup(cut ? "Cut UV Edges" : "Sew UV Edges"))
            foreach (var id in sel.NodesWithComponents(mode).ToArray())
            {
                var mesh = Document.Find(id)?.Mesh; if (mesh == null) continue;
                var edges = UvCutTargetEdges(mesh, sel.GetComponents(id), mode);
                if (edges.Length == 0) continue;
                Document.Undo.Push(new UvEditCommand(cut ? "Cut UV Edges" : "Sew UV Edges", id, m => { if (cut) UvOps.CutEdges(m, edges); else UvOps.SewEdges(m, edges); }));
            }
        UvEditorWindow?.Canvas.Invalidate();
    }

    private void Flip(bool flipU)
    {
        var targets = UvTargetNodes().ToList();
        using (Document.Undo.BeginGroup(flipU ? "Flip U" : "Flip V"))
            foreach (var (node, faces) in targets)
            {
                Document.Undo.Push(new UvEditCommand(flipU ? "Flip U" : "Flip V", node.Id, m =>
                {
                    var topo = UvTopology.Build(m);
                    IEnumerable<int> pts;
                    var sel = Document.Selection;
                    if (sel.Mode == SelectMode.Uv && sel.Components.TryGetValue(node.Id, out var comps) && comps.Uvs.Count > 0) pts = comps.Uvs.Where(p => p < topo.Points.Count);
                    else
                    {
                        var set = new HashSet<int>();
                        foreach (int f in faces) { int start = m.Faces[f].HalfEdge, he = start; do { set.Add(topo.HeToPoint[he]); he = m.Hes[he].Next; } while (he != start); }
                        pts = set;
                    }
                    UvOps.Flip(m, topo, pts, flipU);
                }));
            }
        UvEditorWindow?.Canvas.Invalidate();
    }
}
