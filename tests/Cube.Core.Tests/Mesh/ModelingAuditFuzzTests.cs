using System.Numerics;
using Cube.Core.IO;
using Cube.Core.Mesh;
using Cube.Core.Scene;
using Xunit.Abstractions;

namespace Cube.Core.Tests.Mesh;

/// <summary>
/// 모델링 연산 전수 퍼즈(감사용): 여러 기본 메시(큐브/n각형/열린 평면/원기둥(캡 유무)/원뿔/구/토러스/Boolean 결과/Array 결과)에
/// 각 MeshOps 연산을 무작위 컴포넌트 부분집합으로 적용하고, 결과의 하프에지 불변식·면 차수·정점 중복·유한 좌표·고립 정점을 검사한다.
/// 위상 보존 연산은 닫힌 입력에서 닫힘과 오일러 특성이 유지되는지도 본다. 연쇄 적용(무작위 순서)도 돌린다.
/// </summary>
public class ModelingAuditFuzzTests
{
    /// <summary>테스트 출력(실패 목록 보고용).</summary>
    private readonly ITestOutputHelper _out;
    /// <summary>xUnit이 출력 도우미를 넣어 준다.</summary>
    public ModelingAuditFuzzTests(ITestOutputHelper output) { _out = output; }

    /// <summary>오일러 특성 V − E + F.</summary>
    private static int Euler(PolyMesh m) => m.AliveVertexCount - m.AliveEdgeCount + m.AliveFaceCount;
    /// <summary>경계 엣지가 하나도 없는(닫힌) 메시인지.</summary>
    private static bool Closed(PolyMesh m) { for (int e = 0; e < m.EdgeCount; e++) if (m.Edges[e].Alive && m.IsBoundaryEdge(e)) return false; return m.AliveFaceCount > 0; }
    /// <summary>살아 있는 ID 목록.</summary>
    private static int[] Faces(PolyMesh m) => Enumerable.Range(0, m.FaceCount).Where(f => m.Faces[f].Alive).ToArray();
    /// <summary>살아 있는 엣지 ID 목록.</summary>
    private static int[] Edges(PolyMesh m) => Enumerable.Range(0, m.EdgeCount).Where(e => m.Edges[e].Alive).ToArray();
    /// <summary>살아 있는 정점 ID 목록.</summary>
    private static int[] Verts(PolyMesh m) => Enumerable.Range(0, m.VertexCount).Where(v => m.Verts[v].Alive).ToArray();
    /// <summary>경계 엣지 ID 목록.</summary>
    private static int[] Boundary(PolyMesh m) => Edges(m).Where(m.IsBoundaryEdge).ToArray();

    /// <summary>무작위 부분집합(최소 1개, 대략 frac 비율).</summary>
    private static int[] Pick(Random r, int[] all, double frac = 0.3)
    {
        if (all.Length == 0) return all;
        var res = all.Where(_ => r.NextDouble() < frac).ToArray();
        return res.Length > 0 ? res : new[] { all[r.Next(all.Length)] };
    }

    /// <summary>
    /// 결과 메시 검사. 반환 = 문제 설명 목록(비어 있으면 정상).
    /// </summary>
    public static List<string> Problems(PolyMesh m, bool allowIsolated = false)
    {
        var p = new List<string>(MeshValidator.Check(m).Take(3));
        var tmp = new List<int>();
        for (int f = 0; f < m.FaceCount; f++)
        {
            if (!m.Faces[f].Alive) continue;
            m.GetFaceVertices(f, tmp);
            if (tmp.Count < 3) p.Add($"face {f} degree {tmp.Count}");
            if (tmp.Distinct().Count() != tmp.Count) p.Add($"face {f} repeats a vertex ({string.Join(",", tmp)})");
            for (int i = 0; i < tmp.Count; i++)
            {
                int e = m.FindEdge(tmp[i], tmp[(i + 1) % tmp.Count]);
                if (e < 0 || !m.Edges[e].Alive) { p.Add($"face {f} edge not found"); break; }
            }
        }
        for (int v = 0; v < m.VertexCount; v++)
        {
            if (!m.Verts[v].Alive) continue;
            var pos = m.Verts[v].Position;
            if (!float.IsFinite(pos.X) || !float.IsFinite(pos.Y) || !float.IsFinite(pos.Z)) p.Add($"vertex {v} non-finite position");
            if (!allowIsolated && m.VertexOutgoing(v).Length == 0) p.Add($"vertex {v} isolated");
        }
        for (int h = 0; h < m.Hes.Count; h++)
            if (m.Hes[h].Alive && (!float.IsFinite(m.Hes[h].Uv0.X) || !float.IsFinite(m.Hes[h].Uv0.Y))) { p.Add($"he {h} non-finite uv"); break; }
        return p.Distinct().Take(6).ToList();
    }

    /// <summary>기본 메시 생성기(이름, 생성 함수).</summary>
    public static readonly (string name, Func<PolyMesh> make)[] Bases =
    {
        ("cube", () => MeshBuilder.Cube()),
        ("ngon", () => MeshBuilder.Polygon(Enumerable.Range(0, 6).Select(i => new Vector3(MathF.Cos(i * MathF.Tau / 6), 0, MathF.Sin(i * MathF.Tau / 6))).ToArray(), Vector3.UnitY)),
        ("plane3", () => MeshBuilder.Plane(1, 1, 3, 3)),
        ("cylCaps", () => MeshBuilder.Cylinder(0.5f, 1f, 8, true)),
        ("cylOpen", () => MeshBuilder.Cylinder(0.5f, 1f, 8, false)),
        ("cone", () => MeshBuilder.Cone(0.5f, 1f, 8, true)),
        ("sphere", () => MeshBuilder.Sphere(0.5f, 8, 6)),
        ("torus", () => MeshBuilder.Torus(0.5f, 0.2f, 8, 6)),
        ("boolean", () =>
        {
            var r = MeshOps.Boolean(MeshBuilder.Cube(), MeshBuilder.Cube(), Matrix4x4.CreateTranslation(0.5f, 0.5f, 0.5f), BooleanOperation.Difference, out _);
            MeshNormals.Recompute(r); return r;
        }),
        ("array", () =>
        {
            var m = MeshBuilder.Cube();
            MeshOps.MakeArray(m, new ArrayOptions { Count = 3, Relative = new Vector3(1, 0, 0), Merge = true, MergeDistance = 0.01f });
            MeshNormals.Recompute(m); return m;
        }),
    };

    /// <summary>연산 정의: 이름, 위상 보존(닫힘·오일러 유지) 여부, 적용 함수(메시, 난수).</summary>
    public static readonly (string name, bool preserves, Action<PolyMesh, Random> op)[] Ops =
    {
        ("ExtrudeFaces", true, (m, r) => MeshOps.ExtrudeFaces(m, Pick(r, Faces(m)))),
        ("ExtrudeIndividual", true, (m, r) => MeshOps.Extrude(m, Pick(r, Faces(m)), new ExtrudeOptions { Type = ExtrudeType.IndividualFaces, Offset = 0.1f })),
        ("ExtrudeRegionSteps", true, (m, r) => MeshOps.Extrude(m, Pick(r, Faces(m)), new ExtrudeOptions { Offset = 0.2f, Steps = 3, Direction = (ExtrudeDirection)r.Next(6) })),
        ("ExtrudeFlip", false, (m, r) => MeshOps.Extrude(m, Pick(r, Faces(m)), new ExtrudeOptions { Offset = 0.2f, FlipNormals = true })),
        ("ExtrudeAll", false, (m, r) => MeshOps.Extrude(m, Faces(m), new ExtrudeOptions { Offset = 0.2f })),
        ("ExtrudeEdges", false, (m, r) => MeshOps.ExtrudeEdges(m, Pick(r, Boundary(m)), new ExtrudeOptions { Offset = 0.2f, Direction = (ExtrudeDirection)r.Next(6) }, out _)),
        ("BevelLegacy", true, (m, r) => MeshOps.BevelEdges(m, Pick(r, Edges(m)), 0.05f, 1 + r.Next(3))),
        ("BevelOptions", true, (m, r) => MeshOps.Bevel(m, Pick(r, Edges(m)), new BevelOptions
        {
            Width = 0.05f, Segments = 1 + r.Next(4), WidthType = (BevelWidthType)r.Next(5), Intersection = (BevelIntersection)r.Next(3),
            MiterOuter = (BevelMiter)r.Next(3), MiterInner = (BevelMiter)r.Next(2) == 0 ? BevelMiter.Sharp : BevelMiter.Arc, HardenNormals = r.Next(2) == 0,
            ClampOverlap = r.Next(2) == 0, LoopSlide = r.Next(2) == 0, MarkSeams = r.Next(2) == 0, MarkSharp = r.Next(2) == 0,
            FaceStrength = (BevelFaceStrength)r.Next(4), ProfileType = (BevelProfileType)r.Next(2), Preset = (BevelProfilePreset)r.Next(5), Shape = (float)r.NextDouble(),
        })),
        ("BevelVertices", true, (m, r) => MeshOps.Bevel(m, Pick(r, Verts(m)), new BevelOptions { Affect = BevelAffect.Vertices, Width = 0.05f, Segments = 1 + r.Next(3) })),
        ("BevelAll", true, (m, r) => MeshOps.Bevel(m, Edges(m), new BevelOptions { Width = 0.03f, Segments = 1 + r.Next(2) })),
        ("DeleteFaces", false, (m, r) => MeshOps.DeleteFaces(m, Pick(r, Faces(m), 0.2))),
        ("DeleteEdges", false, (m, r) => MeshOps.DeleteEdges(m, Pick(r, Edges(m), 0.15))),
        ("DeleteVertices", false, (m, r) => MeshOps.DeleteVertices(m, Pick(r, Verts(m), 0.15))),
        ("MergeVertices", false, (m, r) => MeshOps.MergeVertices(m, Pick(r, Verts(m), 0.5), 0.3f)),
        ("Poke", true, (m, r) => MeshOps.Poke(m, Pick(r, Faces(m)), 0.1f)),
        ("CollapseEdges", false, (m, r) => MeshOps.CollapseEdges(m, Pick(r, Edges(m), 0.15))),
        ("CollapseFaces", false, (m, r) => MeshOps.CollapseFaces(m, Pick(r, Faces(m), 0.15))),
        ("MergeToCenter", false, (m, r) => MeshOps.MergeToCenter(m, Pick(r, Verts(m), 0.2))),
        ("AverageVertices", true, (m, r) => MeshOps.AverageVertices(m, Pick(r, Verts(m)), 3, 0.5f)),
        ("DetachVertices", false, (m, r) => MeshOps.DetachVertices(m, Pick(r, Verts(m), 0.2))),
        ("DetachFaces", false, (m, r) => MeshOps.DetachFaces(m, Pick(r, Faces(m)))),
        ("DuplicateFaces", false, (m, r) => MeshOps.DuplicateFaces(m, Pick(r, Faces(m)))),
        ("ChamferVertices", true, (m, r) => MeshOps.ChamferVertices(m, Pick(r, Verts(m), 0.2), 0.1f, false)),
        ("ChamferVerticesRemove", false, (m, r) => MeshOps.ChamferVertices(m, Pick(r, Verts(m), 0.2), 0.1f, true)),
        ("ConnectVertices", true, (m, r) => MeshOps.ConnectVertices(m, Pick(r, Verts(m), 0.4))),
        ("ConnectEdges", true, (m, r) => MeshOps.ConnectEdges(m, Pick(r, Edges(m), 0.3))),
        ("FlipTriangleEdges", true, (m, r) => MeshOps.FlipTriangleEdges(m, Pick(r, Edges(m), 0.3))),
        ("SpinForward", true, (m, r) => MeshOps.SpinEdges(m, Pick(r, Edges(m), 0.2), true)),
        ("SpinBackward", true, (m, r) => MeshOps.SpinEdges(m, Pick(r, Edges(m), 0.2), false)),
        ("Wedge", false, (m, r) =>
        {
            var fs = Pick(r, Faces(m), 0.1); var tmp = new List<int>(); m.GetFaceVertices(fs[0], tmp);
            int e = m.FindEdge(tmp[0], tmp[1]); MeshOps.Wedge(m, new[] { fs[0] }, e, 90f, 1 + r.Next(4));
        }),
        ("Circularize", true, (m, r) => MeshOps.Circularize(m, Pick(r, Verts(m), 0.5), 0.1f, r.Next(2) == 0)),
        ("OffsetEdgeLoop", true, (m, r) => MeshOps.OffsetEdgeLoop(m, Pick(r, Edges(m), 0.1), 0.05f)),
        ("SlideEdges", true, (m, r) => MeshOps.SlideEdges(m, Pick(r, Edges(m), 0.2), (float)(r.NextDouble() * 1.8 - 0.9))),
        ("SymmetrizeVertices", true, (m, r) => MeshOps.SymmetrizeVertices(m, Verts(m), r.Next(3), 0f, 0.01f)),
        ("FlipVertices", true, (m, r) => MeshOps.FlipVertices(m, Verts(m), r.Next(3), 0f, 0.01f)),
        ("FillHoles", false, (m, r) => MeshOps.FillHoles(m, Edges(m))),
        ("Triangulate", true, (m, r) => MeshOps.Triangulate(m, Pick(r, Faces(m), 0.5))),
        ("Quadrangulate", true, (m, r) => { MeshOps.Triangulate(m, Faces(m)); MeshOps.Quadrangulate(m, Faces(m), 30f); }),
        ("Mirror", false, (m, r) => MeshOps.MirrorGeometry(m, r.Next(3), 0.25f, r.Next(2) == 0, false, 0.001f)),
        ("MirrorCut", false, (m, r) => MeshOps.MirrorGeometry(m, r.Next(3), 0.1f, r.Next(2) == 0, true, 0.001f)),
        ("Symmetrize0", false, (m, r) => MeshOps.MirrorGeometry(m, r.Next(3), 0f, r.Next(2) == 0, true, 0.001f)),
        ("Cleanup", false, (m, r) => MeshOps.Cleanup(m)),
        ("Slice", true, (m, r) => MeshOps.SliceWithPlane(m, new Vector3(0.05f, 0.03f, 0.02f), Vector3.Normalize(new Vector3((float)r.NextDouble() - 0.5f, (float)r.NextDouble() + 0.1f, (float)r.NextDouble() - 0.5f)))),
        ("Crease", true, (m, r) => MeshOps.SetCrease(m, Pick(r, Edges(m)), 2f)),
        ("AddDivQuads", true, (m, r) => MeshOps.AddDivisions(m, Pick(r, Faces(m)), 1 + r.Next(2), MeshOps.DivisionMode.Quads)),
        ("AddDivTris", true, (m, r) => MeshOps.AddDivisions(m, Pick(r, Faces(m)), 1, MeshOps.DivisionMode.Triangles)),
        ("AddDivLinear", true, (m, r) => MeshOps.AddDivisionsLinear(m, Pick(r, Faces(m)), 1 + r.Next(3), 1 + r.Next(3))),
        ("DivideEdges", true, (m, r) => MeshOps.DivideEdges(m, Pick(r, Edges(m)), 1 + r.Next(3))),
        ("Smooth", true, (m, r) => MeshOps.Smooth(m, 1)),
        ("InsertEdgeLoop", true, (m, r) => MeshOps.InsertEdgeLoop(m, Pick(r, Edges(m), 0.05)[0], (float)r.NextDouble() * 0.8f + 0.1f)),
        ("Bridge", false, (m, r) => MeshOps.BridgeEdges(m, Boundary(m))),
        ("Reverse", true, (m, r) => MeshOps.ReverseFaces(m, Pick(r, Faces(m)))),
        ("Normals", true, (m, r) =>
        {
            MeshOps.SoftenHardenByAngle(m, Edges(m), 30f); MeshOps.LockNormals(m, Pick(r, Verts(m))); MeshOps.SetNormalsToFace(m, Pick(r, Verts(m)));
            MeshOps.AverageNormals(m, Pick(r, Verts(m))); MeshOps.SetVertexNormal(m, Pick(r, Verts(m)), Vector3.UnitY); MeshOps.ConformNormals(m); MeshOps.UnlockNormals(m, Verts(m));
        }),
        ("ExtractFaces", false, (m, r) =>
        {
            var fs = Pick(r, Faces(m)); var ex = MeshOps.ExtractFaces(m, fs);
            var pr = Problems(ex); if (pr.Count > 0) throw new InvalidOperationException("extracted: " + string.Join("; ", pr));
        }),
    };

    /// <summary>xUnit 데이터: 연산 이름.</summary>
    public static IEnumerable<object[]> OpNames => Ops.Select(o => new object[] { o.name });

    /// <summary>각 연산 × 각 기본 메시 × 시드 8개: 결과 건전성, 위상 보존 연산은 닫힘·오일러 유지.</summary>
    [Theory]
    [MemberData(nameof(OpNames))]
    public void EachOpOnEachBase(string opName)
    {
        var op = Ops.First(o => o.name == opName);
        var fails = new List<string>();
        foreach (var (bname, make) in Bases)
            for (int seed = 0; seed < 8; seed++)
            {
                var m = make(); MeshNormals.Recompute(m);
                bool closed = Closed(m); int euler = Euler(m);
                try
                {
                    op.op(m, new Random(seed * 7919 + bname.Length));
                    MeshNormals.Recompute(m);
                    var pr = Problems(m);
                    if (op.preserves && closed && pr.Count == 0)
                    {
                        if (!Closed(m)) pr.Add("closed input became open");
                        else if (Euler(m) != euler) pr.Add($"euler {euler} -> {Euler(m)}");
                    }
                    if (pr.Count > 0) fails.Add($"{bname}#{seed}: {string.Join("; ", pr)}");
                    // 다시 한 번(연쇄) + 복제/압축 왕복
                    var c = m.Clone(); c.Compact(); var pc = Problems(c);
                    if (pc.Count > 0) fails.Add($"{bname}#{seed} compact: {string.Join("; ", pc)}");
                }
                catch (Exception ex) { fails.Add($"{bname}#{seed}: EXCEPTION {ex.GetType().Name}: {ex.Message.Split('\n')[0]} @ {ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}"); }
            }
        foreach (var f in fails) _out.WriteLine(f);
        Assert.True(fails.Count == 0, $"{opName}: {fails.Count} failure(s)\n" + string.Join("\n", fails.Take(12)));
    }

    /// <summary>한 팬으로 이어지지 않는(꼬집힌) 비매니폴드 정점 수. 이런 입력에서는 Bevel/Chamfer가 닫힘을 보장하지 않는다(알려진 제한).</summary>
    public static int NonManifoldVertices(PolyMesh m)
    {
        int bad = 0;
        for (int v = 0; v < m.VertexCount; v++)
        {
            if (!m.Verts[v].Alive) continue;
            var outs = m.VertexOutgoing(v).ToArray(); if (outs.Length == 0) continue;
            int start = outs.FirstOrDefault(h => m.Hes[m.Hes[h].Prev].Twin < 0, outs[0]);
            int cur = start, n = 0;
            do { n++; int tw = m.Hes[cur].Twin; if (tw < 0) break; cur = m.Hes[tw].Next; } while (cur != start && n < 1000);
            if (n != outs.Length) bad++;
        }
        return bad;
    }

    /// <summary>
    /// 무작위 연쇄: 기본 메시마다 연산 6개를 무작위로 이어 적용(시드 60개). 매 단계 건전성, 그리고 닫힌 매니폴드 입력에
    /// 위상 보존 연산을 적용했을 때 열리지 않는지 검사한다.
    /// </summary>
    [Fact]
    public void RandomChains()
    {
        var fails = new List<string>();
        foreach (var (bname, make) in Bases)
            for (int seed = 0; seed < 60; seed++)
            {
                var r = new Random(seed * 31 + bname.Sum(ch => (int)ch));
                var m = make(); MeshNormals.Recompute(m);
                var trail = new List<string>();
                try
                {
                    for (int k = 0; k < 6; k++)
                    {
                        if (m.AliveFaceCount == 0) break;
                        var op = Ops[r.Next(Ops.Length)]; trail.Add(op.name);
                        if (m.AliveFaceCount > 1500) break;
                        bool closedManifold = Closed(m) && NonManifoldVertices(m) == 0;
                        op.op(m, r); MeshNormals.Recompute(m);
                        var pr = Problems(m);
                        if (pr.Count == 0 && op.preserves && closedManifold && !Closed(m)) pr.Add("closed manifold input became open");
                        if (pr.Count > 0) { fails.Add($"{bname}#{seed} [{string.Join(">", trail)}]: {string.Join("; ", pr)}"); break; }
                    }
                }
                catch (Exception ex) { fails.Add($"{bname}#{seed} [{string.Join(">", trail)}]: EXCEPTION {ex.GetType().Name}: {ex.Message.Split('\n')[0]} @ {ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}"); }
            }
        foreach (var f in fails) _out.WriteLine(f);
        Assert.True(fails.Count == 0, $"{fails.Count} chain failure(s)\n" + string.Join("\n", fails.Take(15)));
    }

    /// <summary>
    /// .cube 왕복: 각 연산 결과(시드 2개, 기본 메시 전부)를 문서에 넣어 저장 → 열기 한 뒤 위상 수·정점 위치 집합·엣지 플래그(하드/심/크리즈)·
    /// 코너 UV 집합·잠긴 코너 노멀 수가 같은지 본다(.cube는 Compact된 메시를 쓰므로 ID가 아니라 집합으로 비교).
    /// </summary>
    [Theory]
    [MemberData(nameof(OpNames))]
    public void CubeFileRoundTripAfterOp(string opName)
    {
        var op = Ops.First(o => o.name == opName);
        var fails = new List<string>();
        foreach (var (bname, make) in Bases)
            for (int seed = 0; seed < 2; seed++)
            {
                var m = make(); MeshNormals.Recompute(m);
                try { op.op(m, new Random(seed * 7919 + bname.Length)); MeshNormals.Recompute(m); }
                catch { continue; }
                if (m.AliveFaceCount == 0) continue;
                var doc = new Document();
                var node = new SceneNode { Name = "n", Shape = new MeshShape(m) };
                doc.AddNode(node);
                var doc2 = new Document();
                CubeFileFormat.Deserialize(doc2, CubeFileFormat.Serialize(doc));
                var m2 = doc2.Root.Children[0].Mesh!;
                string Sig(PolyMesh x)
                {
                    var pos = Enumerable.Range(0, x.VertexCount).Where(v => x.Verts[v].Alive).Select(v => x.Verts[v].Position).Select(p => $"{p.X:F4},{p.Y:F4},{p.Z:F4}").OrderBy(t => t, StringComparer.Ordinal);
                    var flags = Enumerable.Range(0, x.EdgeCount).Where(e => x.Edges[e].Alive).Select(e => { var (a, b) = x.EdgeVertices(e); var pa = x.Verts[a].Position; var pb = x.Verts[b].Position; var k = string.CompareOrdinal($"{pa}", $"{pb}") < 0 ? $"{pa}{pb}" : $"{pb}{pa}"; return $"{k}:{(x.Edges[e].Hard ? 1 : 0)}{(x.Edges[e].Seam ? 1 : 0)}{x.Edges[e].Crease:F2}"; }).OrderBy(t => t, StringComparer.Ordinal);
                    var uvs = Enumerable.Range(0, x.Hes.Count).Where(h => x.Hes[h].Alive && x.Faces[x.Hes[h].Face].Alive).Select(h => $"{x.Verts[x.Hes[h].Vertex].Position}:{x.Hes[h].Uv0.X:F4},{x.Hes[h].Uv0.Y:F4}:{(x.Hes[h].NormalLocked ? 1 : 0)}").OrderBy(t => t, StringComparer.Ordinal);
                    return $"V{x.AliveVertexCount} E{x.AliveEdgeCount} F{x.AliveFaceCount}|{string.Join(";", pos)}|{string.Join(";", flags)}|{string.Join(";", uvs)}|L{x.LockedNormals.Count}";
                }
                string s1 = Sig(m), s2 = Sig(m2);
                if (s1 != s2)
                {
                    int i = 0; while (i < Math.Min(s1.Length, s2.Length) && s1[i] == s2[i]) i++;
                    fails.Add($"{bname}#{seed}: differs at {i}: '{s1.Substring(Math.Max(0, i - 40), Math.Min(80, s1.Length - Math.Max(0, i - 40)))}' vs '{s2.Substring(Math.Max(0, i - 40), Math.Min(80, s2.Length - Math.Max(0, i - 40)))}'");
                }
                var pr = Problems(m2); if (pr.Count > 0) fails.Add($"{bname}#{seed} loaded: {string.Join("; ", pr)}");
            }
        Assert.True(fails.Count == 0, $"{opName}: {fails.Count} " + string.Join(" || ", fails.Take(6)));
    }
}
