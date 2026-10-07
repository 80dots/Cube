using Cube.Core.Commands;
using Cube.Core.Mesh;
using Cube.Core.Scene;
using Cube.Core.Selection;
using Godot;
using NVec3 = System.Numerics.Vector3;

namespace Cube.App.UI;

/// <summary>
/// Maya Mesh / Edit Mesh / Mesh Tools / Mesh Display 메뉴의 모델링 액션(v0.0.8에서 추가된 것들).
/// 옵션이 있는 명령은 "…"(옵션 창)과 즉시 실행(마지막 옵션) 두 액션을 가진다. 모든 편집은 MeshOpCommand(Undo + 구성 이력).
/// </summary>
public partial class Shell
{
    private readonly Dictionary<string, OptionValues> _options = new();
    private OptionValues Opt(string id) { if (!_options.TryGetValue(id, out var v)) { v = new OptionValues(); _options[id] = v; } return v; }

    /// <summary>옵션 창을 띄운다. 기본값은 values에 없을 때만 적용.</summary>
    private void ShowOptions(string id, string title, string okText, Action<OptionValues> defaults, IReadOnlyList<OptionField> fields, Action apply)
    {
        var v = Opt(id);
        if (!v.Has("_init")) { defaults(v); v.Set("_init", 1f); }
        var dlg = new OptionsDialog(title, okText, fields, v, apply);
        AddChild(dlg);
        dlg.Confirmed += dlg.QueueFree; dlg.Canceled += dlg.QueueFree;
        dlg.PopupCentered();
    }

    private OptionValues OptWithDefaults(string id, Action<OptionValues> defaults)
    {
        var v = Opt(id);
        if (!v.Has("_init")) { defaults(v); v.Set("_init", 1f); }
        return v;
    }

    private bool HasMeshSelection() { var s = Document.Selection; return s.Mode == SelectMode.Object ? s.Objects.Any(id => Document.Find(id)?.Mesh != null) : s.NodesWithComponents(s.Mode).Any(); }
    private bool HasComponents(params SelectMode[] modes) { var s = Document.Selection; return modes.Contains(s.Mode) && s.NodesWithComponents(s.Mode).Any(); }

    /// <summary>현재 선택을 wanted 모드의 컴포넌트 집합으로 바꿔 노드마다 명령을 만든다(오브젝트 모드 = 메시 전체). 한 Undo 스텝.</summary>
    private void ForEachMeshTarget(string groupName, SelectMode wanted, Func<NodeId, HashSet<int>, ICommand?> make)
    {
        var doc = Document; var sel = doc.Selection;
        var targets = new List<(NodeId id, HashSet<int> ids)>();
        if (sel.Mode == SelectMode.Object)
        {
            foreach (var id in sel.Objects)
            {
                var mesh = doc.Find(id)?.Mesh; if (mesh == null) continue;
                var all = wanted switch
                {
                    SelectMode.Vertex => Enumerable.Range(0, mesh.VertexCount).Where(v => mesh.Verts[v].Alive),
                    SelectMode.Edge => Enumerable.Range(0, mesh.EdgeCount).Where(e => mesh.Edges[e].Alive),
                    _ => Enumerable.Range(0, mesh.FaceCount).Where(f => mesh.Faces[f].Alive),
                };
                targets.Add((id, new HashSet<int>(all)));
            }
        }
        else
        {
            foreach (var id in sel.NodesWithComponents(sel.Mode))
            {
                var mesh = doc.Find(id)?.Mesh; if (mesh == null) continue;
                var comps = sel.GetComponents(id);
                var set = sel.Mode == wanted ? new HashSet<int>(comps.Get(wanted)) : SelectionOps.Convert(mesh, comps, sel.Mode == SelectMode.Uv ? SelectMode.Vertex : sel.Mode, wanted);
                if (sel.Mode == SelectMode.Uv)
                {
                    var topo = Core.Uv.UvTopology.Build(mesh);
                    var c = new ComponentSet(); foreach (int p in comps.Uvs) if (p < topo.Points.Count) c.Verts.Add(topo.Points[p].Vertex);
                    set = wanted == SelectMode.Vertex ? c.Verts : SelectionOps.Convert(mesh, c, SelectMode.Vertex, wanted);
                }
                if (set.Count > 0) targets.Add((id, set));
            }
        }
        if (targets.Count == 0) { HelpLine.Text = $"{groupName}: nothing selected."; return; }
        using (doc.Undo.BeginGroup(groupName))
            foreach (var (id, ids) in targets) { var cmd = make(id, ids); if (cmd != null) doc.Undo.Push(cmd); }
    }

    private void RegisterMeshActions()
    {
        var doc = Document; var sel = doc.Selection;

        // ---------------------------------------------------------------- Edit Mesh: Components
        Actions.Register("mesh.addDivisions", "Add Divisions...", () => ShowOptions("mesh.addDivisions", "Add Divisions Options", "Add Divisions",
            v => { v.Set("method", 0); v.Set("levels", 1); v.Set("mode", 0); v.Set("u", 2); v.Set("v", 2); v.Set("edgeLevels", 1); },
            new[] { OptionField.E("method", "Division method", "Exponentially", "Linearly"), OptionField.I("levels", "Division levels", 1, 4), OptionField.E("mode", "Mode", "Quads", "Triangles"), OptionField.I("u", "Divisions in U", 1, 32), OptionField.I("v", "Divisions in V", 1, 32), OptionField.I("edgeLevels", "Edge division levels", 1, 32) },
            () => Actions.Invoke("mesh.addDivisionsApply")), canExecute: () => HasComponents(SelectMode.Face, SelectMode.Edge) || sel.Mode == SelectMode.Object && HasMeshSelection());
        Actions.Register("mesh.addDivisionsApply", "Add Divisions", AddDivisionsSelection, canExecute: () => HasComponents(SelectMode.Face, SelectMode.Edge) || sel.Mode == SelectMode.Object && HasMeshSelection(), repeatable: true);

        Actions.Register("mesh.circularize", "Circularize...", () => ShowOptions("mesh.circularize", "Circularize Options", "Circularize",
            v => { v.Set("radial", 0f); v.Set("evenly", 1f); },
            new[] { OptionField.F("radial", "Radial offset", -0.9, 10, 0.01), OptionField.B("evenly", "Evenly distribute") },
            () => Actions.Invoke("mesh.circularizeApply")), canExecute: () => HasComponents(SelectMode.Vertex, SelectMode.Edge, SelectMode.Face));
        Actions.Register("mesh.circularizeApply", "Circularize", CircularizeSelection, canExecute: () => HasComponents(SelectMode.Vertex, SelectMode.Edge, SelectMode.Face), repeatable: true);

        Actions.Register("mesh.collapse", "Collapse", CollapseSelection, canExecute: () => HasComponents(SelectMode.Edge, SelectMode.Face, SelectMode.Vertex), repeatable: true);
        Actions.Register("mesh.connect", "Connect", ConnectSelection, canExecute: () => HasComponents(SelectMode.Vertex, SelectMode.Edge), repeatable: true);
        Actions.Register("mesh.detach", "Detach", DetachSelection, canExecute: () => HasComponents(SelectMode.Vertex, SelectMode.Face, SelectMode.Edge), repeatable: true);
        Actions.Register("mesh.mergeToCenter", "Merge to Center", () => ForEachMeshTarget("Merge to Center", SelectMode.Vertex, (id, ids) => new MeshOpCommand("Merge to Center", id, m => { int r = MeshOps.MergeToCenter(m, ids); return (r >= 0 && ids.Count > 1, SelectMode.Vertex, r >= 0 ? new[] { r } : null); })),
            canExecute: () => HasComponents(SelectMode.Vertex, SelectMode.Edge, SelectMode.Face), repeatable: true);
        Actions.Register("mesh.flipComponents", "Flip...", () => ShowOptions("mesh.symmetry", "Symmetry Options", "Flip", SymmetryDefaults, SymmetryFields, () => Actions.Invoke("mesh.flipComponentsApply")), canExecute: () => HasComponents(SelectMode.Vertex, SelectMode.Edge, SelectMode.Face));
        Actions.Register("mesh.flipComponentsApply", "Flip", () => FlipOrSymmetrize(flip: true), canExecute: () => HasComponents(SelectMode.Vertex, SelectMode.Edge, SelectMode.Face), repeatable: true);
        Actions.Register("mesh.symmetrizeComponents", "Symmetrize...", () => ShowOptions("mesh.symmetry", "Symmetry Options", "Symmetrize", SymmetryDefaults, SymmetryFields, () => Actions.Invoke("mesh.symmetrizeComponentsApply")), canExecute: () => HasComponents(SelectMode.Vertex, SelectMode.Edge, SelectMode.Face));
        Actions.Register("mesh.symmetrizeComponentsApply", "Symmetrize", () => FlipOrSymmetrize(flip: false), canExecute: () => HasComponents(SelectMode.Vertex, SelectMode.Edge, SelectMode.Face), repeatable: true);

        // ---------------------------------------------------------------- Edit Mesh: Vertex
        Actions.Register("mesh.averageVertices", "Average Vertices...", () => ShowOptions("mesh.averageVertices", "Average Vertices Options", "Average",
            v => { v.Set("iterations", 1); v.Set("strength", 0.5f); },
            new[] { OptionField.I("iterations", "Iterations", 1, 100), OptionField.F("strength", "Strength", 0.01, 1, 0.01) },
            () => Actions.Invoke("mesh.averageVerticesApply")), canExecute: () => HasComponents(SelectMode.Vertex, SelectMode.Edge, SelectMode.Face));
        Actions.Register("mesh.averageVerticesApply", "Average Vertices", () =>
        {
            var o = OptWithDefaults("mesh.averageVertices", v => { v.Set("iterations", 1); v.Set("strength", 0.5f); });
            ForEachMeshTarget("Average Vertices", SelectMode.Vertex, (id, ids) => new MeshOpCommand("Average Vertices", id, new HistoryParams(HistoryParam.I("Iterations", o.Int("iterations"), 1, 100), HistoryParam.F("Strength", o.Float("strength"), 0.01f, 1f, 0.01f)),
                (m, p) => { MeshOps.AverageVertices(m, ids, p.Int("Iterations"), p.Float("Strength")); return (true, null, null); }));
        }, canExecute: () => HasComponents(SelectMode.Vertex, SelectMode.Edge, SelectMode.Face), repeatable: true);
        Actions.Register("mesh.chamferVertices", "Chamfer Vertices...", () => ShowOptions("mesh.chamferVertices", "Chamfer Vertices Options", "Chamfer",
            v => { v.Set("width", 0.1f); v.Set("remove", 0f); },
            new[] { OptionField.F("width", "Width", 0.0001, 1000, 0.001), OptionField.B("remove", "Remove the face after chamfer") },
            () => Actions.Invoke("mesh.chamferVerticesApply")), canExecute: () => HasComponents(SelectMode.Vertex));
        Actions.Register("mesh.chamferVerticesApply", "Chamfer Vertices", () =>
        {
            var o = OptWithDefaults("mesh.chamferVertices", v => { v.Set("width", 0.1f); v.Set("remove", 0f); });
            bool remove = o.Bool("remove");
            ForEachMeshTarget("Chamfer Vertices", SelectMode.Vertex, (id, ids) => new MeshOpCommand("Chamfer Vertices", id, new HistoryParams(HistoryParam.F("Width", o.Float("width"), 0.0001f, 1000f, 0.001f)),
                (m, p) => { var faces = MeshOps.ChamferVertices(m, ids, p.Float("Width"), remove); return (true, remove ? null : SelectMode.Face, remove ? null : faces); }));
        }, canExecute: () => HasComponents(SelectMode.Vertex), repeatable: true);

        // ---------------------------------------------------------------- Edit Mesh: Edge
        Actions.Register("mesh.flipTriangleEdge", "Flip Triangle Edge", () => ForEachMeshTarget("Flip Triangle Edge", SelectMode.Edge, (id, ids) => new MeshOpCommand("Flip Triangle Edge", id, m => { var ne = MeshOps.FlipTriangleEdges(m, ids); return (ne.Count > 0, SelectMode.Edge, ne); })), canExecute: () => HasComponents(SelectMode.Edge), repeatable: true);
        Actions.Register("mesh.spinEdgeForward", "Spin Edge Forward", () => ForEachMeshTarget("Spin Edge Forward", SelectMode.Edge, (id, ids) => new MeshOpCommand("Spin Edge Forward", id, m => { var ne = MeshOps.SpinEdges(m, ids, true); return (ne.Count > 0, SelectMode.Edge, ne); })), canExecute: () => HasComponents(SelectMode.Edge), repeatable: true);
        Actions.Register("mesh.spinEdgeBackward", "Spin Edge Backward", () => ForEachMeshTarget("Spin Edge Backward", SelectMode.Edge, (id, ids) => new MeshOpCommand("Spin Edge Backward", id, m => { var ne = MeshOps.SpinEdges(m, ids, false); return (ne.Count > 0, SelectMode.Edge, ne); })), canExecute: () => HasComponents(SelectMode.Edge), repeatable: true);
        Actions.Register("mesh.offsetEdgeLoop", "Offset Edge Loop...", () => ShowOptions("mesh.offsetEdgeLoop", "Offset Edge Loop Options", "Offset", v => v.Set("offset", 0.1f),
            new[] { OptionField.F("offset", "Offset", 0.0001, 1000, 0.001) }, () => Actions.Invoke("mesh.offsetEdgeLoopApply")), canExecute: () => HasComponents(SelectMode.Edge));
        Actions.Register("mesh.offsetEdgeLoopApply", "Offset Edge Loop", () =>
        {
            var o = OptWithDefaults("mesh.offsetEdgeLoop", v => v.Set("offset", 0.1f));
            ForEachMeshTarget("Offset Edge Loop", SelectMode.Edge, (id, ids) => new MeshOpCommand("Offset Edge Loop", id, new HistoryParams(HistoryParam.F("Offset", o.Float("offset"), 0.0001f, 1000f, 0.001f)),
                (m, p) => { var ne = MeshOps.OffsetEdgeLoop(m, ids, p.Float("Offset")); return (ne.Count > 0, SelectMode.Edge, ne); }));
        }, canExecute: () => HasComponents(SelectMode.Edge), repeatable: true);
        Actions.Register("mesh.slideEdge", "Slide Edge...", () => ShowOptions("mesh.slideEdge", "Slide Edge Options", "Slide", v => v.Set("slide", 0.25f),
            new[] { OptionField.F("slide", "Slide (-1..1)", -0.98, 0.98, 0.01, "Positive slides toward the left face of the loop, negative toward the right") }, () => Actions.Invoke("mesh.slideEdgeApply")), canExecute: () => HasComponents(SelectMode.Edge));
        Actions.Register("mesh.slideEdgeApply", "Slide Edge", () =>
        {
            var o = OptWithDefaults("mesh.slideEdge", v => v.Set("slide", 0.25f));
            ForEachMeshTarget("Slide Edge", SelectMode.Edge, (id, ids) => new MeshOpCommand("Slide Edge", id, new HistoryParams(HistoryParam.F("Slide", o.Float("slide"), -0.98f, 0.98f, 0.01f)),
                (m, p) => { MeshOps.SlideEdges(m, ids, p.Float("Slide")); return (true, SelectMode.Edge, ids); }));
        }, canExecute: () => HasComponents(SelectMode.Edge), repeatable: true);

        // ---------------------------------------------------------------- Edit Mesh: Face
        Actions.Register("mesh.duplicateFaces", "Duplicate", () => ForEachMeshTarget("Duplicate Faces", SelectMode.Face, (id, ids) => new MeshOpCommand("Duplicate Faces", id, m => { var nf = MeshOps.DuplicateFaces(m, ids); return (nf.Count > 0, SelectMode.Face, nf); })), canExecute: () => HasComponents(SelectMode.Face), repeatable: true);
        Actions.Register("mesh.extractFaces", "Extract", ExtractSelection, canExecute: () => HasComponents(SelectMode.Face), repeatable: true);
        Actions.Register("mesh.poke", "Poke...", () => ShowOptions("mesh.poke", "Poke Options", "Poke", v => v.Set("offset", 0f),
            new[] { OptionField.F("offset", "Offset (along normal)", -1000, 1000, 0.01) }, () => Actions.Invoke("mesh.pokeApply")), canExecute: () => HasComponents(SelectMode.Face));
        Actions.Register("mesh.pokeApply", "Poke", () =>
        {
            var o = OptWithDefaults("mesh.poke", v => v.Set("offset", 0f));
            ForEachMeshTarget("Poke", SelectMode.Face, (id, ids) => new MeshOpCommand("Poke", id, new HistoryParams(HistoryParam.F("Offset", o.Float("offset"), -1000f, 1000f, 0.01f)),
                (m, p) => { var nv = MeshOps.Poke(m, ids, p.Float("Offset")); return (nv.Count > 0, SelectMode.Vertex, nv); }));
            Tools.SetTool("move");
        }, canExecute: () => HasComponents(SelectMode.Face), repeatable: true);
        Actions.Register("mesh.wedge", "Wedge...", () => ShowOptions("mesh.wedge", "Wedge Options", "Wedge", v => { v.Set("angle", 90f); v.Set("divisions", 4); },
            new[] { OptionField.F("angle", "Arc angle", -360, 360, 1), OptionField.I("divisions", "Divisions", 1, 64) }, () => Actions.Invoke("mesh.wedgeApply")), canExecute: () => HasComponents(SelectMode.Face, SelectMode.Edge));
        Actions.Register("mesh.wedgeApply", "Wedge", WedgeSelection, canExecute: () => HasComponents(SelectMode.Face, SelectMode.Edge), repeatable: true);

        // ---------------------------------------------------------------- Mesh
        Actions.Register("mesh.fillHole", "Fill Hole", () => ForEachMeshTarget("Fill Hole", SelectMode.Edge, (id, ids) => new MeshOpCommand("Fill Hole", id, m => { var nf = MeshOps.FillHoles(m, ids); return (nf.Count > 0, SelectMode.Face, nf); })), canExecute: HasMeshSelection, repeatable: true);
        Actions.Register("mesh.triangulate", "Triangulate", () => ForEachMeshTarget("Triangulate", SelectMode.Face, (id, ids) => new MeshOpCommand("Triangulate", id, m => { var nf = MeshOps.Triangulate(m, ids); return (nf.Count > 0, sel.Mode == SelectMode.Object ? null : SelectMode.Face, nf); })), canExecute: HasMeshSelection, repeatable: true);
        Actions.Register("mesh.quadrangulate", "Quadrangulate...", () => ShowOptions("mesh.quadrangulate", "Quadrangulate Options", "Quadrangulate", v => v.Set("angle", 30f),
            new[] { OptionField.F("angle", "Angle threshold (deg)", 0, 180, 1) }, () => Actions.Invoke("mesh.quadrangulateApply")), canExecute: HasMeshSelection);
        Actions.Register("mesh.quadrangulateApply", "Quadrangulate", () =>
        {
            var o = OptWithDefaults("mesh.quadrangulate", v => v.Set("angle", 30f));
            ForEachMeshTarget("Quadrangulate", SelectMode.Face, (id, ids) => new MeshOpCommand("Quadrangulate", id, new HistoryParams(HistoryParam.F("Angle", o.Float("angle"), 0f, 180f, 1f)),
                (m, p) => { var nf = MeshOps.Quadrangulate(m, ids, p.Float("Angle")); return (nf.Count > 0, sel.Mode == SelectMode.Object ? null : SelectMode.Face, nf); }));
        }, canExecute: HasMeshSelection, repeatable: true);
        Actions.Register("mesh.mirror", "Mirror...", () => ShowOptions("mesh.mirror", "Mirror Options", "Mirror", MirrorDefaults, MirrorFields, () => Actions.Invoke("mesh.mirrorApply")), canExecute: () => sel.Mode == SelectMode.Object && HasMeshSelection());
        Actions.Register("mesh.mirrorApply", "Mirror", () => MirrorSelection(cut: Opt("mesh.mirror").Bool("cut")), canExecute: () => sel.Mode == SelectMode.Object && HasMeshSelection(), repeatable: true);
        Actions.Register("mesh.symmetrizeMesh", "Symmetrize (Mirror + Cut)", () => MirrorSelection(cut: true), canExecute: () => sel.Mode == SelectMode.Object && HasMeshSelection(), repeatable: true);
        Actions.Register("mesh.cleanup", "Cleanup", () =>
        {
            int total = 0;
            ForEachMeshTarget("Cleanup", SelectMode.Face, (id, _) => new MeshOpCommand("Cleanup", id, m => { int n = MeshOps.Cleanup(m); total += n; return n > 0; }));
            HelpLine.Text = $"Cleanup: removed {total} face(s).";
        }, canExecute: HasMeshSelection, repeatable: true);
        Actions.Register("mesh.conform", "Conform (wrap to last selected)", ConformSelection, canExecute: () => sel.Objects.Count >= 2 && doc.Find(sel.ActiveObject)?.Mesh != null, repeatable: true);

        // ---------------------------------------------------------------- Mesh Display (normals)
        Actions.Register("normals.average", "Average Normals", () => ForEachMeshTarget("Average Normals", SelectMode.Vertex, (id, ids) => new MeshOpCommand("Average Normals", id, m => { MeshOps.AverageNormals(m, ids); return true; })), canExecute: HasMeshSelection, repeatable: true);
        Actions.Register("normals.conform", "Conform", () => ForEachMeshTarget("Conform Normals", SelectMode.Face, (id, _) => new MeshOpCommand("Conform Normals", id, m => MeshOps.ConformNormals(m) > 0)), canExecute: HasMeshSelection, repeatable: true);
        Actions.Register("normals.setToFace", "Set to Face", () => ForEachMeshTarget("Set to Face", SelectMode.Vertex, (id, ids) => new MeshOpCommand("Set to Face", id, m => { MeshOps.SetNormalsToFace(m, ids, sel.Mode == SelectMode.Face ? sel.GetComponents(id).Faces : null); return true; })), canExecute: HasMeshSelection, repeatable: true);
        Actions.Register("normals.setVertexNormal", "Set Vertex Normal...", () => ShowOptions("normals.setVertexNormal", "Set Vertex Normal", "Set", v => v.Set("normal", new Vector3(0, 1, 0)),
            new[] { OptionField.V("normal", "Normal XYZ") }, () => Actions.Invoke("normals.setVertexNormalApply")), canExecute: HasMeshSelection);
        Actions.Register("normals.setVertexNormalApply", "Set Vertex Normal", () =>
        {
            var n = OptWithDefaults("normals.setVertexNormal", v => v.Set("normal", new Vector3(0, 1, 0))).Vec("normal");
            ForEachMeshTarget("Set Vertex Normal", SelectMode.Vertex, (id, ids) => new MeshOpCommand("Set Vertex Normal", id, m => { MeshOps.SetVertexNormal(m, ids, new NVec3(n.X, n.Y, n.Z)); return true; }));
        }, canExecute: HasMeshSelection, repeatable: true);
        Actions.Register("normals.softenHardenAngle", "Soften/Harden Edge...", () => ShowOptions("normals.softenHardenAngle", "Soften/Harden Edge", "Apply", v => v.Set("angle", 30f),
            new[] { OptionField.F("angle", "Angle (deg)", 0, 180, 1, "Edges with a dihedral angle above this become hard, others soft") }, () => Actions.Invoke("normals.softenHardenAngleApply")), canExecute: HasMeshSelection);
        Actions.Register("normals.softenHardenAngleApply", "Soften/Harden Edge (angle)", () =>
        {
            var o = OptWithDefaults("normals.softenHardenAngle", v => v.Set("angle", 30f));
            ForEachMeshTarget("Soften/Harden Edge", SelectMode.Edge, (id, ids) => new MeshOpCommand("Soften/Harden Edge", id, new HistoryParams(HistoryParam.F("Angle", o.Float("angle"), 0f, 180f, 1f)),
                (m, p) => { MeshOps.SoftenHardenByAngle(m, ids, p.Float("Angle")); return (true, null, null); }));
        }, canExecute: HasMeshSelection, repeatable: true);
        Actions.Register("normals.lock", "Lock Normals", () => ForEachMeshTarget("Lock Normals", SelectMode.Vertex, (id, ids) => new MeshOpCommand("Lock Normals", id, m => { MeshOps.LockNormals(m, ids); return true; })), canExecute: HasMeshSelection, repeatable: true);
        Actions.Register("normals.unlock", "Unlock Normals", () => ForEachMeshTarget("Unlock Normals", SelectMode.Vertex, (id, ids) => new MeshOpCommand("Unlock Normals", id, m => { MeshOps.UnlockNormals(m, ids); return true; })), canExecute: HasMeshSelection, repeatable: true);

        // ---------------------------------------------------------------- Mesh Tools
        Actions.Register("mesh.crease", "Crease...", () => ShowOptions("mesh.crease", "Crease Options", "Crease", v => v.Set("crease", 2f),
            new[] { OptionField.F("crease", "Crease level (0 = none)", 0, 10, 0.5, "Number of Smooth levels the edge stays sharp") }, () => Actions.Invoke("mesh.creaseApply")), canExecute: () => HasComponents(SelectMode.Edge, SelectMode.Face, SelectMode.Vertex));
        Actions.Register("mesh.creaseApply", "Crease", () =>
        {
            var o = OptWithDefaults("mesh.crease", v => v.Set("crease", 2f));
            ForEachMeshTarget("Crease", SelectMode.Edge, (id, ids) => new MeshOpCommand("Crease", id, new HistoryParams(HistoryParam.F("Crease", o.Float("crease"), 0f, 10f, 0.5f)),
                (m, p) => { MeshOps.SetCrease(m, ids, p.Float("Crease")); return (true, null, null); }));
        }, canExecute: () => HasComponents(SelectMode.Edge, SelectMode.Face, SelectMode.Vertex), repeatable: true);
        Actions.Register("mesh.uncrease", "Remove Crease", () => ForEachMeshTarget("Remove Crease", SelectMode.Edge, (id, ids) => new MeshOpCommand("Remove Crease", id, m => { MeshOps.SetCrease(m, ids, 0f); return true; })), canExecute: () => HasComponents(SelectMode.Edge, SelectMode.Face, SelectMode.Vertex), repeatable: true);
        Actions.Register("mesh.multiCut", "Multi-Cut Tool", () => Tools.SetTool("multiCut"), isChecked: () => Tools.Current?.Id == "multiCut");
        Actions.Register("mesh.targetWeld", "Target Weld Tool", () => Tools.SetTool("targetWeld"), isChecked: () => Tools.Current?.Id == "targetWeld");
        Actions.Register("mesh.appendPolygon", "Append to Polygon Tool", () => Tools.SetTool("appendPolygon"), isChecked: () => Tools.Current?.Id == "appendPolygon");
    }

    // ---------------------------------------------------------------- 구현

    private void AddDivisionsSelection()
    {
        var o = OptWithDefaults("mesh.addDivisions", v => { v.Set("method", 0); v.Set("levels", 1); v.Set("mode", 0); v.Set("u", 2); v.Set("v", 2); v.Set("edgeLevels", 1); });
        var sel = Document.Selection;
        if (sel.Mode == SelectMode.Edge)
        {
            ForEachMeshTarget("Add Divisions", SelectMode.Edge, (id, ids) => new MeshOpCommand("Add Divisions", id, new HistoryParams(HistoryParam.I("Levels", o.Int("edgeLevels"), 1, 32)),
                (m, p) => { var nv = MeshOps.DivideEdges(m, ids, p.Int("Levels")); return (nv.Count > 0, SelectMode.Vertex, nv); }));
            return;
        }
        bool linear = o.Int("method") == 1;
        var mode = o.Int("mode") == 1 ? MeshOps.DivisionMode.Triangles : MeshOps.DivisionMode.Quads;
        ForEachMeshTarget("Add Divisions", SelectMode.Face, (id, ids) => linear
            ? new MeshOpCommand("Add Divisions", id, new HistoryParams(HistoryParam.I("U", o.Int("u"), 1, 32), HistoryParam.I("V", o.Int("v"), 1, 32)), (m, p) => { var nf = MeshOps.AddDivisionsLinear(m, ids, p.Int("U"), p.Int("V")); return (nf.Count > 0, SelectMode.Face, nf); })
            : new MeshOpCommand("Add Divisions", id, new HistoryParams(HistoryParam.I("Levels", o.Int("levels"), 1, 4)), (m, p) => { var nf = MeshOps.AddDivisions(m, ids, p.Int("Levels"), mode); return (nf.Count > 0, SelectMode.Face, nf); }));
    }

    private void CircularizeSelection()
    {
        var o = OptWithDefaults("mesh.circularize", v => { v.Set("radial", 0f); v.Set("evenly", 1f); });
        var sel = Document.Selection;
        bool evenly = o.Bool("evenly");
        ForEachMeshTarget("Circularize", SelectMode.Vertex, (id, ids) =>
        {
            var mesh = Document.Find(id)!.Mesh!;
            // 면 선택이면 둘레 정점만
            if (sel.Mode == SelectMode.Face)
            {
                var border = SelectionOps.BoundaryEdgesOfFaces(mesh, sel.GetComponents(id).Faces);
                var bv = new HashSet<int>(); foreach (int e in border) { var (a, b) = mesh.EdgeVertices(e); bv.Add(a); bv.Add(b); }
                if (bv.Count >= 3) ids = bv;
            }
            var verts = ids.ToArray();
            return new MeshOpCommand("Circularize", id, new HistoryParams(HistoryParam.F("Radial Offset", o.Float("radial"), -0.9f, 10f, 0.01f)),
                (m, p) => { MeshOps.Circularize(m, verts, p.Float("Radial Offset"), evenly); return (true, null, null); });
        });
    }

    private void CollapseSelection()
    {
        var sel = Document.Selection;
        if (sel.Mode == SelectMode.Face) ForEachMeshTarget("Collapse", SelectMode.Face, (id, ids) => new MeshOpCommand("Collapse", id, m => { var r = MeshOps.CollapseFaces(m, ids); return (r.Count > 0, SelectMode.Vertex, r); }));
        else if (sel.Mode == SelectMode.Edge) ForEachMeshTarget("Collapse", SelectMode.Edge, (id, ids) => new MeshOpCommand("Collapse", id, m => { var r = MeshOps.CollapseEdges(m, ids); return (r.Count > 0, SelectMode.Vertex, r); }));
        else Actions.Invoke("mesh.mergeToCenter");
    }

    private void ConnectSelection()
    {
        var sel = Document.Selection;
        if (sel.Mode == SelectMode.Edge) ForEachMeshTarget("Connect", SelectMode.Edge, (id, ids) => new MeshOpCommand("Connect", id, m => { var ne = MeshOps.ConnectEdges(m, ids); return (ne.Count > 0, SelectMode.Edge, ne); }));
        else ForEachMeshTarget("Connect", SelectMode.Vertex, (id, ids) => new MeshOpCommand("Connect", id, m => { var ne = MeshOps.ConnectVertices(m, ids); return (ne.Count > 0, SelectMode.Edge, ne); }));
    }

    private void DetachSelection()
    {
        var sel = Document.Selection;
        if (sel.Mode == SelectMode.Face) ForEachMeshTarget("Detach", SelectMode.Face, (id, ids) => new MeshOpCommand("Detach", id, m => { var nf = MeshOps.DetachFaces(m, ids); return (nf.Count > 0, SelectMode.Face, nf); }));
        else ForEachMeshTarget("Detach", SelectMode.Vertex, (id, ids) => new MeshOpCommand("Detach", id, m => { var nv = MeshOps.DetachVertices(m, ids); return (nv.Count > 0, SelectMode.Vertex, nv.Concat(ids)); }));
    }

    private void ExtractSelection()
    {
        var doc = Document; var sel = doc.Selection;
        using (doc.Undo.BeginGroup("Extract"))
        {
            foreach (var id in sel.NodesWithComponents(SelectMode.Face).ToArray())
            {
                var node = doc.Find(id); var mesh = node?.Mesh; if (node == null || mesh == null) continue;
                var faces = sel.GetComponents(id).Faces.ToArray();
                if (faces.Length == 0 || faces.Length >= mesh.AliveFaceCount) continue;
                var extracted = MeshOps.ExtractFaces(mesh, faces);
                MeshNormals.Recompute(extracted);
                var copy = new SceneNode { Name = doc.UniqueName("polySurface1"), Local = node.Local, Visible = node.Visible, Shape = new MeshShape(extracted), MaterialId = node.MaterialId };
                doc.Undo.Push(new DeleteComponentsCommand(id, SelectMode.Face, faces));
                doc.Undo.Push(new AddNodeCommand("Extract", copy, node.Parent != null && !node.Parent.IsRoot ? node.Parent.Id : NodeId.None));
                RecordSelection(s => { s.Mode = SelectMode.Object; s.SelectObjects(new[] { copy.Id }); });
            }
        }
    }

    private void WedgeSelection()
    {
        var o = OptWithDefaults("mesh.wedge", v => { v.Set("angle", 90f); v.Set("divisions", 4); });
        var doc = Document; var sel = doc.Selection;
        bool any = false;
        using (doc.Undo.BeginGroup("Wedge"))
            foreach (var id in sel.Components.Keys.ToArray())
            {
                var mesh = doc.Find(id)?.Mesh; if (mesh == null) continue;
                var comps = sel.GetComponents(id);
                var faces = comps.Faces.ToArray(); var edge = comps.Edges.FirstOrDefault(-1);
                if (faces.Length == 0 || edge < 0) continue;
                any = true;
                doc.Undo.Push(new MeshOpCommand("Wedge", id, new HistoryParams(HistoryParam.F("Arc Angle", o.Float("angle"), -360f, 360f, 1f), HistoryParam.I("Divisions", o.Int("divisions"), 1, 64)),
                    (m, p) => { var nf = MeshOps.Wedge(m, faces, edge, p.Float("Arc Angle"), p.Int("Divisions")); return (nf.Count > 0, SelectMode.Face, nf); }));
            }
        if (!any) HelpLine.Text = "Wedge: select the faces (face mode), then switch to edge mode and select one pivot edge on their border.";
    }

    private static readonly OptionField[] MirrorFields =
    {
        OptionField.E("axis", "Mirror axis", "X", "Y", "Z"), OptionField.E("direction", "Direction", "+ (keep positive side)", "- (keep negative side)"),
        OptionField.E("position", "Mirror axis position", "Bounding Box", "Object", "World"), OptionField.B("merge", "Merge vertices"), OptionField.F("threshold", "Merge threshold", 0, 10, 0.0001), OptionField.B("cut", "Cut geometry (symmetrize)"),
    };
    private static void MirrorDefaults(OptionValues v) { v.Set("axis", 0); v.Set("direction", 0); v.Set("position", 0); v.Set("merge", 1f); v.Set("threshold", 0.001f); v.Set("cut", 0f); }

    private void MirrorSelection(bool cut)
    {
        var o = OptWithDefaults("mesh.mirror", MirrorDefaults);
        int axis = o.Int("axis"); bool keepPositive = o.Int("direction") == 0; int position = o.Int("position");
        float threshold = o.Bool("merge") ? o.Float("threshold") : 0f;
        var doc = Document;
        using (doc.Undo.BeginGroup(cut ? "Symmetrize" : "Mirror"))
            foreach (var id in doc.Selection.Objects.ToArray())
            {
                var node = doc.Find(id); var mesh = node?.Mesh; if (node == null || mesh == null) continue;
                float plane = 0f;
                if (position == 0)
                {
                    float mn = float.MaxValue, mx = float.MinValue;
                    foreach (var v in mesh.Verts) if (v.Alive) { float c = axis == 0 ? v.Position.X : axis == 1 ? v.Position.Y : v.Position.Z; mn = MathF.Min(mn, c); mx = MathF.Max(mx, c); }
                    plane = (mn + mx) * 0.5f;
                }
                else if (position == 2)
                {
                    System.Numerics.Matrix4x4.Invert(node.WorldMatrix, out var inv);
                    var origin = NVec3.Transform(NVec3.Zero, inv);
                    plane = axis == 0 ? origin.X : axis == 1 ? origin.Y : origin.Z;
                }
                doc.Undo.Push(new MeshOpCommand(cut ? "Symmetrize" : "Mirror", id, new HistoryParams(HistoryParam.F("Plane", plane), HistoryParam.F("Merge Threshold", threshold, 0f, 10f, 0.0001f)),
                    (m, p) => { var nf = MeshOps.MirrorGeometry(m, axis, p.Float("Plane"), keepPositive, cut, p.Float("Merge Threshold")); return (nf.Count > 0, null, null); }));
            }
    }

    private static readonly OptionField[] SymmetryFields = { OptionField.E("axis", "Symmetry axis", "X", "Y", "Z"), OptionField.F("tolerance", "Tolerance", 0.0001, 10, 0.001) };
    private static void SymmetryDefaults(OptionValues v) { v.Set("axis", 0); v.Set("tolerance", 0.01f); }

    private void FlipOrSymmetrize(bool flip)
    {
        var o = OptWithDefaults("mesh.symmetry", SymmetryDefaults);
        int axis = o.Int("axis"); float tol = o.Float("tolerance");
        ForEachMeshTarget(flip ? "Flip" : "Symmetrize", SelectMode.Vertex, (id, ids) => new MeshOpCommand(flip ? "Flip" : "Symmetrize", id, m =>
            (flip ? MeshOps.FlipVertices(m, ids, axis, 0f, tol) : MeshOps.SymmetrizeVertices(m, ids, axis, 0f, tol)) > 0));
    }

    private void ConformSelection()
    {
        var doc = Document; var sel = doc.Selection;
        var target = doc.Find(sel.ActiveObject); if (target?.Mesh == null) return;
        var sources = sel.Objects.Where(id => id != target.Id && doc.Find(id)?.Mesh != null).ToArray();
        if (sources.Length == 0) { HelpLine.Text = "Conform: select the mesh to wrap first, then the target surface last."; return; }
        using (doc.Undo.BeginGroup("Conform"))
            foreach (var id in sources)
            {
                var node = doc.Find(id)!;
                System.Numerics.Matrix4x4.Invert(node.WorldMatrix, out var inv);
                var targetToLocal = target.WorldMatrix * inv;
                var targetMesh = target.Mesh.Clone();
                doc.Undo.Push(new MeshOpCommand("Conform", id, m => { MeshOps.ConformToSurface(m, Enumerable.Range(0, m.VertexCount), targetMesh, targetToLocal); return true; }));
            }
        HelpLine.Text = $"Conform: wrapped {sources.Length} object(s) onto {target.Name}.";
    }
}
