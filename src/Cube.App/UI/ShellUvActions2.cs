using System.Numerics;
using Cube.Core.Commands;
using Cube.Core.Mesh;
using Cube.Core.Scene;
using Cube.Core.Selection;
using Cube.Core.Uv;
using NVec2 = System.Numerics.Vector2;

namespace Cube.App.UI;

/// <summary>
/// Maya UV 메뉴 / UV Editor 메뉴(Edit·Create·Select·Cut/Sew·Modify·Tools·View·Image·Textures·UV Sets) 액션(v0.0.9).
/// 대상: UV 점 연산은 현재 선택을 UV 점으로 바꾼 집합(오브젝트 모드 = 전체), 셸 연산은 그 점들이 속한 셸, 면 연산은 UvTargetNodes().
/// </summary>
public partial class Shell
{
    /// <summary>Copy UVs(face)가 저장한 면 하나의 코너 UV 목록(면의 하프에지 순서). Paste UVs가 같은 변 수의 면에 붙인다.</summary>
    private List<Vector2>? _uvClipboard;

    /// <summary>현재 선택을 UV 점 집합으로(캔버스와 같은 규칙).</summary>
    /// <remarks>
    /// 규칙: 오브젝트 모드 = 선택된 오브젝트면 모든 UV 점, UV 모드 = 유효한 선택 UV 점, 정점 모드 = 선택 정점에 속한 모든 UV 점,
    /// 엣지 모드 = 엣지 양쪽 하프에지의 끝 UV 점, 면 모드 = 면의 모든 코너 UV 점. 이 노드에 컴포넌트 선택이 없으면 빈 집합.
    /// </remarks>
    /// <param name="node">대상 메시 노드.</param>
    /// <param name="topo">그 메시로 만든(또는 캔버스가 캐시한) UV 토폴로지. 반환 ID는 topo.Points 인덱스.</param>
    public HashSet<int> UvPointSelection(SceneNode node, UvTopology topo)
    {
        var sel = Document.Selection; var m = node.Mesh!;
        var set = new HashSet<int>();
        if (!sel.Components.TryGetValue(node.Id, out var comps) || sel.Mode == SelectMode.Object)
        {
            if (sel.Mode == SelectMode.Object && sel.IsObjectSelected(node.Id)) for (int i = 0; i < topo.Points.Count; i++) set.Add(i);
            return set;
        }
        switch (sel.Mode)
        {
            case SelectMode.Uv: set.UnionWith(comps.Uvs.Where(i => i < topo.Points.Count)); break;
            case SelectMode.Vertex: for (int i = 0; i < topo.Points.Count; i++) if (comps.Verts.Contains(topo.Points[i].Vertex)) set.Add(i); break;
            case SelectMode.Edge:
                foreach (int e in comps.Edges) AddEdgeUvPoints(m, topo, e, set);
                break;
            case SelectMode.Face:
                foreach (int f in comps.Faces)
                {
                    if (f >= m.FaceCount || !m.Faces[f].Alive) continue;
                    // 면의 하프에지 고리를 한 바퀴 돌며 코너마다 UV 점을 추가
                    int start = m.Faces[f].HalfEdge, he = start;
                    do { set.Add(topo.HeToPoint[he]); he = m.Hes[he].Next; } while (he != start);
                }
                break;
        }
        return set;
    }

    /// <summary>엣지 양쪽 하프에지의 두 끝 UV 점을 set에 넣는다.</summary>
    internal static void AddEdgeUvPoints(PolyMesh m, UvTopology topo, int e, HashSet<int> set)
    {
        if (e < 0 || e >= m.EdgeCount || !m.Edges[e].Alive) return;
        var ed = m.Edges[e];
        if (ed.He0 >= 0) { set.Add(topo.HeToPoint[ed.He0]); set.Add(topo.HeToPoint[m.Hes[ed.He0].Next]); }
        if (ed.He1 >= 0) { set.Add(topo.HeToPoint[ed.He1]); set.Add(topo.HeToPoint[m.Hes[ed.He1].Next]); }
    }

    /// <summary>
    /// 선택에서 UV 점이 하나라도 나오는지(UV 점 연산의 canExecute). 캔버스가 캐시한 토폴로지가 있으면 재사용해 비용을 줄인다.
    /// </summary>
    private bool HasUvPoints()
    {
        foreach (var n in UvNodes()) { var topo = UvEditorWindow?.Canvas.Topo(n) ?? UvTopology.Build(n.Mesh!); if (UvPointSelection(n, topo).Count > 0) return true; }
        return false;
    }

    /// <summary>UV 작업 후보 노드: 선택 오브젝트 ∪ 컴포넌트가 선택된 노드 중 메시가 있는 것.</summary>
    private IEnumerable<SceneNode> UvNodes()
    {
        var sel = Document.Selection;
        var ids = new HashSet<NodeId>(sel.Objects);
        foreach (var (id, c) in sel.Components) if (!c.IsEmpty) ids.Add(id);
        foreach (var id in ids) { var n = Document.Find(id); if (n?.Mesh != null) yield return n; }
    }

    /// <summary>노드마다 (메시, 위상, 선택 UV 점)으로 UV 편집 명령을 만든다. 한 Undo 스텝.</summary>
    private void ForEachUvPoints(string name, Action<PolyMesh, UvTopology, HashSet<int>> op, bool requirePoints = true)
    {
        // ① 실행 전에 대상 노드를 고른다(requirePoints면 선택 UV 점이 없는 노드 제외).
        // ② 명령 람다 안에서는 토폴로지와 선택 UV 점을 다시 계산한다 — Redo 때도 같은 규칙으로 현재 메시에 적용되도록.
        var targets = new List<(SceneNode node, HashSet<int> pts)>();
        foreach (var n in UvNodes())
        {
            var topo = UvTopology.Build(n.Mesh!);
            var pts = UvPointSelection(n, topo);
            if (pts.Count == 0 && requirePoints) continue;
            targets.Add((n, pts));
        }
        if (targets.Count == 0) { HelpLine.Text = $"{name}: nothing selected."; return; }
        using (Document.Undo.BeginGroup(name))
            foreach (var (node, _) in targets)
                Document.Undo.Push(new UvEditCommand(name, node.Id, m => { var topo = UvTopology.Build(m); op(m, topo, UvPointSelection(node, topo)); }));
        UvEditorWindow?.Canvas.Invalidate();
    }

    /// <summary>ForEachUvPoints의 셸 버전: 선택 UV 점이 속한 셸(섬) 번호 목록을 op에 넘긴다(Unfold/Layout/Stack 등 셸 단위 연산).</summary>
    private void ForEachUvShells(string name, Action<PolyMesh, UvTopology, List<int>> op)
        => ForEachUvPoints(name, (m, topo, pts) => op(m, topo, UvOps.ShellsOf(topo, pts).ToList()));

    /// <summary>
    /// Maya UV 메뉴 / UV Editor 메뉴 액션 등록(그룹 순서): Create(자동·카메라·최적 평면·Contour Stretch 투영),
    /// Cut/Sew(셸 만들기·Split·Merge·Move and Sew·Delete·3D Cut/Sew 툴), Modify(정렬·분배·회전·정규화·Unitize·Cycle·그리드 맞춤·대칭,
    /// 곧게 펴기·경계 매핑·Optimize, 셸 Layout·방향·랜덤·Stack·Distribute·Gather·Snap·Flip Reversed),
    /// Edit(복사/붙여넣기, 핀), Select(반전·앞/뒷면·겹침·미매핑·텍스처 경계·최단 경로·루프 Grow/Shrink·포함/연결 면),
    /// View/Image/Textures(캔버스 표시 토글), Tools(캔버스 툴·브러시 옵션), UV Sets(세트 편집기·생성·복사·삭제·전환).
    /// </summary>
    private void RegisterUvActions2()
    {
        var doc = Document; var sel = doc.Selection;
        // HasTargets = 면 기반 대상 존재, EditorOpen = UV 편집기가 열려 있음(캔버스 표시/툴 액션의 조건)
        bool HasTargets() => UvTargetNodes().Any();
        bool EditorOpen() => UvEditorWindow?.IsOpen ?? false;

        // ---------------------------------------------------------------- Create
        RegisterOptionPair("uv.automatic", "Automatic Mapping", new OptionSpec("Automatic Mapping Options",
            v => { v.Set("planes", 3); v.Set("fewer", 1f); v.Set("spacing", 0.01f); },
            new[] { OptionField.E("planes", "Planes", "3", "4", "5", "6", "8", "12"), OptionField.B("fewer", "Optimize for fewer pieces"), OptionField.F("spacing", "Shell spacing", 0, 0.2, 0.001) }, "Project"), () =>
        {
            var o = Options("uv.automatic");
            // 옵션 값(0~5)은 평면 수 목록의 인덱스
            int planes = new[] { 3, 4, 5, 6, 8, 12 }[Math.Clamp(o.Int("planes"), 0, 5)];
            Project("Automatic", (m, f) => UvOps.AutomaticProject(m, f, planes, o.Bool("fewer"), o.Float("spacing")));
        }, HasTargets);
        Actions.Register("uv.cameraBased", "Camera-Based Mapping", () =>
        {
            // 활성 뷰포트 카메라의 오른쪽/위쪽 벡터(월드)를 노드 로컬 축으로 바꿔 화면 평면에 투영한다.
            // 방향 벡터는 월드 행렬의 전치로 변환한다(회전 성분의 역 = 전치이므로 월드 방향 → 로컬 방향; 스케일 영향은 정규화로 제거).
            var proj = Viewport.Picker.Projection();
            var right = proj.Right; var up = NVec3Cross(right, proj.Forward);
            var targets = UvTargetNodes().ToList();
            using (Document.Undo.BeginGroup("Camera-Based Mapping"))
                foreach (var (node, faces) in targets)
                {
                    Matrix4x4.Invert(node.WorldMatrix, out var inv);
                    var r = System.Numerics.Vector3.Normalize(System.Numerics.Vector3.TransformNormal(right, Matrix4x4.Transpose(node.WorldMatrix)));
                    var u = System.Numerics.Vector3.Normalize(System.Numerics.Vector3.TransformNormal(up, Matrix4x4.Transpose(node.WorldMatrix)));
                    Document.Undo.Push(new UvEditCommand("Camera-Based", node.Id, m => UvOps.CameraProject(m, faces, r, u)));
                }
            UvEditorWindow?.Canvas.Invalidate();
        }, canExecute: HasTargets, repeatable: true);
        Actions.Register("uv.bestPlane", "Best Plane Texturing (faces + plane vertices)", () =>
        {
            var targets = UvTargetNodes().ToList();
            using (Document.Undo.BeginGroup("Best Plane"))
                foreach (var (node, faces) in targets)
                {
                    // 선택 정점이 있으면 그 정점들이 정의하는 평면으로, 없으면 면들의 최적 평면으로 투영
                    var verts = sel.Components.TryGetValue(node.Id, out var c) ? c.Verts.ToArray() : Array.Empty<int>();
                    Document.Undo.Push(new UvEditCommand("Best Plane", node.Id, m => UvOps.BestPlaneProject(m, faces, verts)));
                }
            UvEditorWindow?.Canvas.Invalidate();
            HelpLine.Text = "Best Plane: select faces, then (vertex mode) the vertices that define the plane, then run again for an exact plane.";
        }, canExecute: HasTargets, repeatable: true);
        Actions.Register("uv.contourStretch", "Contour Stretch Mapping", () => Project("Contour Stretch", (m, f) => UvOps.ContourStretch(m, f)), canExecute: HasTargets, repeatable: true);

        // ---------------------------------------------------------------- Cut / Sew
        Actions.Register("uv.createShellGrid", "Create UV Shell (Grid)", () => Project("Create UV Shell (Grid)", (m, f) => UvOps.CreateShellGrid(m, f)), canExecute: HasTargets, repeatable: true);
        Actions.Register("uv.createShell", "Create UV Shell", () => Project("Create UV Shell", (m, f) => UvOps.CreateUvShell(m, f)), canExecute: () => sel.IsComponentMode && HasTargets(), repeatable: true);
        Actions.Register("uv.split", "Split UVs", () => ForEachUvPoints("Split UVs", (m, t, p) => UvOps.SplitUvs(m, t, p)), canExecute: () => sel.IsComponentMode && HasUvPoints(), repeatable: true);
        RegisterOptionPair("uv.merge", "Merge UVs", new OptionSpec("Merge UVs Options", v => v.Set("threshold", 0.001f), new[] { OptionField.F("threshold", "Distance threshold", 0, 1, 0.0001) }, "Merge"),
            () => { float t = Options("uv.merge").Float("threshold"); ForEachUvPoints("Merge UVs", (m, tp, p) => UvOps.MergeUvs(m, tp, p, t)); }, HasUvPoints);
        Actions.Register("uv.moveAndSew", "Move and Sew UV Edges", MoveAndSew, canExecute: () => sel.IsComponentMode && sel.NodesWithComponents(sel.Mode).Any(), repeatable: true);
        // 면 모드 = 선택 면, UV 모드 = 선택 UV 점이 모두 덮는 면(TargetFaces 규칙)의 UV를 지운다
        Actions.Register("uv.deleteUvs", "Delete UVs", () => Project("Delete UVs", (m, f) => UvOps.DeleteUvs(m, f)), canExecute: () => sel.Mode is SelectMode.Face or SelectMode.Uv && sel.NodesWithComponents(sel.Mode).Any() && HasTargets(), repeatable: true);
        Actions.Register("uv.cutSewTool", "3D Cut and Sew UV Tool", () => Tools.SetTool("cutSewUv"), isChecked: () => Tools.Current?.Id == "cutSewUv");

        // ---------------------------------------------------------------- Modify: align / distribute / rotate
        foreach (var (id, label, mode) in new[] { ("uv.alignMinU", "Align Min U", UvOps.AlignMode.MinU), ("uv.alignMaxU", "Align Max U", UvOps.AlignMode.MaxU), ("uv.alignMinV", "Align Min V", UvOps.AlignMode.MinV), ("uv.alignMaxV", "Align Max V", UvOps.AlignMode.MaxV), ("uv.alignCenterU", "Align Center U", UvOps.AlignMode.CenterU), ("uv.alignCenterV", "Align Center V", UvOps.AlignMode.CenterV) })
        {
            // 루프 변수를 지역 변수로 복사해 람다 캡처 문제를 피한다
            var am = mode; string lb = label;
            Actions.Register(id, label, () => ForEachUvPoints(lb, (m, t, p) => UvOps.Align(m, t, p, am)), canExecute: HasUvPoints, repeatable: true);
        }
        Actions.Register("uv.linearAlign", "Linear Align", () => ForEachUvPoints("Linear Align", (m, t, p) => UvOps.LinearAlign(m, t, p)), canExecute: HasUvPoints, repeatable: true);
        Actions.Register("uv.distributeU", "Distribute UVs (U)", () => ForEachUvPoints("Distribute U", (m, t, p) => UvOps.Distribute(m, t, p, true)), canExecute: HasUvPoints, repeatable: true);
        Actions.Register("uv.distributeV", "Distribute UVs (V)", () => ForEachUvPoints("Distribute V", (m, t, p) => UvOps.Distribute(m, t, p, false)), canExecute: HasUvPoints, repeatable: true);
        RegisterOptionPair("uv.rotate", "Rotate UVs", new OptionSpec("Rotate UVs Options", v => v.Set("angle", 90f), new[] { OptionField.F("angle", "Angle (deg, CCW)", -360, 360, 1) }, "Rotate"),
            () => { float a = Options("uv.rotate").Float("angle"); ForEachUvPoints("Rotate UVs", (m, t, p) => UvOps.Rotate(m, t, p, a)); }, HasUvPoints);
        Actions.Register("uv.rotateCw", "Rotate UVs 90° CW", () => ForEachUvPoints("Rotate UVs", (m, t, p) => UvOps.Rotate(m, t, p, -90f)), canExecute: HasUvPoints, repeatable: true);
        Actions.Register("uv.rotateCcw", "Rotate UVs 90° CCW", () => ForEachUvPoints("Rotate UVs", (m, t, p) => UvOps.Rotate(m, t, p, 90f)), canExecute: HasUvPoints, repeatable: true);
        RegisterOptionPair("uv.normalize", "Normalize", new OptionSpec("Normalize Options", v => { v.Set("aspect", 1f); v.Set("collect", 1f); }, new[] { OptionField.B("aspect", "Preserve aspect ratio"), OptionField.B("collect", "Collectively (as one group)") }, "Normalize"),
            () => { var o = Options("uv.normalize"); ForEachUvPoints("Normalize", (m, t, p) => UvOps.Normalize(m, t, p, o.Bool("aspect"), o.Bool("collect"))); }, HasUvPoints);
        Actions.Register("uv.unitize", "Unitize", () => Project("Unitize", (m, f) => UvOps.Unitize(m, f)), canExecute: HasTargets, repeatable: true);
        Actions.Register("uv.cycle", "Cycle", () => Project("Cycle", (m, f) => UvOps.Cycle(m, f)), canExecute: () => sel.Mode == SelectMode.Face && HasTargets(), repeatable: true);
        RegisterOptionPair("uv.matchGrid", "Match Grid", new OptionSpec("Match Grid Options", v => v.Set("size", 0.125f), new[] { OptionField.F("size", "Grid size", 0.0001, 1, 0.001) }, "Match"),
            () => { float g = Options("uv.matchGrid").Float("size"); ForEachUvPoints("Match Grid", (m, t, p) => UvOps.MatchGrid(m, t, p, g)); }, HasUvPoints);
        Actions.Register("uv.matchUvs", "Match UVs", () => ForEachUvPoints("Match UVs", (m, t, p) => UvOps.MatchUvs(m, t, p)), canExecute: HasUvPoints, repeatable: true);
        RegisterOptionPair("uv.symmetrize", "Symmetrize UVs", new OptionSpec("Symmetrize UVs Options", v => { v.Set("axis", 0); v.Set("position", 0.5f); v.Set("tolerance", 0.02f); }, new[] { OptionField.E("axis", "Mirror axis", "U", "V"), OptionField.F("position", "Mirror axis position", -10, 10, 0.001), OptionField.F("tolerance", "Tolerance", 0.0001, 1, 0.001) }, "Symmetrize"),
            () => { var o = Options("uv.symmetrize"); ForEachUvPoints("Symmetrize UVs", (m, t, p) => UvOps.SymmetrizeUvs(m, t, p, o.Int("axis") == 0, o.Float("position"), o.Float("tolerance"))); }, HasUvPoints);

        // ---------------------------------------------------------------- Modify: straighten / border / optimize
        RegisterOptionPair("uv.straighten", "Straighten UVs", new OptionSpec("Straighten UVs Options", v => { v.Set("angle", 30f); v.Set("u", 1f); v.Set("v", 1f); }, new[] { OptionField.F("angle", "Max angle (deg)", 0, 90, 1), OptionField.B("u", "Along U"), OptionField.B("v", "Along V") }, "Straighten"),
            () => { var o = Options("uv.straighten"); ForEachUvPoints("Straighten UVs", (m, t, p) => UvOps.StraightenUvs(m, t, p, o.Float("angle"), o.Bool("u"), o.Bool("v"), 30)); }, HasUvPoints);
        Actions.Register("uv.straightenBorder", "Straighten Border", () => ForEachUvShells("Straighten Border", (m, t, shells) => { foreach (int s in shells) UvOps.StraightenBorder(m, t, s); }), canExecute: HasUvPoints, repeatable: true);
        // 선택 엣지(노드별)를 기준으로 셸을 곧게 편다(엣지 모드 전용)
        Actions.Register("uv.straightenShell", "Straighten Shell (selected edges)", () =>
        {
            using (Document.Undo.BeginGroup("Straighten Shell"))
                foreach (var id in sel.NodesWithComponents(SelectMode.Edge).ToArray())
                {
                    var edges = sel.GetComponents(id).Edges.ToArray();
                    Document.Undo.Push(new UvEditCommand("Straighten Shell", id, m => UvOps.StraightenShell(m, UvTopology.Build(m), edges)));
                }
            UvEditorWindow?.Canvas.Invalidate();
        }, canExecute: () => sel.Mode == SelectMode.Edge && sel.NodesWithComponents(SelectMode.Edge).Any(), repeatable: true);
        Actions.Register("uv.mapBorderSquare", "Map Border (Square)", () => MapBorder(true), canExecute: HasUvPoints, repeatable: true);
        Actions.Register("uv.mapBorderCircle", "Map Border (Circle)", () => MapBorder(false), canExecute: HasUvPoints, repeatable: true);
        Actions.Register("uv.optimize", "Optimize", () => ForEachUvShells("Optimize", (m, t, shells) => UvOps.Optimize(m, t, shells)), canExecute: HasUvPoints, repeatable: true);

        // ---------------------------------------------------------------- Modify: shells
        RegisterOptionPair("uv.layout", "Layout", new OptionSpec("Layout UVs Options", v => { v.Set("spacing", 0.01f); v.Set("rotate", 0f); v.Set("tileU", 0); v.Set("tileV", 0); }, new[] { OptionField.F("spacing", "Shell padding", 0, 0.2, 0.001), OptionField.B("rotate", "Rotate shells to fit (upright)"), OptionField.I("tileU", "Target tile U", 0, 9), OptionField.I("tileV", "Target tile V", 0, 9) }, "Layout"), () =>
        {
            var o = Options("uv.layout");
            ForEachUvShells("Layout", (m, t, shells) => UvOps.Layout(m, t, shells, o.Float("spacing"), o.Bool("rotate"), new NVec2(o.Int("tileU"), o.Int("tileV")), 1f));
        }, HasUvPoints);
        Actions.Register("uv.orientShells", "Orient Shells", () => ForEachUvShells("Orient Shells", (m, t, shells) => UvOps.OrientShells(m, t, shells)), canExecute: HasUvPoints, repeatable: true);
        // 노드마다 첫 선택 엣지가 U 또는 V 축에 맞도록 그 셸을 회전
        Actions.Register("uv.orientToEdge", "Orient Shell to Edges", () =>
        {
            using (Document.Undo.BeginGroup("Orient Shell to Edges"))
                foreach (var id in sel.NodesWithComponents(SelectMode.Edge).ToArray())
                {
                    int e = sel.GetComponents(id).Edges.FirstOrDefault(-1); if (e < 0) continue;
                    Document.Undo.Push(new UvEditCommand("Orient Shell to Edges", id, m => UvOps.OrientShellToEdge(m, UvTopology.Build(m), e)));
                }
            UvEditorWindow?.Canvas.Invalidate();
        }, canExecute: () => sel.Mode == SelectMode.Edge && sel.NodesWithComponents(SelectMode.Edge).Any(), repeatable: true);
        RegisterOptionPair("uv.randomizeShells", "Randomize Shells", new OptionSpec("Randomize Shells Options", v => { v.Set("translate", 0.1f); v.Set("rotate", 30f); v.Set("scale", 0.1f); v.Set("seed", 1); }, new[] { OptionField.F("translate", "Translate (max)", 0, 2, 0.01), OptionField.F("rotate", "Rotate (max deg)", 0, 180, 1), OptionField.F("scale", "Scale (max ±)", 0, 1, 0.01), OptionField.I("seed", "Seed", 0, 9999) }, "Randomize"),
            () => { var o = Options("uv.randomizeShells"); ForEachUvShells("Randomize Shells", (m, t, s) => UvOps.RandomizeShells(m, t, s, o.Float("translate"), o.Float("rotate"), o.Float("scale"), o.Int("seed"))); }, HasUvPoints);
        Actions.Register("uv.stackShells", "Stack Shells", () => ForEachUvShells("Stack Shells", (m, t, s) => UvOps.StackShells(m, t, s)), canExecute: HasUvPoints, repeatable: true);
        Actions.Register("uv.stackSimilar", "Stack Similar Shells", () => ForEachUvShells("Stack Similar Shells", (m, t, s) => UvOps.StackSimilarShells(m, t, s)), canExecute: HasUvPoints, repeatable: true);
        Actions.Register("uv.unstackShells", "Unstack Shells", () => ForEachUvShells("Unstack Shells", (m, t, s) => UvOps.UnstackShells(m, t, s)), canExecute: HasUvPoints, repeatable: true);
        Actions.Register("uv.distributeShellsU", "Distribute Shells (U)", () => ForEachUvShells("Distribute Shells", (m, t, s) => UvOps.DistributeShells(m, t, s, true, 0.02f)), canExecute: HasUvPoints, repeatable: true);
        Actions.Register("uv.distributeShellsV", "Distribute Shells (V)", () => ForEachUvShells("Distribute Shells", (m, t, s) => UvOps.DistributeShells(m, t, s, false, 0.02f)), canExecute: HasUvPoints, repeatable: true);
        Actions.Register("uv.gatherShells", "Gather Shells", () => ForEachUvShells("Gather Shells", (m, t, s) => UvOps.GatherShells(m, t, s)), canExecute: HasUvPoints, repeatable: true);
        Actions.Register("uv.snapTogether", "Snap Together (two UVs)", () => ForEachUvPoints("Snap Together", (m, t, p) => { var l = p.ToList(); if (l.Count >= 2) UvOps.SnapTogether(m, t, l[0], l[1]); }), canExecute: () => sel.Mode == SelectMode.Uv && HasUvPoints(), repeatable: true);
        Actions.Register("uv.snapAndStack", "Snap and Stack Shells", () => ForEachUvShells("Snap and Stack", (m, t, s) => UvOps.SnapAndStack(m, t, s)), canExecute: HasUvPoints, repeatable: true);
        Actions.Register("uv.flipReversed", "Flip Reversed UV Shells", () => ForEachUvShells("Flip Reversed", (m, t, s) => UvOps.FlipReversedShells(m, t, s)), canExecute: HasUvPoints, repeatable: true);

        // ---------------------------------------------------------------- Edit: copy/paste, pins
        // 첫 번째로 찾은 선택 면의 UV만 클립보드에 복사(Maya Copy UVs와 같이 한 면 단위)
        Actions.Register("uv.copy", "Copy UVs (face)", () =>
        {
            foreach (var id in sel.NodesWithComponents(SelectMode.Face))
            {
                var m = doc.Find(id)?.Mesh; int f = sel.GetComponents(id).Faces.FirstOrDefault(-1);
                if (m != null && f >= 0) { _uvClipboard = UvOps.CopyFaceUvs(m, f); HelpLine.Text = $"Copied UVs of a {_uvClipboard.Count}-sided face."; return; }
            }
        }, canExecute: () => sel.Mode == SelectMode.Face && sel.NodesWithComponents(SelectMode.Face).Any());
        Actions.Register("uv.paste", "Paste UVs (face)", () =>
        {
            if (_uvClipboard == null) return;
            // 클립보드를 캡처해(이후 복사로 바뀌어도 Redo 결과 유지) 선택 면마다 붙인다. 변 수가 다르면 건너뛰고 성공 면 수를 센다.
            var clip = _uvClipboard;
            int ok = 0;
            using (Document.Undo.BeginGroup("Paste UVs"))
                foreach (var id in sel.NodesWithComponents(SelectMode.Face).ToArray())
                {
                    var faces = sel.GetComponents(id).Faces.ToArray();
                    Document.Undo.Push(new UvEditCommand("Paste UVs", id, m => { foreach (int f in faces) if (UvOps.PasteFaceUvs(m, f, clip)) ok++; }));
                }
            UvEditorWindow?.Canvas.Invalidate();
            HelpLine.Text = $"Pasted UVs onto {ok} face(s) (faces must have the same vertex count).";
        }, canExecute: () => _uvClipboard != null && sel.Mode == SelectMode.Face && sel.NodesWithComponents(SelectMode.Face).Any(), repeatable: true);
        Actions.Register("uv.pin", "Pin Selection", () => ForEachUvPoints("Pin UVs", (m, t, p) => UvOps.SetPins(m, t, p, true)), canExecute: HasUvPoints, repeatable: true);
        Actions.Register("uv.unpin", "Unpin Selection", () => ForEachUvPoints("Unpin UVs", (m, t, p) => UvOps.SetPins(m, t, p, false)), canExecute: HasUvPoints, repeatable: true);
        Actions.Register("uv.invertPins", "Invert Pinning", () => ForEachUvPoints("Invert Pinning", (m, _, _) => UvOps.InvertPins(m), requirePoints: false), canExecute: () => UvNodes().Any(), repeatable: true);
        Actions.Register("uv.unpinAll", "Unpin All", () => ForEachUvPoints("Unpin All", (m, _, _) => UvOps.UnpinAll(m), requirePoints: false), canExecute: () => UvNodes().Any(), repeatable: true);

        // ---------------------------------------------------------------- Select
        // Select All(UV 편집기): 컴포넌트 모드면 대상 노드의 현재 종류 컴포넌트를 모두 선택(Maya UV Editor Select > All).
        // 셸의 select.all은 항상 오브젝트 모드로 바꿔 UV 편집기에서 UV 모드가 풀렸다. 오브젝트 모드면 select.all과 같다.
        Actions.Register("uv.selectAll", "Select All", () =>
        {
            if (!sel.IsComponentMode) { Actions.Invoke("select.all"); return; }
            RecordSelection(s =>
            {
                bool first = true;
                foreach (var n in UvNodes().ToList())
                {
                    var m = n.Mesh!;
                    IEnumerable<int> all = s.Mode switch
                    {
                        SelectMode.Uv => Enumerable.Range(0, UvTopology.Build(m).Points.Count),
                        SelectMode.Edge => Enumerable.Range(0, m.EdgeCount).Where(e => m.Edges[e].Alive),
                        SelectMode.Face => Enumerable.Range(0, m.FaceCount).Where(f => m.Faces[f].Alive),
                        _ => Enumerable.Range(0, m.VertexCount).Where(v => m.Verts[v].Alive),
                    };
                    s.SelectComponents(n.Id, s.Mode, all.ToList(), replace: first); first = false;
                }
            });
        }, canExecute: () => UvNodes().Any() || !sel.IsComponentMode);
        // 현재 모드의 컴포넌트 선택을 노드별로 반전(살아 있는 요소 중 선택되지 않은 것만 남김)
        Actions.Register("uv.selectInverse", "Select Inverse", () => RecordSelection(s =>
        {
            foreach (var n in UvNodes().ToList())
            {
                var m = n.Mesh!; var comps = s.GetComponents(n.Id);
                switch (s.Mode)
                {
                    case SelectMode.Uv: { var topo = UvTopology.Build(m); var all = Enumerable.Range(0, topo.Points.Count).Where(p => !comps.Uvs.Contains(p)).ToList(); comps.Uvs.Clear(); comps.Uvs.UnionWith(all); break; }
                    case SelectMode.Edge: { var all = Enumerable.Range(0, m.EdgeCount).Where(e => m.Edges[e].Alive && !comps.Edges.Contains(e)).ToList(); comps.Edges.Clear(); comps.Edges.UnionWith(all); break; }
                    case SelectMode.Face: { var all = Enumerable.Range(0, m.FaceCount).Where(f => m.Faces[f].Alive && !comps.Faces.Contains(f)).ToList(); comps.Faces.Clear(); comps.Faces.UnionWith(all); break; }
                    case SelectMode.Vertex: { var all = Enumerable.Range(0, m.VertexCount).Where(v => m.Verts[v].Alive && !comps.Verts.Contains(v)).ToList(); comps.Verts.Clear(); comps.Verts.UnionWith(all); break; }
                }
            }
        }), canExecute: () => sel.IsComponentMode && UvNodes().Any());
        Actions.Register("uv.selectBackFacing", "Select Back-Facing", () => SelectFaces(m => UvOps.BackFacingFaces(m, true)), canExecute: () => UvNodes().Any());
        Actions.Register("uv.selectFrontFacing", "Select Front-Facing", () => SelectFaces(m => UvOps.BackFacingFaces(m, false)), canExecute: () => UvNodes().Any());
        Actions.Register("uv.selectOverlapping", "Select Overlapping", () => SelectFaces(UvOps.OverlappingFaces), canExecute: () => UvNodes().Any());
        Actions.Register("uv.selectNonOverlapping", "Select Non-Overlapping", () => SelectFaces(m => { var ov = new HashSet<int>(UvOps.OverlappingFaces(m)); return Enumerable.Range(0, m.FaceCount).Where(f => m.Faces[f].Alive && !ov.Contains(f)).ToList(); }), canExecute: () => UvNodes().Any());
        Actions.Register("uv.selectUnmapped", "Select Unmapped Faces", () => SelectFaces(UvOps.UnmappedFaces), canExecute: () => UvNodes().Any());
        Actions.Register("uv.selectTextureBorders", "Select Texture Borders", () => RecordSelection(s =>
        {
            s.Mode = SelectMode.Uv; bool first = true;
            foreach (var n in UvNodes().ToList()) { var topo = UvTopology.Build(n.Mesh!); s.SelectComponents(n.Id, SelectMode.Uv, UvOps.TextureBorderPoints(n.Mesh!, topo), replace: first); first = false; }
        }), canExecute: () => UvNodes().Any());
        // 첫 대상 노드에서 선택한 정점(또는 UV 점의 정점) 중 처음과 마지막 사이의 최단 엣지 경로를 엣지로 선택
        Actions.Register("uv.shortestPath", "Shortest Edge Path (two vertices/UVs)", () =>
        {
            var node = UvNodes().FirstOrDefault(); if (node == null) return;
            var comps = sel.GetComponents(node.Id);
            var verts = sel.Mode == SelectMode.Vertex ? comps.Verts.ToList() : sel.Mode == SelectMode.Uv ? comps.Uvs.Select(p => UvTopology.Build(node.Mesh!).Points[p].Vertex).Distinct().ToList() : new List<int>();
            if (verts.Count < 2) { HelpLine.Text = "Shortest Edge Path: select two vertices (or UVs)."; return; }
            var path = UvOps.ShortestEdgePath(node.Mesh!, verts[0], verts[^1]);
            RecordSelection(s => { s.Mode = SelectMode.Edge; s.SelectComponents(node.Id, SelectMode.Edge, path, replace: true); });
            HelpLine.Text = $"Shortest Edge Path: {path.Count} edge(s).";
        }, canExecute: () => sel.Mode is SelectMode.Vertex or SelectMode.Uv && UvNodes().Any());
        Actions.Register("uv.growLoop", "Grow Along Loop", () => RecordSelection(s => { foreach (var id in s.NodesWithComponents(SelectMode.Edge).ToArray()) { var m = doc.Find(id)?.Mesh; if (m != null) UvOps.GrowAlongLoop(m, s.GetComponents(id).Edges); } }), canExecute: () => sel.Mode == SelectMode.Edge && sel.NodesWithComponents(SelectMode.Edge).Any());
        Actions.Register("uv.shrinkLoop", "Shrink Along Loop", () => RecordSelection(s => { foreach (var id in s.NodesWithComponents(SelectMode.Edge).ToArray()) { var m = doc.Find(id)?.Mesh; if (m != null) UvOps.ShrinkAlongLoop(m, s.GetComponents(id).Edges); } }), canExecute: () => sel.Mode == SelectMode.Edge && sel.NodesWithComponents(SelectMode.Edge).Any());
        Actions.Register("uv.containedFaces", "Contained Faces", () => SelectFacesOfPoints(all: true), canExecute: () => sel.IsComponentMode && HasUvPoints());
        Actions.Register("uv.connectedFaces", "Connected Faces", () => SelectFacesOfPoints(all: false), canExecute: () => sel.IsComponentMode && HasUvPoints());

        // ---------------------------------------------------------------- View / Image / Textures (UV Editor 표시)
        // 캔버스 표시 토글: UvCanvas의 표시 속성을 뒤집는다(편집기가 열려 있을 때만; 체크 = 현재 값, 기본값은 ?? 오른쪽)
        Actions.Register("uv.viewShaded", "Shaded Shells (front blue / back red)", () => { if (UvEditorWindow != null) { UvEditorWindow.Canvas.Shaded = !UvEditorWindow.Canvas.Shaded; } }, canExecute: EditorOpen, isChecked: () => UvEditorWindow?.Canvas.Shaded ?? false);
        Actions.Register("uv.viewDistortion", "UV Distortion (red stretched / blue compressed)", () => { if (UvEditorWindow != null) UvEditorWindow.Canvas.Distortion = !UvEditorWindow.Canvas.Distortion; }, canExecute: EditorOpen, isChecked: () => UvEditorWindow?.Canvas.Distortion ?? false);
        Actions.Register("uv.viewTextureBorders", "Texture Borders (bold)", () => { if (UvEditorWindow != null) UvEditorWindow.Canvas.TextureBorders = !UvEditorWindow.Canvas.TextureBorders; }, canExecute: EditorOpen, isChecked: () => UvEditorWindow?.Canvas.TextureBorders ?? true);
        Actions.Register("uv.viewIsolate", "Isolate Select (toggle)", () => UvEditorWindow?.Canvas.ToggleIsolate(), canExecute: EditorOpen, isChecked: () => UvEditorWindow?.Canvas.Isolated ?? false);
        Actions.Register("uv.viewStats", "UV Statistics", () => { if (UvEditorWindow != null) UvEditorWindow.Canvas.ShowStats = !UvEditorWindow.Canvas.ShowStats; }, canExecute: EditorOpen, isChecked: () => UvEditorWindow?.Canvas.ShowStats ?? false);
        Actions.Register("uv.viewGrid", "Grid Lines (toggle)", () => { if (UvEditorWindow != null) UvEditorWindow.Canvas.ShowGridLines = !UvEditorWindow.Canvas.ShowGridLines; }, canExecute: EditorOpen, isChecked: () => UvEditorWindow?.Canvas.ShowGridLines ?? true);
        Actions.Register("uv.viewTiles", "Show UV Tiles (UDIM)", () => { if (UvEditorWindow != null) UvEditorWindow.Canvas.ShowTiles = !UvEditorWindow.Canvas.ShowTiles; }, canExecute: EditorOpen, isChecked: () => UvEditorWindow?.Canvas.ShowTiles ?? false);
        Actions.Register("uv.imageDim", "Dim Image", () => { if (UvEditorWindow != null) UvEditorWindow.Canvas.DimImage = !UvEditorWindow.Canvas.DimImage; }, canExecute: EditorOpen, isChecked: () => UvEditorWindow?.Canvas.DimImage ?? true);
        Actions.Register("uv.imageUnfiltered", "Unfiltered Image (pixel edges)", () => { if (UvEditorWindow != null) UvEditorWindow.Canvas.Unfiltered = !UvEditorWindow.Canvas.Unfiltered; }, canExecute: EditorOpen, isChecked: () => UvEditorWindow?.Canvas.Unfiltered ?? false);
        Actions.Register("uv.pixelSnap", "Pixel Snap", () => { if (UvEditorWindow != null) UvEditorWindow.Canvas.PixelSnap = !UvEditorWindow.Canvas.PixelSnap; }, canExecute: EditorOpen, isChecked: () => UvEditorWindow?.Canvas.PixelSnap ?? false);
        Actions.Register("uv.snapshot", "UV Snapshot...", () => UvEditorWindow?.SaveSnapshot(), canExecute: EditorOpen);
        Actions.Register("uv.checkerSizeUp", "Checker Size +", () => UvEditorWindow?.Canvas.SetCheckerSize(UvEditorWindow.Canvas.CheckerSize * 2), canExecute: EditorOpen);
        Actions.Register("uv.checkerSizeDown", "Checker Size -", () => UvEditorWindow?.Canvas.SetCheckerSize(UvEditorWindow.Canvas.CheckerSize / 2), canExecute: EditorOpen);
        Actions.Register("uv.checkerMap", "Checker Map Background", () => { if (UvEditorWindow != null) UvEditorWindow.SetBackground(UvEditor.UvBackground.Checker); }, canExecute: EditorOpen, isChecked: () => UvEditorWindow?.Canvas.Background == UvEditor.UvBackground.Checker);

        // ---------------------------------------------------------------- Tools (UV Editor)
        foreach (var (id, label, tool) in new[] { ("uv.toolTweak", "Tweak UV Tool", UvEditor.UvCanvasTool.Tweak), ("uv.toolGrab", "Grab UV Tool", UvEditor.UvCanvasTool.Grab), ("uv.toolSmooth", "Smooth UV Tool (relax brush)", UvEditor.UvCanvasTool.Smooth), ("uv.toolPinch", "Pinch UV Tool", UvEditor.UvCanvasTool.Pinch), ("uv.toolSmear", "Smear UV Tool", UvEditor.UvCanvasTool.Smear), ("uv.toolPinBrush", "Pin UV Tool (brush)", UvEditor.UvCanvasTool.PinBrush), ("uv.toolCutSew", "Cut / Sew UV Tool (click edge, Ctrl = sew)", UvEditor.UvCanvasTool.CutSew), ("uv.toolMoveShell", "Move UV Shell Tool", UvEditor.UvCanvasTool.MoveShell) })
        {
            // 같은 툴을 다시 고르면 None(선택/변형 조작기)으로 돌아간다
            var t = tool;
            Actions.Register(id, label, () => { if (UvEditorWindow != null) UvEditorWindow.Canvas.Tool = UvEditorWindow.Canvas.Tool == t ? UvEditor.UvCanvasTool.None : t; }, canExecute: EditorOpen, isChecked: () => UvEditorWindow?.Canvas.Tool == t);
        }
        Actions.Register("uv.toolNone", "UV Select/Transform (manipulator)", () => { if (UvEditorWindow != null) UvEditorWindow.Canvas.Tool = UvEditor.UvCanvasTool.None; }, canExecute: EditorOpen, isChecked: () => UvEditorWindow?.Canvas.Tool == UvEditor.UvCanvasTool.None);
        // 브러시 옵션은 실행 액션이 없는 옵션 창(OK만): 값은 BrushOptions로 브러시 툴이 직접 읽는다
        _optionSpecs["uv.brush"] = new OptionSpec("UV Brush Options", v => { v.Set("radius", 60f); v.Set("strength", 0.5f); }, new[] { OptionField.F("radius", "Radius (px)", 5, 500, 1), OptionField.F("strength", "Strength", 0.01, 1, 0.01) }, "OK");
        Actions.Register("uv.brushOptions", "Brush Options...", () => ShowOptions("uv.brush", () => { }), canExecute: EditorOpen);

        // ---------------------------------------------------------------- UV Sets
        // UV 세트: 편집기 창, 빈 세트 생성(이름 uvSet<n>), 현재 세트 복사, 현재 세트 삭제(2개 이상일 때), 다음 세트로 전환
        Actions.Register("uv.setEditor", "UV Set Editor", ToggleUvSetEditor, isChecked: () => UvSetEditor?.IsOpen ?? false);
        _optionSpecs["uv.setCreate"] = new OptionSpec("Create Empty UV Set", v => v.Set("n", 1), new[] { OptionField.I("n", "Name suffix (uvSet<n>)", 1, 99) }, "Create");
        Actions.Register("uv.setCreate", "Create Empty UV Set...", () => ShowOptions("uv.setCreate", () => UvSetOp("Create UV Set", m => m.SwitchUvSet(m.AddUvSet("uvSet" + Options("uv.setCreate").Int("n"), false)))), canExecute: () => UvNodes().Any());
        Actions.Register("uv.setCopy", "Copy UVs to New UV Set", () => UvSetOp("Copy UV Set", m => { m.EnsureUvSets(); m.SwitchUvSet(m.AddUvSet(m.UvSets[m.CurrentUvSet].Name + "_copy", true)); }), canExecute: () => UvNodes().Any(), repeatable: true);
        Actions.Register("uv.setDelete", "Delete Current UV Set", () => UvSetOp("Delete UV Set", m => m.RemoveUvSet(m.CurrentUvSet)), canExecute: () => UvNodes().Any(n => n.Mesh!.UvSets.Count > 1));
        Actions.Register("uv.setNext", "Switch to Next UV Set", () => UvSetOp("Switch UV Set", m => { m.EnsureUvSets(); m.SwitchUvSet((m.CurrentUvSet + 1) % m.UvSets.Count); }), canExecute: () => UvNodes().Any(n => n.Mesh!.UvSets.Count > 1));
    }

    /// <summary>System.Numerics 외적 헬퍼(이 파일은 Vector3가 Godot/Numerics로 모호해 정규화된 이름을 쓴다).</summary>
    private static System.Numerics.Vector3 NVec3Cross(System.Numerics.Vector3 a, System.Numerics.Vector3 b) => System.Numerics.Vector3.Cross(a, b);

    /// <summary>
    /// Move and Sew: 선택을 엣지로 바꾼 뒤 심(Seam) 엣지만 골라, 작은 셸을 강체 변환으로 상대 셸에 맞춘 다음 꿰맨다(UvOps.MoveAndSew).
    /// </summary>
    private void MoveAndSew()
    {
        var sel = Document.Selection; var mode = sel.Mode;
        using (Document.Undo.BeginGroup("Move and Sew"))
            foreach (var id in sel.NodesWithComponents(mode).ToArray())
            {
                var mesh = Document.Find(id)?.Mesh; if (mesh == null) continue;
                var edges = UvCutTargetEdges(mesh, sel.GetComponents(id), mode).Where(e => mesh.Edges[e].Seam).ToArray();
                if (edges.Length == 0) continue;
                Document.Undo.Push(new UvEditCommand("Move and Sew", id, m => UvOps.MoveAndSew(m, UvTopology.Build(m), edges)));
            }
        UvEditorWindow?.Canvas.Invalidate();
    }

    /// <summary>
    /// Map Border: 선택 UV 점이 속한 셸마다 경계를 정사각형(square) 또는 원으로 펼치고 내부를 Optimize(80회)로 이완한다.
    /// UV 모드에서 점 하나만 선택했으면 그 점을 경계 매핑의 시작점(사각형 모서리)으로 쓴다.
    /// </summary>
    private void MapBorder(bool square)
    {
        ForEachUvPoints(square ? "Map Border (Square)" : "Map Border (Circle)", (m, t, pts) =>
        {
            foreach (int s in UvOps.ShellsOf(t, pts).ToList())
            {
                int start = Document.Selection.Mode == SelectMode.Uv && pts.Count == 1 ? pts.First() : -1;
                UvOps.MapBorder(m, t, s, square, start);
                UvOps.Optimize(m, t, new[] { s }, 80);
            }
        });
    }

    /// <summary>노드마다 pick(메시)이 고른 면을 면 모드로 선택한다(첫 노드 교체, 이후 추가; Undo 가능).</summary>
    private void SelectFaces(Func<PolyMesh, List<int>> pick)
    {
        RecordSelection(s =>
        {
            s.Mode = SelectMode.Face; bool first = true;
            foreach (var n in UvNodes().ToList()) { s.SelectComponents(n.Id, SelectMode.Face, pick(n.Mesh!), replace: first); first = false; }
        });
    }

    /// <summary>
    /// Contained Faces(all = true: 모든 코너 UV 점이 선택된 면) / Connected Faces(all = false: 하나라도 선택된 면)를 면 모드로 선택한다.
    /// 모드를 바꾸기 전에 현재 선택으로 결과를 먼저 계산한다.
    /// </summary>
    private void SelectFacesOfPoints(bool all)
    {
        var picks = new List<(NodeId, List<int>)>();
        foreach (var n in UvNodes().ToList()) { var topo = UvTopology.Build(n.Mesh!); picks.Add((n.Id, UvOps.FacesOfPoints(n.Mesh!, topo, UvPointSelection(n, topo), all))); }
        RecordSelection(s => { s.Mode = SelectMode.Face; bool first = true; foreach (var (id, faces) in picks) { s.SelectComponents(id, SelectMode.Face, faces, replace: first); first = false; } });
    }

    /// <summary>UV 세트 구조 변경 op를 대상 노드마다 UvSetsCommand로 실행하고(한 Undo 그룹) 캔버스와 UV Set Editor를 갱신한다.</summary>
    private void UvSetOp(string name, Action<PolyMesh> op)
    {
        using (Document.Undo.BeginGroup(name))
            foreach (var n in UvNodes().ToList()) Document.Undo.Push(new UvSetsCommand(name, n.Id, op));
        UvEditorWindow?.Canvas.Invalidate();
        UvSetEditor?.Refresh();
    }

    /// <summary>UV Set Editor 패널(세트 목록·이름 변경·전환). 처음 열 때 만든다.</summary>
    public UvEditor.UvSetEditorWindow? UvSetEditor { get; private set; }

    /// <summary>UV Set Editor 패널을 지연 생성하고 DockManager에 등록한다(레이아웃 복원 "uvSetEditor"에서도 호출).</summary>
    private UvEditor.UvSetEditorWindow EnsureUvSetEditor()
    {
        if (UvSetEditor == null)
        {
            UvSetEditor = new UvEditor.UvSetEditorWindow { Name = "UvSetEditor", Visible = false, PanelId = "uvSetEditor" };
            AddChild(UvSetEditor);
            UvSetEditor.Setup(this);
            UvSetEditor.Closed += RefreshShelf;
            Dock.Register(UvSetEditor);
        }
        return UvSetEditor;
    }

    /// <summary>UV Set Editor 열기/닫기 토글.</summary>
    private void ToggleUvSetEditor() => EnsureUvSetEditor().Toggle();
}
