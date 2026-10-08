using Cube.Core.Commands;
using Cube.Core.Mesh;
using Cube.Core.Scene;
using Cube.Core.Selection;
using Godot;
using NVec3 = System.Numerics.Vector3;

namespace Cube.App.UI;

/// <summary>옵션 창 명세: 액션별 OptionValues 키, 제목, 기본값, 필드.</summary>
/// <param name="Title">옵션 창 제목(예: "Bevel Options").</param>
/// <param name="Defaults">값이 처음 필요할 때 OptionValues에 기본값을 채우는 함수(Options(id)가 한 번만 호출, "_init" 표시).</param>
/// <param name="Fields">창에 표시할 필드 목록(키·라벨·종류·범위·툴팁). Action Popup도 같은 필드로 UI를 만든다.</param>
/// <param name="OkText">확인 버튼 글자(기본 "Apply").</param>
public sealed record OptionSpec(string Title, Action<OptionValues> Defaults, OptionField[] Fields, string OkText = "Apply");

/// <summary>
/// Maya Mesh / Edit Mesh / Mesh Tools / Mesh Display 메뉴의 모델링 액션.
/// 옵션이 있는 명령은 <see cref="RegisterOptionPair"/>로 "<id>"(옵션 창, 라벨 "X Options...")와 "<id>Apply"(마지막 옵션으로 즉시 실행, 라벨 "X") 두 액션을 가진다.
/// 메뉴/파이/셸프는 Apply 쪽을 실행 항목으로 쓰고 메뉴에만 Options... 항목을 덧붙인다(MenuBuilder.Op). 모든 편집은 MeshOpCommand(Undo + 구성 이력).
/// </summary>
public partial class Shell
{
    /// <summary>옵션 키(액션 ID) → 마지막으로 쓴 옵션 값. 세션 동안만 기억한다(파일에 저장하지 않음).</summary>
    private readonly Dictionary<string, OptionValues> _options = new();
    /// <summary>옵션 키 → 옵션 창 명세. RegisterOptionPair(또는 공용 키 "mesh.symmetry"처럼 직접)로 등록된다.</summary>
    private readonly Dictionary<string, OptionSpec> _optionSpecs = new();

    /// <summary>옵션 값 객체를 가져오거나 빈 것을 만들어 둔다(기본값 채우기 없음; Options를 쓸 것).</summary>
    private OptionValues Opt(string id) { if (!_options.TryGetValue(id, out var v)) { v = new OptionValues(); _options[id] = v; } return v; }

    /// <summary>액션 id의 옵션 값(기본값이 아직 없으면 명세의 기본값을 채운다).</summary>
    public OptionValues Options(string id)
    {
        var v = Opt(id);
        if (!v.Has("_init") && _optionSpecs.TryGetValue(id, out var spec)) { spec.Defaults(v); v.Set("_init", 1f); }
        return v;
    }

    /// <summary>실행 액션 ID → 옵션 키(Action Popup용). "<id>Apply"면 id, Flip/Symmetrize 컴포넌트는 공용 "mesh.symmetry". 옵션이 없으면 null.</summary>
    public string? OptionKeyFor(string actionId)
    {
        if (actionId is "mesh.flipComponentsApply" or "mesh.symmetrizeComponentsApply") return "mesh.symmetry";
        if (actionId.EndsWith("Apply", StringComparison.Ordinal)) { var key = actionId[..^5]; if (_optionSpecs.ContainsKey(key)) return key; }
        return null;
    }

    /// <summary>옵션 키의 명세(Action Popup이 필드 UI를 만들 때 사용). 없으면 null.</summary>
    public OptionSpec? OptionSpecFor(string key) => _optionSpecs.TryGetValue(key, out var s) ? s : null;

    /// <summary>옵션 창을 띄운다(명세 등록 필요). OK면 apply.</summary>
    private void ShowOptions(string id, Action apply)
    {
        if (!_optionSpecs.TryGetValue(id, out var spec)) return;
        // 대화상자는 현재 옵션 값 객체를 직접 편집하고, 확인/취소 후 스스로 해제된다
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
        // 옵션 창 액션의 확인 콜백은 직접 apply를 부르지 않고 "<id>Apply" 액션을 Invoke한다
        // → ActionRegistry의 Invoking/Invoked 훅이 돌아 Action Popup과 Repeat Last가 정상 동작한다.
        _optionSpecs[id] = spec;
        Actions.Register(id, label + " Options...", () => ShowOptions(id, () => Actions.Invoke(id + "Apply")), canExecute: canExecute);
        Actions.Register(id + "Apply", label, apply, canExecute: canExecute, repeatable: true);
    }

    /// <summary>메시 대상이 있는지: 오브젝트 모드면 메시 오브젝트 선택, 컴포넌트 모드면 현재 모드 선택이 있어야 한다.</summary>
    private bool HasMeshSelection() { var s = Document.Selection; return s.Mode == SelectMode.Object ? s.Objects.Any(id => Document.Find(id)?.Mesh != null) : s.NodesWithComponents(s.Mode).Any(); }
    /// <summary>현재 모드가 modes 중 하나이고 그 모드의 선택 컴포넌트가 있는지.</summary>
    private bool HasComponents(params SelectMode[] modes) { var s = Document.Selection; return modes.Contains(s.Mode) && s.NodesWithComponents(s.Mode).Any(); }

    /// <summary>현재 선택을 wanted 모드의 컴포넌트 집합으로 바꿔 노드마다 명령을 만든다(오브젝트 모드 = 메시 전체). 한 Undo 스텝.</summary>
    /// <remarks>
    /// 변환 규칙: 오브젝트 모드 = 선택 메시의 살아 있는 모든 wanted 컴포넌트,
    /// UV 모드 = UV 점을 원래 정점으로 바꾼 뒤 wanted로 변환, 같은 모드 = 복사본, 다른 컴포넌트 모드 = SelectionOps.Convert.
    /// 빈 집합인 노드는 빼고, 대상이 하나도 없으면 헬프 라인에 "nothing selected"를 쓴다.
    /// make가 null을 돌려주면 그 노드는 건너뛴다. 모든 명령은 groupName 이름의 한 Undo 그룹으로 묶인다.
    /// </remarks>
    /// <param name="groupName">Undo 그룹/메시지에 쓰일 이름.</param>
    /// <param name="wanted">명령이 받을 컴포넌트 종류(Vertex/Edge/Face).</param>
    /// <param name="make">(노드 ID, 컴포넌트 ID 집합) → 명령. 집합은 이 호출 전용 복사본이라 람다가 캡처해도 안전하다.</param>
    private void ForEachMeshTarget(string groupName, SelectMode wanted, Func<NodeId, HashSet<int>, ICommand?> make)
    {
        var doc = Document; var sel = doc.Selection;
        // 대상 수집을 먼저 끝낸 뒤 명령을 실행한다(명령이 위상을 바꾸며 선택을 비웠다 복원하므로 순회 중 선택을 읽지 않기 위함)
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
    /// <param name="name">명령/히스토리 항목 이름.</param>
    /// <param name="id">대상 노드.</param>
    /// <param name="param">편집 가능한 파라미터 하나(이력 편집·Action Popup에 노출).</param>
    /// <param name="op">(메시, 파라미터) → (변경 여부, 새 선택 모드 또는 null(선택 유지), 새 선택 컴포넌트). Replay 때 다시 호출된다.</param>
    private static MeshOpCommand ParamOp(string name, NodeId id, HistoryParam param, Func<PolyMesh, HistoryParams, (bool, SelectMode?, IEnumerable<int>?)> op)
        => new(name, id, new HistoryParams(param), op);

    /// <summary>
    /// Maya 모델링 메뉴(Mesh / Edit Mesh(Components·Vertex·Edge·Face) / Mesh Display(노멀) / Mesh Tools)의 액션을 등록한다.
    /// 파라미터가 있는 명령은 RegisterOptionPair + 구성 이력 파라미터(ParamOp/HistoryParams)로 만들어
    /// 옵션 창·Action Popup·Properties History에서 다시 조정할 수 있게 한다.
    /// 각 MeshOpCommand 람다는 (변경 여부, 결과 선택 모드, 결과 컴포넌트)를 돌려줘 실행 후 선택을 새 컴포넌트로 바꾼다.
    /// </summary>
    private void RegisterMeshActions()
    {
        var doc = Document; var sel = doc.Selection;
        // 공통 canExecute 조건들: 면/엣지(또는 오브젝트 모드 메시), 아무 컴포넌트, 엣지만, 면만, 정점만
        Func<bool> faceOrEdge = () => HasComponents(SelectMode.Face, SelectMode.Edge) || sel.Mode == SelectMode.Object && HasMeshSelection();
        Func<bool> anyComps = () => HasComponents(SelectMode.Vertex, SelectMode.Edge, SelectMode.Face);
        Func<bool> edges = () => HasComponents(SelectMode.Edge);
        Func<bool> faces = () => HasComponents(SelectMode.Face);
        Func<bool> verts = () => HasComponents(SelectMode.Vertex);

        // ---------------------------------------------------------------- Mesh (기존 Smooth/Merge/Bevel도 옵션 쌍으로)
        // Smooth = Catmull-Clark(레벨 수), Merge = 거리 임계값 안의 정점 병합, Bevel = ShellBevel.cs,
        // Quadrangulate = 각도 임계값 이하인 삼각형 쌍을 쿼드로 병합, Mirror/Symmetrize = 축 평면 기준 반사(+자르기),
        // Fill Hole = 선택 경계 구멍 메우기, Triangulate = 이어 깎기(EarClipping), Cleanup = 퇴화 면 제거, Conform = 마지막 선택 표면에 감싸기
        RegisterOptionPair("mesh.smooth", "Smooth", new OptionSpec("Smooth Options", v => v.Set("levels", 1), new[] { OptionField.I("levels", "Division levels", 1, 4) }, "Smooth"), SmoothSelection,
            () => sel.Mode == SelectMode.Object && sel.Objects.Any(id => doc.Find(id)?.Mesh != null));
        RegisterOptionPair("mesh.merge", "Merge Vertices", new OptionSpec("Merge Vertices Options", v => v.Set("threshold", 0.001f), new[] { OptionField.F("threshold", "Threshold", 0, 1000, 0.0001) }, "Merge"), MergeSelectedVertices,
            () => sel.IsComponentMode && sel.NodesWithComponents(sel.Mode).Any());
        RegisterBevelActions(); // Blender식 Bevel 옵션 전체 + 대화형 툴(ShellBevel.cs)
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
            // 제거된 면 수를 람다가 누적해 헬프 라인에 보고
            int total = 0;
            ForEachMeshTarget("Cleanup", SelectMode.Face, (id, _) => new MeshOpCommand("Cleanup", id, m => { int n = MeshOps.Cleanup(m); total += n; return n > 0; }));
            HelpLine.Text = $"Cleanup: removed {total} face(s).";
        }, canExecute: HasMeshSelection, repeatable: true);
        Actions.Register("mesh.conform", "Conform (wrap to last selected)", ConformSelection, canExecute: () => sel.Objects.Count >= 2 && doc.Find(sel.ActiveObject)?.Mesh != null, repeatable: true);

        // ---------------------------------------------------------------- Edit Mesh: Components
        // Add Divisions(면 = 지수/선형 분할, 엣지 = 엣지 분할), Circularize(원형 정렬), Collapse, Connect, Detach, Merge to Center,
        // Flip/Symmetrize(컴포넌트; 공용 옵션 키 "mesh.symmetry"를 쓰므로 RegisterOptionPair 대신 직접 두 쌍을 등록)
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
        // Flip과 Symmetrize가 하나의 옵션(축/허용 오차)을 공유한다. OptionKeyFor가 두 Apply 액션을 이 키로 매핑한다.
        var symmetrySpec = new OptionSpec("Symmetry Options", SymmetryDefaults, SymmetryFields);
        _optionSpecs["mesh.symmetry"] = symmetrySpec;
        Actions.Register("mesh.flipComponents", "Flip Options...", () => ShowOptions("mesh.symmetry", () => Actions.Invoke("mesh.flipComponentsApply")), canExecute: anyComps);
        Actions.Register("mesh.flipComponentsApply", "Flip", () => FlipOrSymmetrize(flip: true), canExecute: anyComps, repeatable: true);
        Actions.Register("mesh.symmetrizeComponents", "Symmetrize Options...", () => ShowOptions("mesh.symmetry", () => Actions.Invoke("mesh.symmetrizeComponentsApply")), canExecute: anyComps);
        Actions.Register("mesh.symmetrizeComponentsApply", "Symmetrize", () => FlipOrSymmetrize(flip: false), canExecute: anyComps, repeatable: true);

        // ---------------------------------------------------------------- Edit Mesh: Vertex
        // Average Vertices = 라플라시안 평활(반복 횟수·강도), Chamfer Vertices = 정점을 작은 면으로 깎기(폭, 면 제거 옵션)
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
        // 엣지 연산: 삼각형 대각선 뒤집기, Spin Edge 앞/뒤, Offset Edge Loop(양옆에 루프 삽입), Slide Edge(루프를 옆 면 쪽으로 미끄러뜨림)
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
        // 면 연산: Duplicate(같은 메시 안에 면 복제), Extract(새 오브젝트로 분리), Poke(면 중심 정점 + 법선 오프셋 → Move 툴), Wedge(엣지 축 회전 돌출)
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
        // 노멀 연산: 평균, 방향 일치(Conform), 면 노멀로 설정, 지정 벡터로 설정, 각도 기준 소프트/하드, 잠금/해제(LockedNormals)
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
        // Mesh Tools: Crease(엣지 크리즈 단계, Catmull-Clark에서 날카롭게 유지되는 레벨 수)/Remove Crease, 대화형 툴(Multi-Cut/Target Weld/Append Polygon)
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

    /// <summary>
    /// Mesh → Smooth: 선택 메시 오브젝트마다 Catmull-Clark를 levels만큼 적용한다(히스토리 파라미터 Levels로 나중에 바꿀 수 있음).
    /// </summary>
    private void SmoothSelection()
    {
        int levels = Options("mesh.smooth").Int("levels");
        var targets = Document.Selection.Objects.Where(id => Document.Find(id)?.Mesh != null).ToArray();
        using (Document.Undo.BeginGroup("Smooth"))
            foreach (var id in targets)
                Document.Undo.Push(ParamOp("Smooth", id, HistoryParam.I("Levels", levels, 0, 4), (m, p) => { MeshOps.Smooth(m, p.Int("Levels")); return (true, null, null); }));
        HelpLine.Text = $"Smooth: {levels} level(s).";
    }

    /// <summary>
    /// Merge Vertices: 현재 모드 선택을 정점으로 변환해 임계값(threshold) 안의 정점을 병합한다(노드별 MergeVerticesCommand).
    /// 실행 후 마지막 Undo 명령(그룹이면 그 안의 Merge 명령들)의 MergedCount를 합해 병합 쌍 수를 알린다.
    /// </summary>
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

    /// <summary>
    /// Add Divisions: 엣지 모드면 선택 엣지를 Edge division levels만큼 나누고(새 정점 선택),
    /// 그 외에는 면을 지수(레벨, 쿼드/삼각형) 또는 선형(U×V) 방식으로 분할한다(새 면 선택).
    /// </summary>
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

    /// <summary>
    /// Circularize: 선택 정점(면 모드면 선택 면 영역의 경계 정점, 3개 이상일 때)을 원 위로 정렬한다.
    /// Radial Offset은 이력 파라미터, Evenly distribute는 실행 시점 값으로 고정된다.
    /// </summary>
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

    /// <summary>Collapse: 면 모드 = 면을 한 점으로, 엣지 모드 = 엣지를 한 점으로 붕괴(결과 정점 선택), 정점 모드 = Merge to Center.</summary>
    private void CollapseSelection()
    {
        var sel = Document.Selection;
        if (sel.Mode == SelectMode.Face) ForEachMeshTarget("Collapse", SelectMode.Face, (id, ids) => new MeshOpCommand("Collapse", id, m => { var r = MeshOps.CollapseFaces(m, ids); return (r.Count > 0, SelectMode.Vertex, r); }));
        else if (sel.Mode == SelectMode.Edge) ForEachMeshTarget("Collapse", SelectMode.Edge, (id, ids) => new MeshOpCommand("Collapse", id, m => { var r = MeshOps.CollapseEdges(m, ids); return (r.Count > 0, SelectMode.Vertex, r); }));
        else Actions.Invoke("mesh.mergeToCenter");
    }

    /// <summary>Connect: 엣지 모드 = 선택 엣지 중점을 이어 새 엣지, 그 외 = 같은 면의 선택 정점끼리 연결(새 엣지 선택).</summary>
    private void ConnectSelection()
    {
        var sel = Document.Selection;
        if (sel.Mode == SelectMode.Edge) ForEachMeshTarget("Connect", SelectMode.Edge, (id, ids) => new MeshOpCommand("Connect", id, m => { var ne = MeshOps.ConnectEdges(m, ids); return (ne.Count > 0, SelectMode.Edge, ne); }));
        else ForEachMeshTarget("Connect", SelectMode.Vertex, (id, ids) => new MeshOpCommand("Connect", id, m => { var ne = MeshOps.ConnectVertices(m, ids); return (ne.Count > 0, SelectMode.Edge, ne); }));
    }

    /// <summary>Detach: 면 모드 = 선택 면을 이웃과 분리(정점 복제), 그 외 = 선택 정점을 면마다 따로 복제해 떼어낸다.</summary>
    private void DetachSelection()
    {
        var sel = Document.Selection;
        if (sel.Mode == SelectMode.Face) ForEachMeshTarget("Detach", SelectMode.Face, (id, ids) => new MeshOpCommand("Detach", id, m => { var nf = MeshOps.DetachFaces(m, ids); return (nf.Count > 0, SelectMode.Face, nf); }));
        else ForEachMeshTarget("Detach", SelectMode.Vertex, (id, ids) => new MeshOpCommand("Detach", id, m => { var nv = MeshOps.DetachVertices(m, ids); return (nv.Count > 0, SelectMode.Vertex, nv.Concat(ids)); }));
    }

    /// <summary>
    /// Extract: 노드별 선택 면을 새 오브젝트("polySurface1" 계열, 같은 트랜스폼·머티리얼)로 떼어내고 원래 메시에서는 삭제한다.
    /// 모든 면을 선택한 경우는 건너뛴다(빈 메시가 되므로). 끝나면 새 오브젝트를 선택한다. 한 Undo 그룹.
    /// </summary>
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

    /// <summary>
    /// Wedge: 같은 노드에서 선택한 면들과 그 경계의 엣지 하나(힌지)를 사용해, 엣지를 축으로 Arc Angle만큼 Divisions 단계로 회전 돌출한다.
    /// 면과 엣지를 모두 가진 노드가 없으면 사용법을 안내한다.
    /// </summary>
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

    /// <summary>Mirror 옵션 필드: 축, 남길 쪽(+/-), 평면 위치(바운딩 박스 중심/오브젝트 원점/월드 원점), 병합 여부·임계값, 자르기(Symmetrize).</summary>
    private static readonly OptionField[] MirrorFields =
    {
        OptionField.E("axis", "Mirror axis", "X", "Y", "Z"), OptionField.E("direction", "Direction", "+ (keep positive side)", "- (keep negative side)"),
        OptionField.E("position", "Mirror axis position", "Bounding Box", "Object", "World"), OptionField.B("merge", "Merge vertices"), OptionField.F("threshold", "Merge threshold", 0, 10, 0.0001), OptionField.B("cut", "Cut geometry (symmetrize)"),
    };
    /// <summary>Mirror 옵션 기본값: X축, + 쪽 유지, 바운딩 박스, 병합 켬(0.001), 자르기 끔.</summary>
    private static void MirrorDefaults(OptionValues v) { v.Set("axis", 0); v.Set("direction", 0); v.Set("position", 0); v.Set("merge", 1f); v.Set("threshold", 0.001f); v.Set("cut", 0f); }

    /// <summary>
    /// Mirror / Symmetrize(cut = true): 선택 메시마다 오브젝트 공간의 반사 평면 좌표를 정하고 MeshOps.MirrorGeometry를 적용한다.
    /// 평면 위치: 0 = 메시 AABB 중심, 1 = 오브젝트 원점(0), 2 = 월드 원점을 오브젝트 공간으로 옮긴 좌표.
    /// Plane/Merge Threshold는 이력 파라미터라 나중에 조정할 수 있다.
    /// </summary>
    private void MirrorSelection(bool cut)
    {
        var o = Options("mesh.mirror");
        int axis = o.Int("axis"); bool keepPositive = o.Int("direction") == 0; int position = o.Int("position");
        // 병합을 끄면 임계값 0(병합 없음)
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

    /// <summary>Flip/Symmetrize(컴포넌트) 공용 옵션 필드: 대칭 축, 거울 짝을 찾는 허용 오차.</summary>
    private static readonly OptionField[] SymmetryFields = { OptionField.E("axis", "Symmetry axis", "X", "Y", "Z"), OptionField.F("tolerance", "Tolerance", 0.0001, 10, 0.001) };
    /// <summary>대칭 옵션 기본값: X축, 허용 오차 0.01.</summary>
    private static void SymmetryDefaults(OptionValues v) { v.Set("axis", 0); v.Set("tolerance", 0.01f); }

    /// <summary>
    /// Flip(선택 정점과 거울 짝의 위치를 맞바꿈) 또는 Symmetrize(선택 정점을 반대편 짝의 반사 위치로 맞춤).
    /// 대칭 평면은 오브젝트 공간 축 = 0. 짝을 하나라도 처리했을 때만 변경으로 기록한다.
    /// </summary>
    private void FlipOrSymmetrize(bool flip)
    {
        var o = Options("mesh.symmetry");
        int axis = o.Int("axis"); float tol = o.Float("tolerance");
        ForEachMeshTarget(flip ? "Flip" : "Symmetrize", SelectMode.Vertex, (id, ids) => new MeshOpCommand(flip ? "Flip" : "Symmetrize", id, m =>
            (flip ? MeshOps.FlipVertices(m, ids, axis, 0f, tol) : MeshOps.SymmetrizeVertices(m, ids, axis, 0f, tol)) > 0));
    }

    /// <summary>
    /// Conform: 활성(마지막 선택) 오브젝트를 대상 표면으로, 나머지 선택 메시의 모든 정점을 그 표면의 최근접점으로 옮긴다.
    /// 대상 메시는 복제해 캡처하고(이력 Replay 시 대상이 바뀌어도 결과 고정), 대상 → 소스 로컬 변환 행렬(target.World × inv(source.World))을 함께 넘긴다.
    /// </summary>
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
