using System.Numerics;
using Cube.Core.Mesh;
using Cube.Core.Uv;
using Xunit.Abstractions;

namespace Cube.Core.Tests.Uv;

/// <summary>
/// UV 연산 전수 스트레스: 여러 종류의 메시(큐브·평면·원기둥·원뿔·구·토러스·Extrude/Bevel/Boolean/Array 결과·삼각형 메시·UV 없는/겹친 메시·구멍 난 메시)에
/// 모든 UV 연산을 여러 선택(전체/절반/빈 선택)으로 돌려 예외·NaN·위상 변화·MeshValidator 오류가 없는지 확인한다.
/// </summary>
public class UvAuditStressTests
{
    /// <summary>xUnit 출력(실패 목록 보고용).</summary>
    private readonly ITestOutputHelper _out;
    /// <summary>xUnit이 출력 도우미를 주입한다.</summary>
    public UvAuditStressTests(ITestOutputHelper output) => _out = output;

    /// <summary>시험할 메시 모음(이름, 생성기). 매 연산마다 새로 만든다.</summary>
    public static IEnumerable<(string name, Func<PolyMesh> make)> Meshes()
    {
        yield return ("cube", () => MeshBuilder.Cube());
        yield return ("plane4", () => MeshBuilder.Plane(1, 1, 4, 4));
        yield return ("cylinder", () => MeshBuilder.Cylinder(0.5f, 1f, 12));
        yield return ("tube", () => MeshBuilder.Cylinder(0.5f, 1f, 12, caps: false));
        yield return ("cone", () => MeshBuilder.Cone(0.5f, 1f, 10));
        yield return ("sphere", () => MeshBuilder.Sphere(0.5f, 12, 6));
        yield return ("torus", () => MeshBuilder.Torus(0.5f, 0.2f, 12, 8));
        yield return ("extruded", () => { var m = MeshBuilder.Cube(); MeshOps.ExtrudeFaces(m, new[] { 4 }); MeshNormals.Recompute(m); return m; });
        yield return ("beveled", () => { var m = MeshBuilder.Cube(); MeshOps.BevelEdges(m, Enumerable.Range(0, m.EdgeCount), 0.1f, 2); MeshNormals.Recompute(m); return m; });
        yield return ("boolean", () =>
        {
            var r = MeshOps.Boolean(MeshBuilder.Cube(), MeshBuilder.Cube(), Matrix4x4.CreateTranslation(0.4f, 0.4f, 0.4f), BooleanOperation.Difference, out _);
            MeshNormals.Recompute(r); return r;
        });
        yield return ("array", () => { var m = MeshBuilder.Cube(); MeshOps.MakeArray(m, new ArrayOptions { Count = 3, Merge = true }); MeshNormals.Recompute(m); return m; });
        yield return ("tris", () => { var m = MeshBuilder.Sphere(0.5f, 8, 4); MeshOps.Triangulate(m, Enumerable.Range(0, m.FaceCount)); MeshNormals.Recompute(m); return m; });
        yield return ("noUv", () => { var m = MeshBuilder.Cube(); for (int h = 0; h < m.HalfEdgeCount; h++) { var he = m.Hes[h]; he.Uv0 = Vector2.Zero; m.Hes[h] = he; } return m; });
        yield return ("overlap", () => { var m = MeshBuilder.Cylinder(0.5f, 1f, 8); UvOps.PlanarProject(m, Enumerable.Range(0, m.FaceCount), Vector3.UnitY); return m; });
        yield return ("holed", () => { var m = MeshBuilder.Plane(1, 1, 3, 3); MeshOps.DeleteFaces(m, new[] { 4 }); return m; });
        yield return ("smoothed", () => { var m = MeshBuilder.Cube(); MeshOps.Smooth(m, 1); MeshNormals.Recompute(m); return m; });
    }

    /// <summary>살아 있는 면 ID 목록.</summary>
    private static List<int> AliveFaces(PolyMesh m) => Enumerable.Range(0, m.FaceCount).Where(f => m.Faces[f].Alive).ToList();
    /// <summary>살아 있는 엣지 ID 목록.</summary>
    private static List<int> AliveEdges(PolyMesh m) => Enumerable.Range(0, m.EdgeCount).Where(e => m.Edges[e].Alive).ToList();

    /// <summary>선택 변형: 전체, 앞 절반, 하나, 없음.</summary>
    private static IEnumerable<(string name, Func<List<int>, List<int>> pick)> Picks()
    {
        yield return ("all", l => l);
        yield return ("half", l => l.Take(Math.Max(1, l.Count / 2)).ToList());
        yield return ("one", l => l.Take(1).ToList());
        yield return ("none", _ => new List<int>());
    }

    /// <summary>모든 연산(이름, 실행기: 메시와 선택 변형을 받아 연산을 적용).</summary>
    private static IEnumerable<(string name, Action<PolyMesh, Func<List<int>, List<int>>> run)> Ops()
    {
        // 면 기반
        yield return ("planarBest", (m, p) => UvOps.PlanarProjectBestFit(m, p(AliveFaces(m))));
        yield return ("planarX", (m, p) => UvOps.PlanarProject(m, p(AliveFaces(m)), Vector3.UnitX));
        yield return ("planarY", (m, p) => UvOps.PlanarProject(m, p(AliveFaces(m)), Vector3.UnitY));
        yield return ("cylindrical", (m, p) => UvOps.CylindricalProject(m, p(AliveFaces(m))));
        yield return ("spherical", (m, p) => UvOps.SphericalProject(m, p(AliveFaces(m))));
        foreach (int planes in new[] { 3, 4, 5, 6, 8, 12 })
            foreach (bool fewer in new[] { true, false })
            {
                int pl = planes; bool fw = fewer;
                yield return ($"automatic{pl}{(fw ? "f" : "")}", (m, p) => UvOps.AutomaticProject(m, p(AliveFaces(m)), pl, fw, 0.01f));
            }
        yield return ("camera", (m, p) => UvOps.CameraProject(m, p(AliveFaces(m)), Vector3.Normalize(new Vector3(1, 0, -1)), Vector3.UnitY));
        yield return ("bestPlaneVerts", (m, p) => UvOps.BestPlaneProject(m, p(AliveFaces(m)), new[] { 0, 1, 2, 3 }));
        yield return ("contour", (m, p) => UvOps.ContourStretch(m, p(AliveFaces(m))));
        yield return ("shellGrid", (m, p) => UvOps.CreateShellGrid(m, p(AliveFaces(m))));
        yield return ("createShell", (m, p) => UvOps.CreateUvShell(m, p(AliveFaces(m))));
        yield return ("deleteUvs", (m, p) => UvOps.DeleteUvs(m, p(AliveFaces(m))));
        yield return ("unitize", (m, p) => UvOps.Unitize(m, p(AliveFaces(m))));
        yield return ("cycle", (m, p) => UvOps.Cycle(m, p(AliveFaces(m))));
        yield return ("cut", (m, p) => UvOps.CutEdges(m, p(AliveEdges(m))));
        yield return ("sew", (m, p) => UvOps.SewEdges(m, p(AliveEdges(m))));
        yield return ("autoWrap", (m, _) => UvOps.AutoWrap(m));
        // 점 기반
        static List<int> Pts(PolyMesh m, UvTopology t, Func<List<int>, List<int>> p) => p(Enumerable.Range(0, t.Points.Count).ToList());
        yield return ("flipU", (m, p) => { var t = UvTopology.Build(m); UvOps.Flip(m, t, Pts(m, t, p), true); });
        foreach (var am in Enum.GetValues<UvOps.AlignMode>()) { var a = am; yield return ($"align{a}", (m, p) => { var t = UvTopology.Build(m); UvOps.Align(m, t, Pts(m, t, p), a); }); }
        yield return ("linearAlign", (m, p) => { var t = UvTopology.Build(m); UvOps.LinearAlign(m, t, Pts(m, t, p)); });
        yield return ("distribute", (m, p) => { var t = UvTopology.Build(m); UvOps.Distribute(m, t, Pts(m, t, p), true); });
        yield return ("rotate", (m, p) => { var t = UvTopology.Build(m); UvOps.Rotate(m, t, Pts(m, t, p), 37f); });
        yield return ("normalize", (m, p) => { var t = UvTopology.Build(m); UvOps.Normalize(m, t, Pts(m, t, p), true, false); });
        yield return ("normalizeFree", (m, p) => { var t = UvTopology.Build(m); UvOps.Normalize(m, t, Pts(m, t, p), false, true); });
        yield return ("matchGrid", (m, p) => { var t = UvTopology.Build(m); UvOps.MatchGrid(m, t, Pts(m, t, p), 0.125f); });
        yield return ("matchUvs", (m, p) => { var t = UvTopology.Build(m); UvOps.MatchUvs(m, t, Pts(m, t, p)); });
        yield return ("symmetrize", (m, p) => { var t = UvTopology.Build(m); UvOps.SymmetrizeUvs(m, t, Pts(m, t, p), true, 0.5f, 0.05f); });
        yield return ("straighten", (m, p) => { var t = UvTopology.Build(m); UvOps.StraightenUvs(m, t, Pts(m, t, p), 30f); });
        yield return ("split", (m, p) => { var t = UvTopology.Build(m); UvOps.SplitUvs(m, t, Pts(m, t, p)); });
        yield return ("merge", (m, p) => { var t = UvTopology.Build(m); UvOps.MergeUvs(m, t, Pts(m, t, p), 0.5f); });
        yield return ("pin", (m, p) => { var t = UvTopology.Build(m); UvOps.SetPins(m, t, Pts(m, t, p), true); UvOps.InvertPins(m); UvOps.UnpinAll(m); });
        // 셸 기반
        static List<int> Shells(UvTopology t, List<int> pts) => UvOps.ShellsOf(t, pts).ToList();
        yield return ("unfold", (m, p) => { var t = UvTopology.Build(m); UvOps.UnfoldRelax(m, t, Shells(t, Pts(m, t, p))); });
        yield return ("optimize", (m, p) => { var t = UvTopology.Build(m); UvOps.Optimize(m, t, Shells(t, Pts(m, t, p))); });
        yield return ("layout", (m, p) => { var t = UvTopology.Build(m); UvOps.Layout(m, t, Shells(t, Pts(m, t, p))); });
        yield return ("layoutRot", (m, p) => { var t = UvTopology.Build(m); UvOps.Layout(m, t, Shells(t, Pts(m, t, p)), 0.05f, true, new Vector2(1, 0), 1f); });
        yield return ("orient", (m, p) => { var t = UvTopology.Build(m); UvOps.OrientShells(m, t, Shells(t, Pts(m, t, p))); });
        yield return ("orientEdge", (m, p) => { var t = UvTopology.Build(m); foreach (int e in p(AliveEdges(m)).Take(3)) UvOps.OrientShellToEdge(m, t, e); });
        yield return ("randomize", (m, p) => { var t = UvTopology.Build(m); UvOps.RandomizeShells(m, t, Shells(t, Pts(m, t, p)), 0.1f, 30, 0.5f, 3); });
        yield return ("stack", (m, p) => { var t = UvTopology.Build(m); UvOps.StackShells(m, t, Shells(t, Pts(m, t, p))); });
        yield return ("stackSimilar", (m, p) => { var t = UvTopology.Build(m); UvOps.StackSimilarShells(m, t, Shells(t, Pts(m, t, p))); });
        yield return ("unstack", (m, p) => { var t = UvTopology.Build(m); UvOps.UnstackShells(m, t, Shells(t, Pts(m, t, p))); });
        yield return ("distShells", (m, p) => { var t = UvTopology.Build(m); UvOps.DistributeShells(m, t, Shells(t, Pts(m, t, p)), false, 0.02f); });
        yield return ("gather", (m, p) => { var t = UvTopology.Build(m); UvOps.GatherShells(m, t, Shells(t, Pts(m, t, p))); });
        yield return ("snapTogether", (m, p) => { var t = UvTopology.Build(m); var l = Pts(m, t, p); if (l.Count >= 2) UvOps.SnapTogether(m, t, l[0], l[^1]); });
        yield return ("snapStack", (m, p) => { var t = UvTopology.Build(m); UvOps.SnapAndStack(m, t, Shells(t, Pts(m, t, p))); });
        yield return ("flipReversed", (m, p) => { var t = UvTopology.Build(m); UvOps.FlipReversedShells(m, t, Shells(t, Pts(m, t, p))); });
        yield return ("moveAndSew", (m, p) => { var t = UvTopology.Build(m); UvOps.MoveAndSew(m, t, p(AliveEdges(m)).Where(e => m.Edges[e].Seam)); });
        yield return ("straightenBorder", (m, p) => { var t = UvTopology.Build(m); foreach (int s in Shells(t, Pts(m, t, p))) UvOps.StraightenBorder(m, t, s); });
        yield return ("straightenShell", (m, p) => { var t = UvTopology.Build(m); UvOps.StraightenShell(m, t, p(AliveEdges(m)).Take(3)); });
        yield return ("mapBorderSq", (m, p) => { var t = UvTopology.Build(m); foreach (int s in Shells(t, Pts(m, t, p))) { UvOps.MapBorder(m, t, s, true); UvOps.Optimize(m, t, new[] { s }, 80); } });
        yield return ("mapBorderCircle", (m, p) => { var t = UvTopology.Build(m); foreach (int s in Shells(t, Pts(m, t, p))) UvOps.MapBorder(m, t, s, false); });
        // 선택/조회
        yield return ("queries", (m, p) =>
        {
            var t = UvTopology.Build(m);
            UvOps.BackFacingFaces(m); UvOps.OverlappingFaces(m); UvOps.TextureBorderPoints(m, t); UvOps.UnmappedFaces(m);
            UvOps.ShortestEdgePath(m, 0, m.VertexCount - 1);
            var es = new HashSet<int>(p(AliveEdges(m)).Take(2)); UvOps.GrowAlongLoop(m, es); UvOps.ShrinkAlongLoop(m, es);
            UvOps.FacesOfPoints(m, t, Pts(m, t, p), true); UvOps.Statistics(m, t); UvOps.DistortionPerFace(m);
            UvOps.ShellBorderLoops(m, t, 0);
        });
    }

    /// <summary>모든 메시 × 모든 연산 × 모든 선택에서 예외·NaN·위상 변화·검증 오류가 없어야 한다.</summary>
    [Fact]
    public void AllOps_AllMeshes_AllSelections_StayValid()
    {
        var failures = new List<string>();
        foreach (var (mname, make) in Meshes())
            foreach (var (oname, run) in Ops())
                foreach (var (pname, pick) in Picks())
                {
                    var m = make();
                    int v = m.AliveVertexCount, e = m.AliveEdgeCount, f = m.AliveFaceCount;
                    string tag = $"{mname}/{oname}/{pname}";
                    try { run(m, pick); }
                    catch (Exception ex) { failures.Add($"{tag}: {ex.GetType().Name} {ex.Message}"); continue; }
                    if (m.AliveVertexCount != v || m.AliveEdgeCount != e || m.AliveFaceCount != f) failures.Add($"{tag}: topology changed");
                    for (int h = 0; h < m.HalfEdgeCount; h++)
                        if (m.Hes[h].Alive && (!float.IsFinite(m.Hes[h].Uv0.X) || !float.IsFinite(m.Hes[h].Uv0.Y))) { failures.Add($"{tag}: non-finite UV"); break; }
                    var errs = MeshValidator.Check(m);
                    if (errs.Count > 0) failures.Add($"{tag}: invalid {errs[0]}");
                }
        foreach (var s in failures) _out.WriteLine(s);
        Assert.True(failures.Count == 0, $"{failures.Count} failure(s):\n" + string.Join("\n", failures.Take(60)));
    }

    /// <summary>Layout은 모든 셸을 0..1 안에 겹치지 않게 넣어야 한다(셸이 많아도).</summary>
    [Fact]
    public void Layout_ManyShells_AllInsideUnitSquare()
    {
        var failures = new List<string>();
        foreach (var (mname, make) in Meshes())
        {
            var m = make();
            UvOps.Unitize(m, AliveFaces(m)); // 면마다 셸 → 셸 수가 많다
            var t = UvTopology.Build(m);
            UvOps.Layout(m, t, Enumerable.Range(0, t.ShellCount), 0.01f);
            for (int h = 0; h < m.HalfEdgeCount; h++)
            {
                if (!m.Hes[h].Alive) continue;
                var uv = m.Hes[h].Uv0;
                if (uv.X < -1e-4f || uv.Y < -1e-4f || uv.X > 1 + 1e-4f || uv.Y > 1 + 1e-4f) { failures.Add($"{mname}: uv {uv} outside"); break; }
            }
            if (UvOps.OverlappingFaces(m).Count > 0) failures.Add($"{mname}: overlapping after layout");
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    /// <summary>Automatic Mapping 결과는 0..1 안이고 볼록한 메시에서는 겹침·뒤집힘이 없어야 한다.</summary>
    [Fact]
    public void Automatic_ConvexMeshes_NoOverlapNoFlip()
    {
        var failures = new List<string>();
        foreach (var (mname, make) in Meshes().Where(x => x.name is "cube" or "cylinder" or "sphere" or "cone" or "beveled" or "smoothed"))
            foreach (int planes in new[] { 3, 4, 5, 6, 8, 12 })
                foreach (bool fewer in new[] { true, false })
                {
                    var m = make();
                    UvOps.AutomaticProject(m, AliveFaces(m), planes, fewer, 0.01f);
                    string tag = $"{mname}/{planes}/{fewer}";
                    var ov = UvOps.OverlappingFaces(m); if (ov.Count > 0) failures.Add($"{tag}: {ov.Count} overlapping");
                    var back = UvOps.BackFacingFaces(m); if (back.Count > 0) failures.Add($"{tag}: {back.Count} back-facing");
                }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    /// <summary>Unfold는 뒤집힌(거울) 셸도 무너뜨리지 않고 3D 비율을 되살려야 한다.</summary>
    [Fact]
    public void Unfold_MirroredShell_KeepsAreaAndOrientation()
    {
        var m = MeshBuilder.Plane(1, 1, 3, 3);
        var t = UvTopology.Build(m);
        // U로 뒤집고(부호 넓이 음수) 한쪽으로 찌그러뜨린 뒤 Unfold
        UvOps.Flip(m, t, Enumerable.Range(0, t.Points.Count), true);
        UvOps.TransformPoints(m, t, Enumerable.Range(0, t.Points.Count), Matrix3x2.CreateScale(1f, 0.3f));
        float before = UvOps.ShellSignedArea(m, t, 0);
        Assert.True(before < 0);
        UvOps.UnfoldRelax(m, UvTopology.Build(m), new[] { 0 }, 200);
        float after = UvOps.ShellSignedArea(m, UvTopology.Build(m), 0);
        // 넓이가 무너지지 않고(0에 가깝지 않고) 부호(방향)를 유지
        Assert.True(after < -0.05f, $"area after unfold = {after}");
        // 각 면이 정사각형에 가까움(3D 비율 복원)
        var d = UvOps.DistortionPerFace(m);
        Assert.All(AliveFaces(m), f => Assert.InRange(d[f], 0.8f, 1.25f));
    }

    /// <summary>Create Shell Grid: 쿼드 격자 면은 정사각 격자 셸 하나가 되어 0..1에 들어가고, 각 쿼드가 같은 크기의 정사각형이다.</summary>
    [Fact]
    public void CreateShellGrid_QuadRegion_BecomesRegularGrid()
    {
        var m = MeshBuilder.Cylinder(0.5f, 1f, 8, caps: false);
        // 원기둥 옆면(쿼드 8개)을 하나의 격자 셸로: 한 엣지를 심으로 잘라 띠가 된다
        var faces = AliveFaces(m);
        UvOps.CreateShellGrid(m, faces);
        var t = UvTopology.Build(m);
        Assert.Equal(1, t.ShellCount);
        Assert.Empty(UvOps.OverlappingFaces(m));
        Assert.Empty(UvOps.BackFacingFaces(m));
        var areas = faces.Select(f => UvOps.FaceUvSignedArea(m, f)).ToList();
        Assert.All(areas, a => Assert.InRange(a, areas[0] * 0.999f, areas[0] * 1.001f));
        for (int h = 0; h < m.HalfEdgeCount; h++) { var uv = m.Hes[h].Uv0; Assert.InRange(uv.X, -1e-5f, 1 + 1e-5f); Assert.InRange(uv.Y, -1e-5f, 1 + 1e-5f); }
    }

    /// <summary>Orient Shell to Edges: 엣지를 가장 가까운 축(U 또는 V)에 맞춘다(최소 회전).</summary>
    [Fact]
    public void OrientShellToEdge_AlignsToNearestAxis()
    {
        var m = MeshBuilder.Plane(1, 1, 1, 1);
        var t = UvTopology.Build(m);
        UvOps.Rotate(m, t, Enumerable.Range(0, t.Points.Count), 10f);
        // 처음엔 거의 세로였던 엣지 → 세로(V)로 맞춰져야 한다(90° 이상 돌리지 않음)
        int edge = -1;
        for (int e = 0; e < m.EdgeCount; e++)
        {
            int he = m.Edges[e].He0; var d = m.Hes[m.Hes[he].Next].Uv0 - m.Hes[he].Uv0;
            if (MathF.Abs(d.Y) > MathF.Abs(d.X)) { edge = e; break; }
        }
        Assert.True(edge >= 0);
        UvOps.OrientShellToEdge(m, UvTopology.Build(m), edge);
        int h0 = m.Edges[edge].He0; var dd = m.Hes[m.Hes[h0].Next].Uv0 - m.Hes[h0].Uv0;
        Assert.True(MathF.Abs(dd.X) < 1e-4f, $"edge not vertical: {dd}");
        // 다른 모든 엣지도 축 정렬(정사각형 면이므로)
        Assert.Empty(UvOps.BackFacingFaces(m));
    }
    /// <summary>
    /// Auto Wrap / Contour Stretch / Create Shell Grid / Unfold(AutoWrap 뒤)은 어떤 메시에서도 겹침·뒤집힘 없이 0..1 안에 펼쳐야 한다
    /// (토러스·구·베벨처럼 휜 영역은 예전에 투영 초기값이 접힌 채 남거나 Unfold가 겹치게 만들었다).
    /// </summary>
    [Fact]
    public void Wrap_Contour_Grid_NoOverlapNoFlip_InsideUnitSquare()
    {
        var failures = new List<string>();
        foreach (var (mname, make) in Meshes())
            foreach (var (op, run) in new (string, Action<PolyMesh>)[] {
                ("autoWrap", m => UvOps.AutoWrap(m)),
                ("contour", m => UvOps.ContourStretch(m, AliveFaces(m))),
                ("grid", m => UvOps.CreateShellGrid(m, AliveFaces(m))) })
            {
                var m = make(); run(m);
                string tag = $"{mname}/{op}";
                var t = UvTopology.Build(m);
                var st = UvOps.Statistics(m, t);
                if (st.overlapping > 0) failures.Add($"{tag}: {st.overlapping} overlapping");
                if (st.reversed > 0) failures.Add($"{tag}: {st.reversed} reversed");
                for (int h = 0; h < m.HalfEdgeCount; h++)
                {
                    if (!m.Hes[h].Alive) continue; var uv = m.Hes[h].Uv0;
                    if (uv.X < -1e-3f || uv.Y < -1e-3f || uv.X > 1.001f || uv.Y > 1.001f) { failures.Add($"{tag}: uv {uv} outside 0..1"); break; }
                }
            }
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    /// <summary>Unfold(ARAP)는 3D 모양을 그대로 펼 수 있는 셸(원기둥 띠)을 늘어남 없이 펴고 셸의 위치·방향을 유지한다.</summary>
    [Fact]
    public void Unfold_DevelopableTube_IsIsometric_AndKeepsPlacement()
    {
        var m = MeshBuilder.Cylinder(0.5f, 1f, 12, caps: false);
        var t = UvTopology.Build(m);
        var (mn0, mx0) = UvOps.Bounds(t.Points.Select(p => p.Uv));
        UvOps.UnfoldRelax(m, t, Enumerable.Range(0, t.ShellCount), 60);
        var d = UvOps.DistortionPerFace(m);
        Assert.All(AliveFaces(m), f => Assert.InRange(d[f], 0.97f, 1.03f));
        // 중심이 처음 위치에 머문다
        t = UvTopology.Build(m);
        var (mn1, mx1) = UvOps.Bounds(t.Points.Select(p => p.Uv));
        Assert.True(Vector2.Distance((mn0 + mx0) * 0.5f, (mn1 + mx1) * 0.5f) < 0.1f);
    }
    /// <summary>
    /// 심과 핀은 UV 세트마다 따로다: 복사 세트에서 투영(심 전부 해제)·핀 설정을 해도 원래 세트로 돌아오면 원래 심·핀이 복원되고,
    /// UvSetsCommand Undo와 .cube 저장→열기 뒤에도 세트별로 유지된다(예전에는 메시 하나에 공유되어 다른 세트의 편집이 덮어썼다).
    /// </summary>
    [Fact]
    public void UvSets_KeepSeamsAndPinsPerSet_ThroughSwitchUndoAndSaveLoad()
    {
        var doc = new Core.Scene.Document();
        var m = MeshBuilder.Cube();
        UvOps.AutoWrap(m);
        int seams0 = Enumerable.Range(0, m.EdgeCount).Count(e => m.Edges[e].Seam);
        Assert.True(seams0 > 0);
        var node = new Core.Scene.SceneNode { Name = "cube", Shape = new Core.Scene.MeshShape(m) };
        doc.AddNode(node, doc.Root);
        // 복사 세트로 전환 → 전체 평면 투영(심 해제) + 핀
        doc.Undo.Push(new Core.Commands.UvSetsCommand("copy", node.Id, mm => mm.SwitchUvSet(mm.AddUvSet("lm", true))));
        UvOps.PlanarProject(m, AliveFaces(m), Vector3.UnitY);
        var t = UvTopology.Build(m); UvOps.SetPins(m, t, new[] { 0 }, true);
        Assert.Equal(0, Enumerable.Range(0, m.EdgeCount).Count(e => m.Edges[e].Seam));
        // map1로 돌아오면 원래 심, 핀 없음
        doc.Undo.Push(new Core.Commands.UvSetsCommand("switch", node.Id, mm => mm.SwitchUvSet(0)));
        Assert.Equal(seams0, Enumerable.Range(0, m.EdgeCount).Count(e => m.Edges[e].Seam));
        Assert.DoesNotContain(m.Hes, h => h.PinUv);
        // Undo(전환 취소) → lm 세트의 심 0·핀 복원
        doc.Undo.Undo();
        Assert.Equal(1, m.CurrentUvSet);
        Assert.Equal(0, Enumerable.Range(0, m.EdgeCount).Count(e => m.Edges[e].Seam));
        Assert.Contains(m.Hes, h => h.PinUv);
        // 저장 → 열기 → map1로 전환하면 심이 돌아온다
        var json = Core.IO.CubeFileFormat.Serialize(doc);
        var doc2 = new Core.Scene.Document(); Core.IO.CubeFileFormat.Deserialize(doc2, json);
        var m2 = doc2.MeshNodes().First().Mesh!;
        Assert.Equal(1, m2.CurrentUvSet);
        Assert.Equal(0, Enumerable.Range(0, m2.EdgeCount).Count(e => m2.Edges[e].Seam));
        Assert.Contains(m2.Hes, h => h.PinUv);
        m2.SwitchUvSet(0);
        Assert.Equal(seams0, Enumerable.Range(0, m2.EdgeCount).Count(e => m2.Edges[e].Seam));
        Assert.DoesNotContain(m2.Hes, h => h.PinUv);
        Assert.Empty(MeshValidator.Check(m2));
    }
    /// <summary>
    /// 토러스 Auto Wrap: 손잡이를 짧은 두 고리(자오선·위선)로 잘라 원반 하나로 펴고, 면의 90%가 평균 텍셀 밀도의 ±50% 안이어야 한다
    /// (예전에는 손잡이가 안 잘려 60/96 면이 겹쳤고, 이후에도 지그재그 절단 + Tutte 그대로라 크게 찌그러졌다).
    /// </summary>
    [Fact]
    public void AutoWrap_Torus_OneLowDistortionShell()
    {
        var m = MeshBuilder.Torus(0.5f, 0.2f, 16, 10);
        UvOps.AutoWrap(m);
        var t = UvTopology.Build(m);
        Assert.Equal(1, t.ShellCount);
        Assert.Empty(UvOps.OverlappingFaces(m));
        var d = UvOps.DistortionPerFace(m).Where((_, f) => m.Faces[f].Alive).OrderBy(x => x).ToArray();
        Assert.InRange(d[d.Length / 10], 0.67f, 1.5f);
        Assert.InRange(d[d.Length * 9 / 10], 0.67f, 1.5f);
    }
    /// <summary>같은 이름으로 세트를 만들거나 복사하면 숫자를 붙여 이름이 겹치지 않는다(uvSet1 → uvSet2, map1_copy → map1_copy1).</summary>
    [Fact]
    public void UvSets_NamesStayUnique()
    {
        var m = MeshBuilder.Cube();
        m.AddUvSet("uvSet1", false); m.AddUvSet("uvSet1", false);
        m.AddUvSet("map1_copy", true); m.AddUvSet("map1_copy", true);
        Assert.Equal(new[] { "map1", "uvSet1", "uvSet2", "map1_copy", "map1_copy1" }, m.UvSets.Select(s => s.Name));
        Assert.Equal("uvSet1", m.UniqueUvSetName("uvSet1", exceptIndex: 1));
    }
    /// <summary>
    /// Bevel(이력) → Auto Wrap(UvEditCommand) → Bevel Distance 편집: 위상이 같으므로 UV 작업이 이력 재실행 뒤에도 남아야 한다
    /// (예전에는 UV 편집이 이력에 없어 Bevel을 고치면 UV가 Bevel 직후 상태로 돌아갔다). Undo는 이력 항목도 뺀다.
    /// </summary>
    [Fact]
    public void UvEdits_SurviveHistoryReplay_WhenTopologyUnchanged()
    {
        var doc = new Core.Scene.Document();
        var cube = Core.Commands.CreatePrimitiveCommand.Cube(doc); doc.Undo.Push(cube);
        var node = cube.Node; var mesh = node.Mesh!;
        var edges = Enumerable.Range(0, mesh.EdgeCount).ToArray();
        doc.Undo.Push(new Core.Commands.MeshOpCommand("Bevel", node.Id, new Core.Commands.HistoryParams(Core.Commands.HistoryParam.F("Distance", 0.1f, 0, 10)),
            (m, p) => { var f = MeshOps.BevelEdges(m, edges, p.Float("Distance")); return (f.Count > 0, Core.Selection.SelectMode.Face, f); }));
        doc.Undo.Push(new Core.Commands.UvEditCommand("Auto Wrap", node.Id, m => UvOps.AutoWrap(m)));
        var shape = node.MeshShape!;
        Assert.Equal("Auto Wrap", shape.History[^1].Name);
        var wrapped = mesh.SnapshotUvs();
        int bevelIdx = shape.History.FindIndex(h => h.Name == "Bevel");
        var p2 = shape.History[bevelIdx].Params.Clone(); p2["Distance"].Float = 0.2f;
        doc.Undo.Push(new Core.Commands.EditHistoryCommand(node.Id, bevelIdx, p2));
        var after = node.Mesh!.SnapshotUvs();
        Assert.Equal(wrapped, after);
        Assert.Empty(MeshValidator.Check(node.Mesh!));
        // 편집 Undo → 이력 항목 유지, UV Undo → 이력 항목 제거
        doc.Undo.Undo();
        doc.Undo.Undo();
        Assert.DoesNotContain(node.MeshShape!.History, h => h.Name == "Auto Wrap");
    }
}
