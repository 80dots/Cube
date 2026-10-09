using Cube.Core.Commands;
using Cube.Core.Mesh;
using Cube.Core.Scene;
using Xunit.Abstractions;

namespace Cube.Core.Tests.Mesh;

/// <summary>
/// 구성 이력 퍼즈(감사용): 앱과 같은 방식(MeshOpCommand + 파라미터)으로 연산 여러 개를 쌓은 뒤 앞쪽 항목의 파라미터를 바꿔
/// (EditHistoryCommand) 뒤 항목들이 다시 실행되어도 예외가 없고 메시가 건전한지, Undo가 편집 전 메시를 정확히 되돌리는지 본다.
/// </summary>
public class ModelingHistoryFuzzTests
{
    /// <summary>테스트 출력.</summary>
    private readonly ITestOutputHelper _out;
    /// <summary>xUnit이 출력 도우미를 넣어 준다.</summary>
    public ModelingHistoryFuzzTests(ITestOutputHelper output) { _out = output; }

    /// <summary>살아 있는 ID 목록.</summary>
    private static int[] Alive(PolyMesh m, int kind) => kind switch
    {
        0 => Enumerable.Range(0, m.VertexCount).Where(v => m.Verts[v].Alive).ToArray(),
        1 => Enumerable.Range(0, m.EdgeCount).Where(e => m.Edges[e].Alive).ToArray(),
        _ => Enumerable.Range(0, m.FaceCount).Where(f => m.Faces[f].Alive).ToArray(),
    };

    /// <summary>무작위 부분집합(최소 1개).</summary>
    private static int[] Pick(Random r, int[] all, double frac)
    {
        var res = all.Where(_ => r.NextDouble() < frac).ToArray();
        return res.Length > 0 || all.Length == 0 ? res : new[] { all[r.Next(all.Length)] };
    }

    /// <summary>파라미터가 있는 연산(앱의 액션과 같은 파라미터 이름) 하나를 만든다. 컴포넌트 ID는 실행 시점 값으로 고정(앱과 동일).</summary>
    private static MeshOpCommand MakeOp(NodeId id, PolyMesh m, Random r, out string name)
    {
        int k = r.Next(9);
        switch (k)
        {
            case 0:
                { var f = Pick(r, Alive(m, 2), 0.3); name = "Extrude"; return new MeshOpCommand(name, id, new HistoryParams(HistoryParam.F("Offset", 0.2f), HistoryParam.I("Steps", 1, 1, 10)), (mm, p) => { var c = MeshOps.Extrude(mm, f, new ExtrudeOptions { Offset = p.Float("Offset"), Steps = p.Int("Steps") }); return (c.Count > 0, null, null); }); }
            case 1:
                { var e = Pick(r, Alive(m, 1), 0.2); name = "Bevel"; return new MeshOpCommand(name, id, new HistoryParams(HistoryParam.F("Width", 0.05f), HistoryParam.I("Segments", 1, 1, 8)), (mm, p) => { var c = MeshOps.Bevel(mm, e, new BevelOptions { Width = p.Float("Width"), Segments = p.Int("Segments") }); return (c.Count > 0, null, null); }); }
            case 2:
                { int e = Pick(r, Alive(m, 1), 0.05)[0]; name = "Insert Edge Loop"; return new MeshOpCommand(name, id, new HistoryParams(HistoryParam.F("Position", 0.5f, 0.01f, 0.99f)), (mm, p) => { var c = MeshOps.InsertEdgeLoop(mm, e, p.Float("Position")); return (c.Count > 0, null, null); }); }
            case 3:
                { name = "Smooth"; return new MeshOpCommand(name, id, new HistoryParams(HistoryParam.I("Levels", 1, 0, 2)), (mm, p) => { MeshOps.Smooth(mm, p.Int("Levels")); return (true, null, null); }); }
            case 4:
                { var f = Pick(r, Alive(m, 2), 0.3); name = "Poke"; return new MeshOpCommand(name, id, new HistoryParams(HistoryParam.F("Offset", 0f)), (mm, p) => { var c = MeshOps.Poke(mm, f, p.Float("Offset")); return (c.Count > 0, null, null); }); }
            case 5:
                { var f = Pick(r, Alive(m, 2), 0.3); name = "Add Divisions"; return new MeshOpCommand(name, id, new HistoryParams(HistoryParam.I("Levels", 1, 1, 3)), (mm, p) => { var c = MeshOps.AddDivisions(mm, f, p.Int("Levels"), MeshOps.DivisionMode.Quads); return (c.Count > 0, null, null); }); }
            case 6:
                { var v = Pick(r, Alive(m, 0), 0.2); name = "Chamfer Vertices"; return new MeshOpCommand(name, id, new HistoryParams(HistoryParam.F("Width", 0.1f)), (mm, p) => { MeshOps.ChamferVertices(mm, v, p.Float("Width"), false); return (true, null, null); }); }
            case 7:
                { var e = Pick(r, Alive(m, 1), 0.1); name = "Offset Edge Loop"; return new MeshOpCommand(name, id, new HistoryParams(HistoryParam.F("Offset", 0.05f)), (mm, p) => { var c = MeshOps.OffsetEdgeLoop(mm, e, p.Float("Offset")); return (c.Count > 0, null, null); }); }
            default:
                { name = "Mirror"; int axis = r.Next(3); return new MeshOpCommand(name, id, new HistoryParams(HistoryParam.F("Plane", 0.5f), HistoryParam.F("Merge Threshold", 0.001f)), (mm, p) => { var c = MeshOps.MirrorGeometry(mm, axis, p.Float("Plane"), true, false, p.Float("Merge Threshold")); return (c.Count > 0, null, null); }); }
        }
    }

    /// <summary>무작위 파라미터 값(이름별 합리적 범위).</summary>
    private static float RandomValue(Random r, HistoryParam p) => p.Name switch
    {
        "Offset" => (float)(r.NextDouble() * 0.6 - 0.1),
        "Steps" => r.Next(1, 4),
        "Width" => (float)(r.NextDouble() * 0.3),
        "Segments" => r.Next(1, 5),
        "Position" => (float)(r.NextDouble() * 0.9 + 0.05),
        "Levels" => r.Next(0, 3),
        "Plane" => (float)(r.NextDouble() - 0.5),
        _ => (float)r.NextDouble() * 0.01f,
    };

    /// <summary>시드마다: 큐브에 연산 4개 → 무작위 앞 항목 편집 → 건전성 → Undo가 편집 전으로 정확히 복원 → Redo가 편집 결과로 복원.</summary>
    [Fact]
    public void EditEarlierHistoryEntries()
    {
        var fails = new List<string>();
        for (int seed = 0; seed < 120; seed++)
        {
            var r = new Random(seed);
            var doc = new Document();
            var add = CreatePrimitiveCommand.Cube(doc); doc.Undo.Push(add);
            var node = add.Node; var trail = new List<string>();
            try
            {
                for (int k = 0; k < 4; k++)
                {
                    if (node.Mesh!.AliveFaceCount > 1500) break;
                    var cmd = MakeOp(node.Id, node.Mesh!, r, out var name); trail.Add(name);
                    doc.Undo.Push(cmd);
                }
                var hist = node.MeshShape!.History;
                if (hist.Count == 0) continue;
                for (int edit = 0; edit < 3; edit++)
                {
                    int idx = r.Next(hist.Count);
                    var np = hist[idx].Params.Clone();
                    if (np.Items.Count == 0) continue;
                    var prm = np.Items[r.Next(np.Items.Count)];
                    prm.Float = RandomValue(r, prm);
                    var before = node.Mesh!.Clone();
                    var ec = new EditHistoryCommand(node.Id, idx, np);
                    doc.Undo.Push(ec);
                    var pr = ModelingAuditFuzzTests.Problems(node.Mesh!);
                    if (pr.Count > 0) { fails.Add($"#{seed} [{string.Join(">", trail)}] edit {idx}:{hist[idx].Name}.{prm.Name}={prm.Float:F2}: {string.Join("; ", pr)}"); break; }
                    var after = node.Mesh!.Clone();
                    doc.Undo.Undo();
                    if (!Same(before, node.Mesh!)) { fails.Add($"#{seed} [{string.Join(">", trail)}] undo of edit {idx} did not restore the mesh"); break; }
                    doc.Undo.Redo();
                    if (!Same(after, node.Mesh!)) { fails.Add($"#{seed} [{string.Join(">", trail)}] redo of edit {idx} differs"); break; }
                }
            }
            catch (Exception ex) { fails.Add($"#{seed} [{string.Join(">", trail)}]: EXCEPTION {ex.GetType().Name}: {ex.Message} @ {ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}"); }
        }
        foreach (var f in fails) _out.WriteLine(f);
        Assert.True(fails.Count == 0, $"{fails.Count} failure(s): " + string.Join(" || ", fails.Take(8)));
    }

    /// <summary>두 메시가 같은지(살아 있는 정점 위치·면 정점 목록).</summary>
    private static bool Same(PolyMesh a, PolyMesh b)
    {
        if (a.AliveFaceCount != b.AliveFaceCount || a.AliveVertexCount != b.AliveVertexCount || a.VertexCount != b.VertexCount) return false;
        for (int v = 0; v < a.VertexCount; v++) if (a.Verts[v].Alive != b.Verts[v].Alive || a.Verts[v].Alive && a.Verts[v].Position != b.Verts[v].Position) return false;
        return true;
    }
}
