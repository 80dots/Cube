using Cube.Core.Commands;
using Cube.Core.Mesh;
using Cube.Core.Scene;
using Cube.Core.Selection;
using Godot;
using NVec3 = System.Numerics.Vector3;

namespace Cube.App.UI;

/// <summary>옵션 창 명세: 액션별 OptionValues 키, 제목, 기본값, 필드.</summary>
public sealed record OptionSpec(string Title, Action<OptionValues> Defaults, OptionField[] Fields, string OkText = "Apply");

/// <summary>
/// Maya Mesh / Edit Mesh / Mesh Tools / Mesh Display 메뉴의 모델링 액션.
/// 옵션이 있는 명령은 <see cref="RegisterOptionPair"/>로 "<id>"(옵션 창, 라벨 "X Options...")와 "<id>Apply"(마지막 옵션으로 즉시 실행, 라벨 "X") 두 액션을 가진다.
/// 메뉴/파이/셸프는 Apply 쪽을 실행 항목으로 쓰고 메뉴에만 Options... 항목을 덧붙인다(MenuBuilder.Op). 모든 편집은 MeshOpCommand(Undo + 구성 이력).
/// </summary>
public partial class Shell
{
    private readonly Dictionary<string, OptionValues> _options = new();
    private readonly Dictionary<string, OptionSpec> _optionSpecs = new();

    private OptionValues Opt(string id) { if (!_options.TryGetValue(id, out var v)) { v = new OptionValues(); _options[id] = v; } return v; }

    /// <summary>액션 id의 옵션 값(기본값이 아직 없으면 명세의 기본값을 채운다).</summary>
    public OptionValues Options(string id)
    {
        var v = Opt(id);
        if (!v.Has("_init") && _optionSpecs.TryGetValue(id, out var spec)) { spec.Defaults(v); v.Set("_init", 1f); }
        return v;
    }

    /// <summary>옵션 창을 띄운다(명세 등록 필요). OK면 apply.</summary>
    private void ShowOptions(string id, Action apply)
    {
        if (!_optionSpecs.TryGetValue(id, out var spec)) return;
        var dlg = new OptionsDialog(spec.Title, spec.OkText, spec.Fields, Options(id), apply);
        AddChild(dlg);
        dlg.Confirmed += dlg.QueueFree; dlg.Canceled += dlg.QueueFree;
        dlg.PopupCentered();
    }

    /// <summary>
    /// 옵션 쌍 등록: "<id>" = 옵션 창("label Options..."), "<id>Apply" = 마지막 옵션으로 실행("label", repeatable).
    /// apply는 Options(id)로 값을 읽는다.
    /// </summary>
    private void RegisterOptionPair(string id, string label, OptionSpec spec, Action apply, Func<bool>? canExecute = null)
    {
        _optionSpecs[id] = spec;
        Actions.Register(id, label + " Options...", () => ShowOptions(id, () => Actions.Invoke(id + "Apply")), canExecute: canExecute);
        Actions.Register(id + "Apply", label, apply, canExecute: canExecute, repeatable: true);
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
                HashSet<int> set;
                if (sel.Mode == SelectMode.Uv)
                {
                    var topo = Core.Uv.UvTopology.Build(mesh);
                    var c = new ComponentSet(); foreach (int p in comps.Uvs) if (p < topo.Points.Count) c.Verts.Add(topo.Points[p].Vertex);
                    set = wanted == SelectMode.Vertex ? c.Verts : SelectionOps.Convert(mesh, c, SelectMode.Vertex, wanted);
                }
                else set = sel.Mode == wanted ? new HashSet<int>(comps.Get(wanted)) : SelectionOps.Convert(mesh, comps, sel.Mode, wanted);
                if (set.Count > 0) targets.Add((id, set));
            }
        }
        if (targets.Count == 0) { HelpLine.Text = $"{groupName}: nothing selected."; return; }
        using (doc.Undo.BeginGroup(groupName))
            foreach (var (id, ids) in targets) { var cmd = make(id, ids); if (cmd != null) doc.Undo.Push(cmd); }
    }

    /// <summary>파라미터 하나짜리 MeshOp(히스토리 편집 가능)를 간단히 만든다.</summary>
    private static MeshOpCommand ParamOp(string name, NodeId id, HistoryParam param, Func<PolyMesh, HistoryParams, (bool, SelectMode?, IEnumerable<int>?)> op)
        => new(name, id, new HistoryParams(param), op);

    private void RegisterMeshActions()
    {
        var doc = Document; var sel = doc.Selection;
        Func<bool> faceOrEdge = () => HasComponents(SelectMode.Face, SelectMode.Edge) || sel.Mode == SelectMode.Object && HasMeshSelection();
        Func<bool> anyComps = () => HasComponents(SelectMode.Vertex, SelectMode.Edge, SelectMode.Face);
        Func<bool> edges = () => HasComponents(SelectMode.Edge);
        Func<bool> faces = () => HasComponents(SelectMode.Face);
        Func<bool> verts = () => HasComponents(SelectMode.Vertex);

        // ---------------------------------------------------------------- Mesh (기존 Smooth/Merge/Bevel도 옵션 쌍으로)
        RegisterOptionPair("mesh.smooth", "Smooth", new OptionSpec("Smooth Options", v => v.Set("levels", 1), new[] { OptionField.I("levels", "Division levels", 1, 4) }, "Smooth"), SmoothSelection,
            () => sel.Mode == SelectMode.Object && sel.Objects.Any(id => doc.Find(id)?.Mesh != null));
        RegisterOptionPair("mesh.merge", "Merge Vertices", new OptionSpec("Merge Vertices Options", v => v.Set("threshold", 0.001f), new[] { OptionField.F("threshold", "Threshold", 0, 1000, 0.0001) }, "Merge"), MergeSelectedVertices,
            () => sel.IsComponentMode && sel.NodesWithComponents(sel.Mode).Any());
        RegisterOptionPair("mesh.bevel", "Bevel", new OptionSpec("Bevel Options", v => { v.Set("distance", 0.1f); v.Set("segments", 1); }, new[] { OptionField.F("distance", "Distance", 0.0001, 1000, 0.001), OptionField.I("segments", "Segments", 1, 16, "1 = chamfer, 2+ = rounded profile with that many strips") }, "Bevel"), BevelSelection,
            () => sel.Mode is SelectMode.Edge or SelectMode.Face && sel.NodesWithComponents(sel.Mode).Any());
        RegisterOptionPair("mesh.quadrangulate", "Quadrangulate", new OptionSpec("Quadrangulate Options", v => v.Set("angle", 30f), new[] { OptionField.F("angle", "Angle threshold (deg)", 0, 180, 1) }), () =>
        {
            float angle = Options("mesh.quadrangulate").Float("angle");
            ForEachMeshTarget("Quadrangulate", SelectMode.Face, (id, ids) => ParamOp("Quadrangulate", id, HistoryParam.F("Angle", angle, 0f, 180f, 1f),
                (m, p) => { var nf = MeshOps.Quadrangulate(m, ids, p.Float("Angle")); return (nf.Count > 0, sel.Mode == SelectMode.Object ? null : SelectMode.Face, nf); }));
        }, HasMeshSelection);
        RegisterOptionPair("mesh.mirror", "Mirror", new OptionSpec("Mirror Options", MirrorDefaults, MirrorFields), () => MirrorSelection(cut: Options("mesh.mirror").Bool("cut")), () => sel.Mode == SelectMode.Object && HasMeshSelection());
        Actions.Register("mesh.symmetrizeMesh", "Symmetrize (Mirror + Cut)", () => MirrorSelection(cut: true), canExecute: () => sel.Mode == SelectMode.Object && HasMeshSelection(), repeatable: true);
        Actions.Register("mesh.fillHole", "Fill Hole", () => ForEachMeshTarget("Fill Hole", SelectMode.Edge, (id, ids) => new MeshOpCommand("Fill Hole", id, m => { var nf = MeshOps.FillHoles(m, ids); return (nf.Count > 0, SelectMode.Face, nf); })), canExecute: HasMeshSelection, repeatable: true);
        Actions.Register("mesh.triangulate", "Triangulate", () => ForEachMeshTarget("Triangulate", SelectMode.Face, (id, ids) => new MeshOpCommand("Triangulate", id, m => { var nf = MeshOps.Triangulate(m, ids); return (nf.Count > 0, sel.Mode == SelectMode.Object ? null : SelectMode.Face, nf); })), canExecute: HasMeshSelection, repeatable: true);
        Actions.Register("mesh.cleanup", "Cleanup", () =>
        {
            int total = 0;
            ForEachMeshTarget("Cleanup", SelectMode.Face, (id, _) => new MeshOpCommand("Cleanup", id, m => { int n = MeshOps.Cleanup(m); total += n; return n > 0; }));
            HelpLine.Text = $"Cleanup: removed {total} face(s).";
        }, canExecute: HasMeshSelection, repeatable: true);
        Actions.Register("mesh.conform", "Conform (wrap to last selected)", ConformSelection, canExecute: () => sel.Objects.Count >= 2 && doc.Find(sel.ActiveObject)?.Mesh != null, repeatable: true);

        // ---------------------------------------------------------------- Edit Mesh: Components
        RegisterOptionPair("mesh.addDivisions", "Add Divisions", new OptionSpec("Add Divisions Options",
            v => { v.Set("method", 0); v.Set("levels", 1); v.Set("mode", 0); v.Set("u", 2); v.Set("v", 2); v.Set("edgeLevels", 1); },
            new[] { OptionField.E("method", "Division method", "Exponentially", "Linearly"), OptionField.I("levels", "Division levels", 1, 4), OptionField.E("mode", "Mode", "Quads", "Triangles"), OptionField.I("u", "Divisions in U", 1, 32), OptionField.I("v", "Divisions in V", 1, 32), OptionField.I("edgeLevels", "Edge division levels", 1, 32) }),
            AddDivisionsSelection, faceOrEdge);
        RegisterOptionPair("mesh.circularize", "Circularize", new OptionSpec("Circularize Options", v => { v.Set("radial", 0f); v.Set("evenly", 1f); },
            new[] { OptionField.F("radial", "Radial offset", -0.9, 10, 0.01), OptionField.B("evenly", "Evenly distribute") }), CircularizeSelection, anyComps);
        Actions.Register("mesh.collapse", "Collapse", CollapseSelection, canExecute: anyComps, repeatable: true);
        Actions.Register("mesh.connect", "Connect", ConnectSelection, canExecute: () => HasComponents(SelectMode.Vertex, SelectMode.Edge), repeatable: true);
        Actions.Register("mesh.detach", "Detach", DetachSelection, canExecute: anyComps, repeatable: true);
        Actions.Register("mesh.mergeToCenter", "Merge to Center", () => ForEachMeshTarget("Merge to Center", SelectMode.Vertex, (id, ids) => new MeshOpCommand("Merge to Center", id, m => { int r = MeshOps.MergeToCenter(m, ids); return (r >= 0 && ids.Count > 1, SelectMode.Vertex, r >= 0 ? new[] { r } : null); })), canExecute: anyComps, repeatable: true);
        var symmetrySpec = new OptionSpec("Symmetry Options", SymmetryDefaults, SymmetryFields);
        _optionSpecs["mesh.symmetry"] = symmetrySpec;
        Actions.Register("mesh.flipComponents", "Flip Options...", () => ShowOptions("mesh.symmetry", () => Actions.Invoke("mesh.flipComponentsApply")), canExecute: anyComps);
        Actions.Register("mesh.flipComponentsApply", "Flip", () => FlipOrSymmetrize(flip: true), canExecute: anyComps, repeatable: true);
        Actions.Register("mesh.symmetrizeComponents", "Symmetrize Options...", () => ShowOptions("mesh.symmetry", () => Actions.Invoke("mesh.symmetrizeComponentsApply")), canExecute: anyComps);
        Actions.Register("mesh.symmetrizeComponentsApply", "Symmetrize", () => FlipOrSymmetrize(flip: false), canExecute: anyComps, repeatable: true);

        // ---------------------------------------------------------------- Edit Mesh: Vertex
        RegisterOptionPair("mesh.averageVertices", "Average Vertices", new OptionSpec("Average Vertices Options", v => { v.Set("iterations", 1); v.Set("strength", 0.5f); },
            new[] { OptionField.I("iterations", "Iterations", 1, 100), OptionField.F("strength", "Strength", 0.01, 1, 0.01) }), () =>
        {
            var o = Options("mesh.averageVertices");
            ForEachMeshTarget("Average Vertices", SelectMode.Vertex, (id, ids) => new MeshOpCommand("Average Vertices", id, new HistoryParams(HistoryParam.I("Iterations", o.Int("iterations"), 1, 100), HistoryParam.F("Strength", o.Float("strength"), 0.01f, 1f, 0.01f)),
                (m, p) => { MeshOps.AverageVertices(m, ids, p.Int("Iterations"), p.Float("Strength")); return (true, null, null); }));
        }, anyComps);
        RegisterOptionPair("mesh.chamferVertices", "Chamfer Vertices", new OptionSpec("Chamfer Vertices Options", v => { v.Set("width", 0.1f); v.Set("remove", 0f); },
            new[] { OptionField.F("width", "Width", 0.0001, 1000, 0.001), OptionField.B("remove", "Remove the face after chamfer") }), () =>
        {
            var o = Options("mesh.chamferVertices"); bool remove = o.Bool("remove");
            ForEachMeshTarget("Chamfer Vertices", SelectMode.Vertex, (id, ids) => ParamOp("Chamfer Vertices", id, HistoryParam.F("Width", o.Float("width"), 0.0001f, 1000f, 0.001f),
                (m, p) => { var nf = MeshOps.ChamferVertices(m, ids, p.Float("Width"), remove); return (true, remove ? null : SelectMode.Face, remove ? null : nf); }));
        }, verts);

        // ---------------------------------------------------------------- Edit Mesh: Edge
        Actions.Register("mesh.flipTriangleEdge", "Flip Triangle Edge", () => ForEachMeshTarget("Flip Triangle Edge", SelectMode.Edge, (id, ids) => new MeshOpCommand("Flip Triangle Edge", id, m => { var ne = MeshOps.FlipTriangleEdges(m, ids); return (ne.Count > 0, SelectMode.Edge, ne); })), canExecute: edges, repeatable: true);
        Actions.Register("mesh.spinEdgeForward", "Spin Edge Forward", () => ForEachMeshTarget("Spin Edge Forward", SelectMode.Edge, (id, ids) => new MeshOpCommand("Spin Edge Forward", id, m => { var ne = MeshOps.SpinEdges(m, ids, true); return (ne.Count > 0, SelectMode.Edge, ne); })), canExecute: edges, repeatable: true);
        Actions.Register("mesh.spinEdgeBackward", "Spin Edge Backward", () => ForEachMeshTarget("Spin Edge Backward", SelectMode.Edge, (id, ids) => new MeshOpCommand("Spin Edge Backward", id, m => { var ne = MeshOps.SpinEdges(m, ids, false); return (ne.Count > 0, SelectMode.Edge, ne); })), canExecute: edges, repeatable: true);
        RegisterOptionPair("mesh.offsetEdgeLoop", "Offset Edge Loop", new OptionSpec("Offset Edge Loop Options", v => v.Set("offset", 0.1f), new[] { OptionField.F("offset", "Offset", 0.0001, 1000, 0.001) }), () =>
        {
            float offset = Options("mesh.offsetEdgeLoop").Float("offset");
            ForEachMeshTarget("Offset Edge Loop", SelectMode.Edge, (id, ids) => ParamOp("Offset Edge Loop", id, HistoryParam.F("Offset", offset, 0.0001f, 1000f, 0.001f),
                (m, p) => { var ne = MeshOps.OffsetEdgeLoop(m, ids, p.Float("Offset")); return (ne.Count > 0, SelectMode.Edge, ne); }));
        }, edges);
        RegisterOptionPair("mesh.slideEdge", "Slide Edge", new OptionSpec("Slide Edge Options", v => v.Set("slide", 0.25f), new[] { OptionField.F("slide", "Slide (-1..1)", -0.98, 0.98, 0.01, "Positive slides toward the left face of the loop, negative toward the right") }), () =>
        {
            float slide = Options("mesh.slideEdge").Float("slide");
            ForEachMeshTarget("Slide Edge", SelectMode.Edge, (id, ids) => ParamOp("Slide Edge", id, HistoryParam.F("Slide", slide, -0.98f, 0.98f, 0.01f),
                (m, p) => { MeshOps.SlideEdges(m, ids, p.Float("Slide")); return (true, SelectMode.Edge, ids); }));
        }, edges);

        // ---------------------------------------------------------------- Edit Mesh: Face
        Actions.Register("mesh.duplicateFaces", "Duplicate", () => ForEachMeshTarget("Duplicate Faces", SelectMode.Face, (id, ids) => new MeshOpCommand("Duplicate Faces", id, m => { var nf = MeshOps.DuplicateFaces(m, ids); return (nf.Count > 0, SelectMode.Face, nf); })), canExecute: faces, repeatable: true);
        Actions.Register("mesh.extractFaces", "Extract", ExtractSelection, canExecute: faces, repeatable: true);
        RegisterOptionPair("mesh.poke", "Poke", new OptionSpec("Poke Options", v => v.Set("offset", 0f), new[] { OptionField.F("offset", "Offset (along normal)", -1000, 1000, 0.01) }), () =>
        {
            float offset = Options("mesh.poke").Float("offset");
            ForEachMeshTarget("Poke", SelectMode.Face, (id, ids) => ParamOp("Poke", id, HistoryParam.F("Offset", offset, -1000f, 1000f, 0.01f),
                (m, p) => { var nv = MeshOps.Poke(m, ids, p.Float("Offset")); return (nv.Count > 0, SelectMode.Vertex, nv); }));
            Tools.SetTool("move");
        }, faces);
        RegisterOptionPair("mesh.wedge", "Wedge", new OptionSpec("Wedge Options", v => { v.Set("angle", 90f); v.Set("divisions", 4); },
            new[] { OptionField.F("angle", "Arc angle", -360, 360, 1), OptionField.I("divisions", "Divisions", 1, 64) }), WedgeSelection, () => HasComponents(SelectMode.Face, SelectMode.Edge));

        // ---------------------------------------------------------------- Mesh Display (normals)
        Actions.Register("normals.average", "Average Normals", () => ForEachMeshTarget("Average Normals", SelectMode.Vertex, (id, ids) => new MeshOpCommand("Average Normals", id, m => { MeshOps.AverageNormals(m, ids); return true; })), canExecute: HasMeshSelection, repeatable: true);
        Actions.Register("normals.conform", "Conform", () => ForEachMeshTarget("Conform Normals", SelectMode.Face, (id, _) => new MeshOpCommand("Conform Normals", id, m => MeshOps.ConformNormals(m) > 0)), canExecute: HasMeshSelection, repeatable: true);
        Actions.Register("normals.setToFace", "Set to Face", () => ForEachMeshTarget("Set to Face", SelectMode.Vertex, (id, ids) => new MeshOpCommand("Set to Face", id, m => { MeshOps.SetNormalsToFace(m, ids, sel.Mode == SelectMode.Face ? sel.GetComponents(id).Faces : null); return true; })), canExecute: HasMeshSelection, repeatable: true);
        RegisterOptionPair("normals.setVertexNormal", "Set Vertex Normal", new OptionSpec("Set Vertex Normal", v => v.Set("normal", new Vector3(0, 1, 0)), new[] { OptionField.V("normal", "Normal XYZ") }, "Set"), () =>
        {
            var n = Options("normals.setVertexNormal").Vec("normal");
            ForEachMeshTarget("Set Vertex Normal", SelectMode.Vertex, (id, ids) => new MeshOpCommand("Set Vertex Normal", id, m => { MeshOps.SetVertexNormal(m, ids, new NVec3(n.X, n.Y, n.Z)); return true; }));
        }, HasMeshSelection);
        RegisterOptionPair("normals.softenHardenAngle", "Soften/Harden Edge", new OptionSpec("Soften/Harden Edge", v => v.Set("angle", 30f), new[] { OptionField.F("angle", "Angle (deg)", 0, 180, 1, "Edges with a dihedral angle above this become hard, others soft") }), () =>
        {
            float angle = Options("normals.softenHardenAngle").Float("angle");
            ForEachMeshTarget("Soften/Harden Edge", SelectMode.Edge, (id, ids) => ParamOp("Soften/Harden Edge", id, HistoryParam.F("Angle", angle, 0f, 180f, 1f),
                (m, p) => { MeshOps.SoftenHardenByAngle(m, ids, p.Float("Angle")); return (true, null, null); }));
        }, HasMeshSelection);
        Actions.Register("normals.lock", "Lock Normals", () => ForEachMeshTarget("Lock Normals", SelectMode.Vertex, (id, ids) => new MeshOpCommand("Lock Normals", id, m => { MeshOps.LockNormals(m, ids); return true; })), canExecute: HasMeshSelection, repeatable: true);
        Actions.Register("normals.unlock", "Unlock Normals", () => ForEachMeshTarget("Unlock Normals", SelectMode.Vertex, (id, ids) => new MeshOpCommand("Unlock Normals", id, m => { MeshOps.UnlockNormals(m, ids); return true; })), canExecute: HasMeshSelection, repeatable: true);

        // ---------------------------------------------------------------- Mesh Tools
        RegisterOptionPair("mesh.crease", "Crease", new OptionSpec("Crease Options", v => v.Set("crease", 2f), new[] { OptionField.F("crease", "Crease level (0 = none)", 0, 10, 0.5, "Number of Smooth levels the edge stays sharp") }), () =>
        {
            float crease = Options("mesh.crease").Float("crease");
            ForEachMeshTarget("Crease", SelectMode.Edge, (id, ids) => ParamOp("Crease", id, HistoryParam.F("Crease", crease, 0f, 10f, 0.5f),
                (m, p) => { MeshOps.SetCrease(m, ids, p.Float("Crease")); return (true, null, null); }));
        }, anyComps);
        Actions.Register("mesh.uncrease", "Remove Crease", () => ForEachMeshTarget("Remove Crease", SelectMode.Edge, (id, ids) => new MeshOpCommand("Remove Crease", id, m => { MeshOps.SetCrease(m, ids, 0f); return true; })), canExecute: anyComps, repeatable: true);
        Actions.Register("mesh.multiCut", "Multi-Cut Tool", () => Tools.SetTool("multiCut"), isChecked: () => Tools.Current?.Id == "multiCut");
        Actions.Register("mesh.targetWeld", "Target Weld Tool", () => Tools.SetTool("targetWeld"), isChecked: () => Tools.Current?.Id == "targetWeld");
        Actions.Register("mesh.appendPolygon", "Append to Polygon Tool", () => Tools.SetTool("appendPolygon"), isChecked: () => Tools.Current?.Id == "appendPolygon");
    }

    // ---------------------------------------------------------------- 구현

    private void SmoothSelection()
    {
        int levels = Options("mesh.smooth").Int("levels");
        var targets = Document.Selection.Objects.Where(id => Document.Find(id)?.Mesh != null).ToArray();
        using (Document.Undo.BeginGroup("Smooth"))
            foreach (var id in targets)
                Document.Undo.Push(ParamOp("Smooth", id, HistoryParam.I("Levels", levels, 0, 4), (m, p) => { MeshOps.Smooth(m, p.Int("Levels")); return (true, null, null); }));
        HelpLine.Text = $"Smooth: {levels} level(s).";
    }

    private void MergeSelectedVertices()
    {
        float threshold = Options("mesh.merge").Float("threshold");
        var sel = Document.Selection; var mode = sel.Mode;
        int total = 0;
        ForEachComponentNode("Merge Vertices", mode, (id, comps) =>
        {
            var mesh = Document.Find(id)?.Mesh; if (mesh == null) return null;
            var verts = mode == SelectMode.Vertex ? comps.Verts : SelectionOps.Convert(mesh, comps, mode, SelectMode.Vertex);
            return new MergeVerticesCommand(id, verts, threshold);
        });
        if (Document.Undo.LastCommand is CompoundCommand cc) total = cc.Items.OfType<MergeVerticesCommand>().Sum(c => c.MergedCount);
        else if (Document.Undo.LastCommand is MergeVerticesCommand m) total = m.MergedCount;
        HelpLine.Text = total > 0 ? $"Merged {total} vertex pair(s)." : "No vertices within threshold.";
    }

    private void BevelSelection()
    {
        var o = Options("mesh.bevel");
        float dist = o.Float("distance"); int segments = Math.Max(1, o.Int("segments", 1));
        var mode = Document.Selection.Mode;
        ForEachComponentNode("Bevel", mode, (id, comps) =>
        {
            var mesh = Document.Find(id)?.Mesh; if (mesh == null) return null;
            var edges = mode == SelectMode.Edge ? comps.Edges.ToArray() : SelectionOps.Convert(mesh, comps, mode, SelectMode.Edge).ToArray();
            return new MeshOpCommand("Bevel", id, new HistoryParams(HistoryParam.F("Distance", dist, 0.0001f, 1000f, 0.001f), HistoryParam.I("Segments", segments, 1, 16)),
                (m, p) => { var faces = MeshOps.BevelEdges(m, edges, p.Float("Distance"), p.Int("Segments")); return (faces.Count > 0, SelectMode.Face, faces); });
        });
        HelpLine.Text = $"Bevel: distance {dist:0.###}, {segments} segment(s).";
    }

    private void AddDivisionsSelection()
    {
        var o = Options("mesh.addDivisions");
        var sel = Document.Selection;
        if (sel.Mode == SelectMode.Edge)
        {
            ForEachMeshTarget("Add Divisions", SelectMode.Edge, (id, ids) => ParamOp("Add Divisions", id, HistoryParam.I("Levels", o.Int("edgeLevels"), 1, 32),
                (m, p) => { var nv = MeshOps.DivideEdges(m, ids, p.Int("Levels")); return (nv.Count > 0, SelectMode.Vertex, nv); }));
            return;
        }
        bool linear = o.Int("method") == 1;
        var mode = o.Int("mode") == 1 ? MeshOps.DivisionMode.Triangles : MeshOps.DivisionMode.Quads;
        ForEachMeshTarget("Add Divisions", SelectMode.Face, (id, ids) => linear
            ? new MeshOpCommand("Add Divisions", id, new HistoryParams(HistoryParam.I("U", o.Int("u"), 1, 32), HistoryParam.I("V", o.Int("v"), 1, 32)), (m, p) => { var nf = MeshOps.AddDivisionsLinear(m, ids, p.Int("U"), p.Int("V")); return (nf.Count > 0, SelectMode.Face, nf); })
            : ParamOp("Add Divisions", id, HistoryParam.I("Levels", o.Int("levels"), 1, 4), (m, p) => { var nf = MeshOps.AddDivisions(m, ids, p.Int("Levels"), mode); return (nf.Count > 0, SelectMode.Face, nf); }));
    }

    private void CircularizeSelection()
    {
        var o = Options("mesh.circularize");
        var sel = Document.Selection;
        bool evenly = o.Bool("evenly");
        ForEachMeshTarget("Circularize", SelectMode.Vertex, (id, ids) =>
        {
            var mesh = Document.Find(id)!.Mesh!;
            if (sel.Mode == SelectMode.Face)
            {
                var border = SelectionOps.BoundaryEdgesOfFaces(mesh, sel.GetComponents(id).Faces);
                var bv = new HashSet<int>(); foreach (int e in border) { var (a, b) = mesh.EdgeVertices(e); bv.Add(a); bv.Add(b); }
                if (bv.Count >= 3) ids = bv;
            }
            var verts = ids.ToArray();
            return ParamOp("Circularize", id, HistoryParam.F("Radial Offset", o.Float("radial"), -0.9f, 10f, 0.01f),
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
        var o = Options("mesh.wedge");
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
        var o = Options("mesh.mirror");
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
        var o = Options("mesh.symmetry");
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
