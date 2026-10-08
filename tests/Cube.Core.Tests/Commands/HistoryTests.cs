using System.Numerics;
using Cube.Core.Commands;
using Cube.Core.Mesh;
using Cube.Core.Scene;
using Cube.Core.Selection;

namespace Cube.Core.Tests.Commands;

/// <summary>
/// 구성 이력(Maya construction history)과 Catmull-Clark 서브디비전을 검증한다.
/// 히스토리 항목의 파라미터를 고치면 그 항목부터 끝까지 다시 실행(Replay)되고, Undo로 되돌릴 수 있어야 한다.
/// </summary>
public class HistoryTests
{
    /// <summary>
    /// 큐브를 한 번 Catmull-Clark하면 쿼드 24개, 정점 26개(원래 8 + 엣지 점 12 + 면 점 6)가 되고 매니폴드·오일러 2를 유지해야 한다.
    /// 스무딩으로 형태가 수축해 원점에서 가장 먼 정점 거리가 0.5~0.8 사이인지도 확인한다.
    /// </summary>
    [Fact]
    public void CatmullClark_Cube_Gives24Quads_AndStaysManifold()
    {
        var m = MeshBuilder.Cube();
        var s = MeshOps.CatmullClark(m);
        Assert.Equal(24, s.AliveFaceCount);
        Assert.Equal(26, s.AliveVertexCount); // 8 + 12 + 6
        Assert.Empty(MeshValidator.Check(s));
        Assert.Equal(2, s.AliveVertexCount - s.AliveEdgeCount + s.AliveFaceCount);
        // 수축: 모서리 정점이 안쪽으로 들어온다(원래 코너 거리 0.866 미만), 면 점은 면 위(0.5)에 남는다
        float maxLen = s.Verts.Where(v => v.Alive).Max(v => v.Position.Length());
        Assert.True(maxLen < 0.8f && maxLen > 0.5f, $"max {maxLen}");
    }

    /// <summary>
    /// 경계가 있는 2x2 평면을 서브디비전하면 16면이 되고, 경계 코너(2가 정점) (1,0,1)은 위치가 고정되어야 한다(경계 규칙).
    /// </summary>
    [Fact]
    public void CatmullClark_OpenPlane_KeepsBoundary()
    {
        var m = MeshBuilder.Plane(2, 2, 2, 2);
        var s = MeshOps.CatmullClark(m);
        Assert.Equal(16, s.AliveFaceCount);
        Assert.Empty(MeshValidator.Check(s));
        // 경계 코너(2가)는 고정
        Assert.Contains(s.Verts, v => v.Alive && MathF.Abs(v.Position.X - 1f) < 1e-5f && MathF.Abs(v.Position.Z - 1f) < 1e-5f);
    }

    /// <summary>
    /// 큐브 전체 엣지를 Distance 0.1로 베벨한 히스토리 항목을 만든 뒤 Distance를 0.2로 편집하면 메시가 다시 계산되고(면 수 26 유지,
    /// 오프셋 성분 0.4 → 0.3), Undo 한 번에 0.1 상태로, 한 번 더 Undo하면 베벨 자체가 사라져 원래 큐브(6면)로 돌아오는지 확인한다.
    /// </summary>
    [Fact]
    public void BevelHistory_EditDistance_Reevaluates_AndUndoes()
    {
        var doc = new Document();
        // 준비: 큐브를 만들고 모든 엣지를 Distance 파라미터가 있는 MeshOpCommand(Bevel)로 베벨한다.
        var cube = CreatePrimitiveCommand.Cube(doc); doc.Undo.Push(cube);
        var node = cube.Node; var mesh = node.Mesh!;
        var edges = Enumerable.Range(0, mesh.EdgeCount).ToArray();
        doc.Undo.Push(new MeshOpCommand("Bevel", node.Id, new HistoryParams(HistoryParam.F("Distance", 0.1f, 0, 10)),
            (m, p) => { var f = MeshOps.BevelEdges(m, edges, p.Float("Distance")); return (f.Count > 0, SelectMode.Face, f); }));
        Assert.Single(node.MeshShape!.History);
        Assert.Equal(26, mesh.AliveFaceCount);
        // 베벨 오프셋: 새 정점은 한 성분이 0.5 - distance 가 된다 → 0이 아닌 성분 중 최소 절댓값
        // 0.05보다 큰 좌표 성분 중 최솟값 = 베벨로 안쪽에 생긴 정점 성분(0.5 - distance). 이름과 달리 "오프셋 지표"로 쓴다.
        float Max() => mesh.Verts.Where(v => v.Alive).SelectMany(v => new[] { v.Position.X, v.Position.Y, v.Position.Z }).Select(MathF.Abs).Where(c => c > 0.05f).Min();
        Assert.True(MathF.Abs(Max() - 0.4f) < 1e-4f, $"max {Max()}");

        // 히스토리 파라미터 편집: EditHistoryCommand가 그 항목부터 Replay한다.
        var p2 = node.MeshShape.History[0].Params.Clone(); p2["Distance"].Float = 0.2f;
        doc.Undo.Push(new EditHistoryCommand(node.Id, 0, p2));
        Assert.Equal(26, mesh.AliveFaceCount);
        Assert.True(MathF.Abs(Max() - 0.3f) < 1e-4f, $"max {Max()} after edit");
        Assert.Equal(0.2f, node.MeshShape.History[0].Params.Float("Distance"), 4);

        doc.Undo.Undo();
        Assert.True(MathF.Abs(Max() - 0.4f) < 1e-4f, $"max {Max()} after undo");
        Assert.Equal(0.1f, node.MeshShape.History[0].Params.Float("Distance"), 4);
        doc.Undo.Undo();
        Assert.Empty(node.MeshShape.History);
        Assert.Equal(6, mesh.AliveFaceCount);
    }

    /// <summary>
    /// 컴포넌트 이동(윗면 +Y 1) 히스토리 뒤에 베벨 히스토리가 있을 때, 앞 항목의 Translate를 (0,2,0)으로 바꾸면
    /// 뒤의 베벨까지 다시 적용되어 최대 Y가 1.5 → 2.5로 바뀌고 면 수(26)가 유지되는지 확인한다.
    /// ID가 슬롯 인덱스라 Replay 후에도 뒤 항목의 컴포넌트 참조가 유효함을 보장하는 테스트다.
    /// </summary>
    [Fact]
    public void MoveVerticesHistory_EditTranslate_ReplaysLaterEntries()
    {
        var doc = new Document();
        var cube = CreatePrimitiveCommand.Cube(doc); doc.Undo.Push(cube);
        var node = cube.Node; var mesh = node.Mesh!;
        // 윗면 정점 4개를 +Y 로 1 이동 (히스토리 op 포함)
        var top = Enumerable.Range(0, mesh.VertexCount).Where(v => mesh.Verts[v].Position.Y > 0).ToArray();
        var before = top.Select(v => mesh.Verts[v].Position).ToArray();
        var after = before.Select(p => p + Vector3.UnitY).ToArray();
        foreach (var (v, p) in top.Zip(after)) { var vert = mesh.Verts[v]; vert.Position = p; mesh.Verts[v] = vert; }
        var op = new ComponentTransformOp { Type = ComponentTransformOp.Kind.Move, MeshWorld = node.WorldMatrix };
        doc.Undo.Push(new MoveVerticesCommand("Move", node.Id, top, before, after, op, op.DefaultParams(Vector3.UnitY, 0, Vector3.One)), alreadyApplied: true);
        // 그 뒤 전체 베벨
        var edges = Enumerable.Range(0, mesh.EdgeCount).ToArray();
        doc.Undo.Push(new MeshOpCommand("Bevel", node.Id, new HistoryParams(HistoryParam.F("Distance", 0.1f, 0, 10)),
            (m, p) => { var f = MeshOps.BevelEdges(m, edges, p.Float("Distance")); return (f.Count > 0, SelectMode.Face, f); }));
        Assert.Equal(2, node.MeshShape!.History.Count);
        float maxY = mesh.Verts.Where(v => v.Alive).Max(v => v.Position.Y);
        Assert.True(MathF.Abs(maxY - 1.5f) < 1e-4f, $"maxY {maxY}");
        // 이동량을 2로 바꾸면 베벨이 다시 적용된 뒤 최대 y = 2.4
        var p2 = node.MeshShape.History[0].Params.Clone(); p2["Translate"].Value = new Vector3(0, 2, 0);
        doc.Undo.Push(new EditHistoryCommand(node.Id, 0, p2));
        maxY = mesh.Verts.Where(v => v.Alive).Max(v => v.Position.Y);
        Assert.True(MathF.Abs(maxY - 2.5f) < 1e-4f, $"maxY {maxY} after edit");
        Assert.Equal(26, mesh.AliveFaceCount);
    }
}
