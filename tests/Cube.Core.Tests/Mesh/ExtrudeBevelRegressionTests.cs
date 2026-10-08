using System.Numerics;
using Cube.Core.Commands;
using Cube.Core.IO;
using Cube.Core.Mesh;
using Cube.Core.Scene;
using Cube.Core.Selection;

namespace Cube.Core.Tests.Mesh;

/// <summary>
/// Extrude/Bevel 회귀 스트레스: 앱이 쓰는 것과 같은 경로(MeshOps + MeshOpCommand + EditHistoryCommand)로 여러 조합·순서를 돌리고
/// 매 단계 MeshValidator/오일러 특성/캐시 검증(PolyMesh.VerifyCaches, CacheVerifyInit)을 확인한다.
/// </summary>
public class ExtrudeBevelRegressionTests
{
    private static int Euler(PolyMesh m) => m.AliveVertexCount - m.AliveEdgeCount + m.AliveFaceCount;

    private static void AssertSound(PolyMesh m, string ctx, bool closed = false, int? euler = null)
    {
        var problems = MeshValidator.Check(m);
        Assert.True(problems.Count == 0, $"{ctx}: {string.Join("; ", problems)}");
        // 모든 살아있는 면의 하프에지가 살아있는 정점을 가리키고 엣지 맵/정점 캐시가 일치하는지(FindEdge/VertexOutgoing가 VerifyCaches로 검사)
        var tmp = new List<int>();
        for (int f = 0; f < m.FaceCount; f++)
        {
            if (!m.Faces[f].Alive) continue;
            m.GetFaceVertices(f, tmp);
            Assert.True(tmp.Count >= 3, $"{ctx}: face {f} degree {tmp.Count}");
            for (int i = 0; i < tmp.Count; i++)
            {
                Assert.True(m.Verts[tmp[i]].Alive, $"{ctx}: face {f} dead vertex {tmp[i]}");
                int e = m.FindEdge(tmp[i], tmp[(i + 1) % tmp.Count]);
                Assert.True(e >= 0 && m.Edges[e].Alive, $"{ctx}: face {f} edge ({tmp[i]},{tmp[(i + 1) % tmp.Count]}) not found via FindEdge");
            }
        }
        for (int v = 0; v < m.VertexCount; v++)
            if (m.Verts[v].Alive) Assert.True(m.VertexOutgoing(v).Length > 0, $"{ctx}: vertex {v} alive but isolated");
        if (closed) for (int e = 0; e < m.EdgeCount; e++) if (m.Edges[e].Alive) Assert.False(m.IsBoundaryEdge(e), $"{ctx}: edge {e} is boundary");
        if (euler != null) Assert.True(Euler(m) == euler, $"{ctx}: euler {Euler(m)} != {euler}");
    }

    private static int[] AliveFaces(PolyMesh m) => Enumerable.Range(0, m.FaceCount).Where(f => m.Faces[f].Alive).ToArray();
    private static int[] AliveEdges(PolyMesh m) => Enumerable.Range(0, m.EdgeCount).Where(e => m.Edges[e].Alive).ToArray();
    private static int[] AliveVerts(PolyMesh m) => Enumerable.Range(0, m.VertexCount).Where(v => m.Verts[v].Alive).ToArray();

    private static TriangleSoupToPolyMesh.Surface ToSoup(PolyMesh m)
    {
        var r = MeshTessellator.Build(m);
        return new TriangleSoupToPolyMesh.Surface
        {
            Positions = r.Positions.Take(r.CornerCount).ToArray(),
            Normals = r.Normals.Take(r.CornerCount).ToArray(),
            Uvs = r.Uvs.Take(r.CornerCount).ToArray(),
            Indices = r.Indices.Take(r.IndexCount).ToArray(),
        };
    }

    private static PolyMesh Imported(PolyMesh src)
    {
        var m = TriangleSoupToPolyMesh.Convert(new[] { ToSoup(src) }, ImportOptions.Default, out _);
        MeshNormals.Recompute(m);
        return m;
    }

    // ---------------------------------------------------------------- 순서 조합

    [Fact]
    public void Cube_ExtrudeTwice_ThenBevel_ThenExtrude()
    {
        var m = MeshBuilder.Cube();
        var caps = MeshOps.ExtrudeFaces(m, new[] { 0 });
        AssertSound(m, "extrude1", closed: true, euler: 2);
        Assert.Equal(10, m.AliveFaceCount);
        var o = new ExtrudeOptions { Offset = 0.3f };
        caps = MeshOps.Extrude(m, caps, o);
        AssertSound(m, "extrude2", closed: true, euler: 2);
        Assert.Equal(14, m.AliveFaceCount);
        var nf = MeshOps.Bevel(m, AliveEdges(m), new BevelOptions { Width = 0.05f, Segments = 2 });
        AssertSound(m, "bevel after extrude", closed: true, euler: 2);
        Assert.NotEmpty(nf);
        var caps2 = MeshOps.Extrude(m, new[] { nf[0] }, new ExtrudeOptions { Offset = 0.1f, Direction = ExtrudeDirection.FaceNormals });
        AssertSound(m, "extrude after bevel", closed: true, euler: 2);
        Assert.NotEmpty(caps2);
    }

    [Fact]
    public void Cube_BevelThenExtrudeCaps_RepeatedBevels()
    {
        var m = MeshBuilder.Cube();
        var nf = MeshOps.Bevel(m, new[] { 0, 1, 2 }, new BevelOptions { Width = 0.1f, Segments = 3 });
        AssertSound(m, "bevel", closed: true, euler: 2);
        var caps = MeshOps.Extrude(m, nf, new ExtrudeOptions { Offset = 0.2f });
        AssertSound(m, "extrude bevel strip", closed: true, euler: 2);
        for (int i = 0; i < 3; i++)
        {
            var edges = AliveEdges(m).Where(e => e % 3 == i).ToArray();
            MeshOps.Bevel(m, edges, new BevelOptions { Width = 0.01f, Segments = 1 + i });
            AssertSound(m, $"bevel pass {i}", closed: true, euler: 2);
        }
    }

    // ---------------------------------------------------------------- Extrude 옵션 전수

    public static IEnumerable<object[]> ExtrudeCombos()
    {
        foreach (var type in new[] { ExtrudeType.Region, ExtrudeType.IndividualFaces })
            foreach (var dir in Enum.GetValues<ExtrudeDirection>())
                foreach (int steps in new[] { 1, 3 })
                    foreach (bool flip in new[] { false, true })
                        yield return new object[] { type, dir, steps, flip };
    }

    private static IEnumerable<(string name, PolyMesh mesh, int[] faces, bool closed)> ExtrudeTargets()
    {
        var cube = MeshBuilder.Cube();
        yield return ("cube single", MeshBuilder.Cube(), new[] { 0 }, true);
        yield return ("cube two adjacent", MeshBuilder.Cube(), new[] { 0, 1 }, true);
        // 마주보는 두 면(분리된 두 영역): 면 법선이 반대인 쌍
        var opp = new List<int>();
        for (int f = 0; f < cube.FaceCount; f++) for (int g = f + 1; g < cube.FaceCount; g++)
                if (Vector3.Dot(MeshNormals.FaceNormalUnnormalized(cube, f), MeshNormals.FaceNormalUnnormalized(cube, g)) < -0.9f && opp.Count == 0) { opp.Add(f); opp.Add(g); }
        yield return ("cube opposite", MeshBuilder.Cube(), opp.ToArray(), true);
        yield return ("cube all (closed volume)", MeshBuilder.Cube(), Enumerable.Range(0, 6).ToArray(), false);
        var plane = MeshBuilder.Plane(1, 1, 3, 3);
        yield return ("plane center", MeshBuilder.Plane(1, 1, 3, 3), new[] { 4 }, false);
        yield return ("plane corner", MeshBuilder.Plane(1, 1, 3, 3), new[] { 0 }, false);
        yield return ("plane all (open plate)", MeshBuilder.Plane(1, 1, 3, 3), AliveFaces(plane), false);
        yield return ("plane diagonal (shared vertex only)", MeshBuilder.Plane(1, 1, 2, 2), new[] { 0, 3 }, false);
        var cyl = MeshBuilder.Cylinder(0.5f, 1f, 8, true);
        var ngons = AliveFaces(cyl).Where(f => cyl.FaceDegree(f) > 4).ToArray();
        yield return ("cylinder cap (n-gon)", MeshBuilder.Cylinder(0.5f, 1f, 8, true), ngons, true);
        yield return ("cylinder side + cap", MeshBuilder.Cylinder(0.5f, 1f, 8, true), new[] { ngons[0], 0 }, true);
        var ic = Imported(MeshBuilder.Cube());
        yield return ("imported cube", ic, AliveFaces(ic).Take(2).ToArray(), true);
        var icy = Imported(MeshBuilder.Cylinder(0.5f, 1f, 8, true));
        yield return ("imported cylinder", icy, AliveFaces(icy).Take(3).ToArray(), true);
    }

    [Theory]
    [MemberData(nameof(ExtrudeCombos))]
    public void Extrude_AllOptions_AllTargets(ExtrudeType type, ExtrudeDirection dir, int steps, bool flip)
    {
        foreach (var (name, m, faces, closed) in ExtrudeTargets())
        {
            int faceBefore = m.AliveFaceCount;
            var o = new ExtrudeOptions { Type = type, Direction = dir, Offset = 0.2f, Steps = steps, FlipNormals = flip, CustomDirection = new Vector3(1, 2, 3) };
            var caps = MeshOps.Extrude(m, faces, o);
            string ctx = $"{name} {type}/{dir}/steps{steps}/flip{flip}";
            Assert.True(caps.Count > 0, $"{ctx}: no caps");
            AssertSound(m, ctx, closed: closed && !flip);
            Assert.True(m.AliveFaceCount > faceBefore, $"{ctx}: face count did not grow");
            foreach (int f in caps) Assert.True(m.Faces[f].Alive, $"{ctx}: cap {f} dead");
            // 두 번째 Extrude(캡 위에 또)
            var caps2 = MeshOps.Extrude(m, caps, o with { Offset = 0.1f, Steps = 1 });
            Assert.True(caps2.Count > 0, $"{ctx}: second extrude no caps");
            AssertSound(m, ctx + " x2", closed: closed && !flip);
            // 이어서 캡 둘레 Bevel
            var edges = new HashSet<int>(); var hes = new List<int>();
            foreach (int f in caps2) { m.GetFaceHalfEdges(f, hes); foreach (int h in hes) edges.Add(m.Hes[h].Edge); }
            var nf = MeshOps.Bevel(m, edges, new BevelOptions { Width = 0.02f, Segments = 2 });
            AssertSound(m, ctx + " bevel", closed: closed && !flip);
            Assert.True(nf.Count > 0, $"{ctx}: bevel produced nothing");
        }
    }

    [Fact]
    public void ExtrudeEdges_AllOptions_OnPlaneBorder()
    {
        foreach (var dir in Enum.GetValues<ExtrudeDirection>())
            foreach (int steps in new[] { 1, 2 })
                foreach (bool flip in new[] { false, true })
                {
                    var m = MeshBuilder.Plane(1, 1, 2, 2);
                    var border = AliveEdges(m).Where(m.IsBoundaryEdge).ToArray();
                    var o = new ExtrudeOptions { Direction = dir, Offset = 0.25f, Steps = steps, FlipNormals = flip, CustomDirection = Vector3.UnitY };
                    var faces = MeshOps.ExtrudeEdges(m, border, o, out var newEdges);
                    string ctx = $"edges {dir}/steps{steps}/flip{flip}";
                    Assert.True(faces.Count > 0, ctx);
                    Assert.True(newEdges.Count > 0, ctx + " newEdges");
                    foreach (int e in newEdges) Assert.True(m.Edges[e].Alive && m.IsBoundaryEdge(e), $"{ctx}: new edge {e} not boundary");
                    AssertSound(m, ctx);
                    // 새 테두리를 다시 Extrude
                    var faces2 = MeshOps.ExtrudeEdges(m, newEdges, o with { Steps = 1 }, out var newEdges2);
                    Assert.True(faces2.Count > 0, ctx + " x2");
                    AssertSound(m, ctx + " x2");
                    // 새 테두리에 접한 정점 Bevel
                    var vs = new HashSet<int>(); foreach (int e in newEdges2) { var (a, b) = m.EdgeVertices(e); vs.Add(a); vs.Add(b); }
                    MeshOps.Bevel(m, vs, new BevelOptions { Affect = BevelAffect.Vertices, Width = 0.03f, Segments = 2 });
                    AssertSound(m, ctx + " vertex bevel");
                }
    }

    // ---------------------------------------------------------------- Bevel 변형

    public static IEnumerable<object[]> BevelSegments() => new[] { 1, 2, 4 }.Select(s => new object[] { s });

    [Theory]
    [MemberData(nameof(BevelSegments))]
    public void Bevel_Edges_Loops_Rings_All_Vertices(int segments)
    {
        foreach (var (name, make, closed) in new (string, Func<PolyMesh>, bool)[]
        {
            ("cube", () => MeshBuilder.Cube(), true),
            ("cylinder", () => MeshBuilder.Cylinder(0.5f, 1f, 10, true), true),
            ("plane", () => MeshBuilder.Plane(1, 1, 3, 3), false),
            ("imported cube", () => Imported(MeshBuilder.Cube()), true),
            ("imported sphere", () => Imported(MeshBuilder.Sphere(0.5f, 8, 4)), true),
            ("extruded cube", () => { var c = MeshBuilder.Cube(); MeshOps.Extrude(c, new[] { 0 }, new ExtrudeOptions { Offset = 0.3f }); return c; }, true),
        })
        {
            foreach (var (sel, pick) in new (string, Func<PolyMesh, int[]>)[]
            {
                ("single", m => new[] { AliveEdges(m).FirstOrDefault(e => !m.IsBoundaryEdge(e), AliveEdges(m)[0]) }),
                ("loop", m => MeshOps.EdgeLoop(m, AliveEdges(m).FirstOrDefault(e => !m.IsBoundaryEdge(e), AliveEdges(m)[0])).ToArray()),
                ("ring", m => MeshOps.EdgeRing(m, AliveEdges(m)[0]).entries.Select(x => x.edge).ToArray()),
                ("all", AliveEdges),
            })
            {
                var m = make();
                var edges = pick(m);
                string ctx = $"{name}/{sel}/seg{segments}";
                var nf = MeshOps.Bevel(m, edges, new BevelOptions { Width = 0.03f, Segments = segments });
                Assert.True(nf.Count > 0, ctx + ": nothing");
                AssertSound(m, ctx, closed: closed);
                // 결과 위에 다시 Bevel(새 면 둘레)
                var edges2 = new HashSet<int>(); var hes = new List<int>();
                foreach (int f in nf.Take(3)) { m.GetFaceHalfEdges(f, hes); foreach (int h in hes) edges2.Add(m.Hes[h].Edge); }
                MeshOps.Bevel(m, edges2, new BevelOptions { Width = 0.005f, Segments = 1 });
                AssertSound(m, ctx + " rebevel", closed: closed);
            }
            // 경계 엣지만: Bevel은 면이 하나뿐인 경계 엣지를 다루지 않는다(아무것도 하지 않고 메시를 망가뜨리지 않아야 함)
            {
                var m = make();
                var border = AliveEdges(m).Where(m.IsBoundaryEdge).ToArray();
                if (border.Length > 0)
                {
                    int faces = m.AliveFaceCount;
                    var nf = MeshOps.Bevel(m, border, new BevelOptions { Width = 0.03f, Segments = segments });
                    Assert.Empty(nf);
                    Assert.Equal(faces, m.AliveFaceCount);
                    AssertSound(m, $"{name}/border-only/seg{segments}", closed: closed);
                }
            }
            // 정점 Bevel
            {
                var m = make();
                var vs = AliveVerts(m).Take(4).ToArray();
                var nf = MeshOps.Bevel(m, vs, new BevelOptions { Affect = BevelAffect.Vertices, Width = 0.05f, Segments = segments });
                Assert.True(nf.Count > 0, $"{name}/vertices/seg{segments}: nothing");
                AssertSound(m, $"{name}/vertices/seg{segments}", closed: closed);
                var caps = MeshOps.Extrude(m, nf, new ExtrudeOptions { Offset = 0.05f, Type = ExtrudeType.IndividualFaces });
                Assert.True(caps.Count > 0);
                AssertSound(m, $"{name}/vertices/seg{segments} extrude", closed: closed);
            }
        }
    }

    [Fact]
    public void Bevel_OptionMatrix_OnCube()
    {
        foreach (var wt in Enum.GetValues<BevelWidthType>())
            foreach (var miter in Enum.GetValues<BevelMiter>())
                foreach (var inter in Enum.GetValues<BevelIntersection>())
                    foreach (bool harden in new[] { false, true })
                        foreach (bool clamp in new[] { false, true })
                        {
                            var m = MeshBuilder.Cube();
                            var o = new BevelOptions { WidthType = wt, Width = wt == BevelWidthType.Percent ? 20f : 0.1f, Segments = 2, MiterOuter = miter, Intersection = inter, HardenNormals = harden, ClampOverlap = clamp, LoopSlide = !clamp, MarkSeams = harden, MarkSharp = clamp };
                            var nf = MeshOps.Bevel(m, AliveEdges(m), o);
                            string ctx = $"bevel {wt}/{miter}/{inter}/harden{harden}/clamp{clamp}";
                            Assert.True(nf.Count > 0, ctx);
                            AssertSound(m, ctx, closed: true, euler: 2);
                            MeshOps.Extrude(m, new[] { nf[0] }, new ExtrudeOptions { Offset = 0.05f });
                            AssertSound(m, ctx + " extrude", closed: true, euler: 2);
                        }
    }

    // ---------------------------------------------------------------- 명령 경로(Undo/Redo/히스토리 편집)

    private static HistoryParams BevelParams(float width, int segments) => new(
        HistoryParam.F("Width", width, 0f, 100f, 0.001f), HistoryParam.I("Segments", segments, 1, 100));

    private static BevelOptions BevelFrom(HistoryParams p) => new() { Width = p.Float("Width"), Segments = Math.Max(1, p.Int("Segments")) };

    private static HistoryParams ExtrudeParams(float offset, int steps, int type, int dir) => new(
        HistoryParam.F("Offset", offset, -100f, 100f, 0.01f), HistoryParam.I("Steps", steps, 1, 100),
        HistoryParam.I("Type", type, 0, 1), HistoryParam.I("Direction", dir, 0, 5));

    private static ExtrudeOptions ExtrudeFrom(HistoryParams p) => new()
    {
        Offset = p.Float("Offset"), Steps = p.Int("Steps"), Type = (ExtrudeType)p.Int("Type"), Direction = (ExtrudeDirection)p.Int("Direction"), CustomDirection = Vector3.UnitY,
    };

    [Fact]
    public void CommandPath_ExtrudeBevel_UndoRedo_HistoryEdits()
    {
        var doc = new Document();
        var create = CreatePrimitiveCommand.Cube(doc); doc.Undo.Push(create);
        var node = create.Node; var mesh = node.Mesh!;
        int[] f0 = { 0 };
        doc.Undo.Push(new MeshOpCommand("Extrude", node.Id, ExtrudeParams(0.2f, 1, 0, 0),
            (m, p) => { var caps = MeshOps.Extrude(m, f0, ExtrudeFrom(p)); return (caps.Count > 0, SelectMode.Face, caps); }));
        AssertSound(mesh, "cmd extrude", closed: true, euler: 2);
        Assert.Equal(SelectMode.Face, doc.Selection.Mode);
        var capFaces = doc.Selection.GetComponents(node.Id).Faces.ToArray();
        Assert.Single(capFaces);

        // 조작기 두께 드래그와 같은 MoveVerticesCommand(이력 포함)
        var dirs = MeshOps.RegionOffsetDirections(mesh, capFaces);
        var ids = dirs.Keys.ToArray();
        var before = ids.Select(v => mesh.Verts[v].Position).ToArray();
        var after = ids.Select((v, i) => before[i] + dirs[v] * 0.3f).ToArray();
        foreach (var (v, p) in ids.Zip(after)) { var vt = mesh.Verts[v]; vt.Position = p; mesh.Verts[v] = vt; }
        mesh.BumpGeometry();
        doc.Undo.Push(new MoveVerticesCommand("Extrude Thickness", node.Id, ids, before, after, null, new HistoryParams(HistoryParam.F("Thickness", 0.3f, -10, 10, 0.01f)),
            (m, p) => { float t = p.Float("Thickness"); for (int i = 0; i < ids.Length; i++) { var vt = m.Verts[ids[i]]; vt.Position = before[i] + dirs[ids[i]] * t; m.Verts[ids[i]] = vt; } return true; }), alreadyApplied: true);
        AssertSound(mesh, "cmd thickness", closed: true, euler: 2);

        // 캡 둘레 Bevel(선택 → 엣지)
        var capEdges = SelectionOps.Convert(mesh, doc.Selection.GetComponents(node.Id), SelectMode.Face, SelectMode.Edge).ToArray();
        Assert.Equal(4, capEdges.Length);
        doc.Undo.Push(new MeshOpCommand("Bevel", node.Id, BevelParams(0.05f, 1),
            (m, p) => { var nf = MeshOps.Bevel(m, capEdges, BevelFrom(p)); return (nf.Count > 0, SelectMode.Face, nf); }));
        AssertSound(mesh, "cmd bevel", closed: true, euler: 2);
        int facesAfterBevel = mesh.AliveFaceCount;
        Assert.Equal(3, node.MeshShape!.History.Count);

        // 히스토리 편집: Bevel Segments 1 → 3 → 2, Width 변경
        foreach (var (seg, w) in new[] { (3, 0.05f), (2, 0.08f), (4, 0.02f), (1, 0.05f) })
        {
            var p = node.MeshShape.History[2].Params.Clone(); p["Segments"].Int = seg; p["Width"].Float = w;
            doc.Undo.Push(new EditHistoryCommand(node.Id, 2, p));
            AssertSound(mesh, $"edit bevel seg{seg}", closed: true, euler: 2);
            Assert.Equal(seg, node.MeshShape.History[2].Params.Int("Segments"));
        }
        // 첫 항목(Extrude) 편집: Offset/Steps/Type 바꾸면 뒤의 두께·Bevel이 다시 적용된다
        foreach (var (off, steps, type) in new[] { (0.5f, 2, 0), (0.1f, 1, 1), (0.3f, 3, 0), (0.2f, 1, 0) })
        {
            var p = node.MeshShape.History[0].Params.Clone(); p["Offset"].Float = off; p["Steps"].Int = steps; p["Type"].Int = type;
            doc.Undo.Push(new EditHistoryCommand(node.Id, 0, p));
            AssertSound(mesh, $"edit extrude off{off} steps{steps} type{type}", closed: true, euler: 2);
            Assert.Equal(3, node.MeshShape.History.Count);
        }
        // Undo 전부 → Redo 전부, 매 단계 검증
        int n = doc.Undo.UndoCount;
        for (int i = 0; i < n; i++) { Assert.True(doc.Undo.Undo()); if (node.Mesh != null) AssertSound(mesh, $"undo {i}"); }
        for (int i = 0; i < n; i++) { Assert.True(doc.Undo.Redo()); AssertSound(mesh, $"redo {i}"); }
        AssertSound(mesh, "final", closed: true, euler: 2);
        Assert.Equal(facesAfterBevel, mesh.AliveFaceCount);
        // Undo 뒤 새 명령(Redo 스택 버림) → 다시 Extrude
        doc.Undo.Undo();
        var sel = AliveFaces(mesh).Take(2).ToArray();
        doc.Undo.Push(new MeshOpCommand("Extrude", node.Id, ExtrudeParams(0.1f, 1, 1, 1),
            (m, p) => { var caps = MeshOps.Extrude(m, sel, ExtrudeFrom(p)); return (caps.Count > 0, SelectMode.Face, caps); }));
        AssertSound(mesh, "extrude after undo", closed: true, euler: 2);
    }

    [Fact]
    public void CommandPath_ImportedMesh_ExtrudeBevel_HistoryEdits()
    {
        var doc = new Document();
        var imported = Imported(MeshBuilder.Cylinder(0.5f, 1f, 12, true));
        var node = new SceneNode { Name = "imported" };
        node.Shape = new MeshShape(imported);
        doc.Undo.Push(new AddNodeCommand("Add", node));
        var mesh = node.Mesh!;
        AssertSound(mesh, "imported", closed: true, euler: 2);
        var cap = AliveFaces(mesh).OrderByDescending(f => mesh.FaceDegree(f)).First();
        int[] target = { cap };
        doc.Undo.Push(new MeshOpCommand("Extrude", node.Id, ExtrudeParams(0.3f, 2, 0, 1),
            (m, p) => { var caps = MeshOps.Extrude(m, target, ExtrudeFrom(p)); return (caps.Count > 0, SelectMode.Face, caps); }));
        AssertSound(mesh, "imported extrude", closed: true, euler: 2);
        var edges = AliveEdges(mesh);
        doc.Undo.Push(new MeshOpCommand("Bevel", node.Id, BevelParams(0.02f, 2),
            (m, p) => { var nf = MeshOps.Bevel(m, edges, BevelFrom(p)); return (nf.Count > 0, SelectMode.Face, nf); }));
        AssertSound(mesh, "imported bevel", closed: true, euler: 2);
        var p2 = node.MeshShape!.History[0].Params.Clone(); p2["Offset"].Float = 0.6f; p2["Steps"].Int = 1;
        doc.Undo.Push(new EditHistoryCommand(node.Id, 0, p2));
        AssertSound(mesh, "imported edit", closed: true, euler: 2);
        doc.Undo.Undo(); doc.Undo.Undo(); AssertSound(mesh, "imported undo2", closed: true, euler: 2);
        doc.Undo.Redo(); doc.Undo.Redo(); AssertSound(mesh, "imported redo2", closed: true, euler: 2);
    }

    // ---------------------------------------------------------------- 무작위 순서(캐시 검증 포함)

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Fuzz_RandomExtrudeBevelSequences(int seed)
    {
        var rng = new Random(seed * 7919);
        foreach (var (meshName, make) in new (string, Func<PolyMesh>)[] { ("cube", () => MeshBuilder.Cube()), ("plane", () => MeshBuilder.Plane(1, 1, 3, 3)), ("cylinder", () => MeshBuilder.Cylinder(0.5f, 1, 8, true)), ("imported cube", () => Imported(MeshBuilder.Cube())) })
        {
            var m = make();
            bool closed = AliveEdges(m).All(e => !m.IsBoundaryEdge(e));
            for (int step = 0; step < 14; step++)
            {
                var faces = AliveFaces(m); var edges = AliveEdges(m); var verts = AliveVerts(m);
                int op = rng.Next(7);
                string ctx = $"{meshName} seed{seed} step{step} op{op} faces={faces.Length}";
                switch (op)
                {
                    case 0: // 면 Extrude(옵션 무작위)
                        {
                            var pick = faces.OrderBy(_ => rng.Next()).Take(1 + rng.Next(Math.Min(4, faces.Length))).ToArray();
                            var o = new ExtrudeOptions { Type = (ExtrudeType)rng.Next(2), Direction = (ExtrudeDirection)rng.Next(6), Offset = 0.05f + (float)rng.NextDouble() * 0.2f, Steps = 1 + rng.Next(2), CustomDirection = Vector3.UnitY };
                            var caps = MeshOps.Extrude(m, pick, o);
                            Assert.True(caps.Count > 0, ctx);
                            break;
                        }
                    case 1: // 엣지 Bevel
                        {
                            var pick = edges.OrderBy(_ => rng.Next()).Take(1 + rng.Next(Math.Min(6, edges.Length))).ToArray();
                            var bo = new BevelOptions { Width = 0.005f + (float)rng.NextDouble() * 0.02f, Segments = 1 + rng.Next(3), Intersection = (BevelIntersection)rng.Next(3), MiterOuter = (BevelMiter)rng.Next(3) };
                            ctx += $" edges=[{string.Join(",", pick.Select(e => $"{e}({m.EdgeVertices(e).a}-{m.EdgeVertices(e).b})"))}] width={bo.Width} seg={bo.Segments} inter={bo.Intersection} miter={bo.MiterOuter}";
                            MeshOps.Bevel(m, pick, bo);
                            break;
                        }
                    case 2: // 정점 Bevel
                        {
                            var pick = verts.OrderBy(_ => rng.Next()).Take(1 + rng.Next(3)).ToArray();
                            MeshOps.Bevel(m, pick, new BevelOptions { Affect = BevelAffect.Vertices, Width = 0.005f + (float)rng.NextDouble() * 0.02f, Segments = 1 + rng.Next(2) });
                            break;
                        }
                    case 3: // 엣지 루프 삽입
                        {
                            MeshOps.InsertEdgeLoop(m, edges[rng.Next(edges.Length)], 0.3f + (float)rng.NextDouble() * 0.4f);
                            break;
                        }
                    case 4: // 테두리 엣지 Extrude(열린 메시만)
                        {
                            var border = edges.Where(m.IsBoundaryEdge).ToArray();
                            if (border.Length == 0) goto case 0;
                            MeshOps.ExtrudeEdges(m, border.Take(1 + rng.Next(border.Length)), new ExtrudeOptions { Offset = 0.1f, Direction = (ExtrudeDirection)rng.Next(6), CustomDirection = Vector3.UnitY }, out _);
                            break;
                        }
                    case 5: // 면 삭제(캐시가 삭제 뒤에도 맞는지)
                        {
                            if (faces.Length < 8) goto case 0;
                            MeshOps.DeleteFaces(m, new[] { faces[rng.Next(faces.Length)] });
                            closed = false;
                            break;
                        }
                    default: // 엣지 루프 Bevel
                        {
                            var loop = MeshOps.EdgeLoop(m, edges[rng.Next(edges.Length)]);
                            MeshOps.Bevel(m, loop, new BevelOptions { Width = 0.01f, Segments = 2 });
                            break;
                        }
                }
                MeshNormals.Recompute(m);
                AssertSound(m, ctx, closed: closed);
            }
        }
    }

    /// <summary>
    /// 회귀(v0.0.47): 끝점이 차수 2(엣지 중간에 끼운 정점, Multi-Cut/Split 뒤)인 엣지를 Bevel하면 그 끝에서 띠 쿼드의 두 옆 정점이 같아
    /// 중복 정점이 있는 면 추가가 거부되어 구멍이 났고, 일직선으로 이어진 엣지를 따라 거의 끝까지(98%) 미끄러졌다.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Bevel_EdgeEndingAtValence2Vertex_NoHole(int segments)
    {
        var m = MeshBuilder.Cube();
        int mid = MeshOps.SplitEdge(m, 0, 0.5f);
        Assert.Equal(2, m.VertexOutgoing(mid).Length);
        int half = AliveEdges(m).First(e => { var (a, b) = m.EdgeVertices(e); return a == mid || b == mid; });
        var midPos = m.Verts[mid].Position;
        var nf = MeshOps.Bevel(m, new[] { half }, new BevelOptions { Width = 0.1f, Segments = segments });
        Assert.NotEmpty(nf);
        AssertSound(m, $"valence2 seg{segments}", closed: true, euler: 2);
        // 띠는 차수 2 끝점에서 뾰족하게 끝난다: 그 자리(원래 정점 위치)에 정점이 남고 이어진 엣지를 따라 미끄러지지 않는다
        Assert.Contains(AliveVerts(m), v => Vector3.Distance(m.Verts[v].Position, midPos) < 1e-4f);
    }
}
