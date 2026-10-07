using System.Numerics;
using Cube.Core.Commands;
using Cube.Core.Mesh;
using Cube.Core.Selection;
using Godot;
using NVec2 = System.Numerics.Vector2;
using NVec3 = System.Numerics.Vector3;

namespace Cube.App.Tools;

/// <summary>
/// Maya Insert Edge Loop Tool: 엣지 위를 클릭하면 그 지점(엣지 위 비율)을 지나는 엣지 루프를 끼운다.
/// 엣지 모드로 전환해 호버 프리셀렉션을 보여 주고, 빈 곳 클릭은 일반 선택으로 동작한다.
/// </summary>
public sealed class InsertEdgeLoopTool : SelectTool
{
    public override string Id => "insertLoop";
    public override string Label => "Insert Edge Loop";
    public override string HelpText => "Insert Edge Loop Tool: click an edge to insert a loop through its ring at that position. Q returns to Select.";

    public override void Activate(ToolContext ctx)
    {
        base.Activate(ctx);
        if (ctx.Sel.Mode != SelectMode.Edge) ctx.Sel.Mode = SelectMode.Edge;
    }

    protected override bool OnPrimaryPress(InputEventMouseButton mb)
    {
        var hit = Picker.Pick(mb.Position, SelectMode.Edge, Ctx.CameraBasedSelection);
        if (hit == null) return false;
        var node = Ctx.Doc.Find(hit.Value.Node);
        var mesh = node?.Mesh;
        if (node == null || mesh == null) return false;
        int edge = hit.Value.Component;
        if (edge < 0 || edge >= mesh.EdgeCount || !mesh.Edges[edge].Alive) return false;

        // 클릭 위치를 엣지 화면 투영에 사영해 비율 t 계산
        var (a, b) = mesh.EdgeVertices(edge);
        var world = node.WorldMatrix;
        var proj = Picker.Projection();
        var pa = proj.Project(NVec3.Transform(mesh.Verts[a].Position, world), out _);
        var pb = proj.Project(NVec3.Transform(mesh.Verts[b].Position, world), out _);
        float t = 0.5f;
        if (pa != null && pb != null)
        {
            var d = pb.Value - pa.Value; float len2 = d.LengthSquared();
            var px = new NVec2(mb.Position.X, mb.Position.Y);
            if (len2 > 1e-6f) t = Math.Clamp(NVec2.Dot(px - pa.Value, d) / len2, 0.02f, 0.98f);
        }
        if (Ctx.Viewport.IsSnapHeld) t = 0.5f; // J: 중앙 스냅

        var cmd = new MeshOpCommand("Insert Edge Loop", node.Id, new HistoryParams(HistoryParam.F("Position", t, 0.01f, 0.99f, 0.01f)), (m, p) =>
        {
            var newEdges = MeshOps.InsertEdgeLoop(m, edge, p.Float("Position"));
            return (newEdges.Count > 0, SelectMode.Edge, newEdges);
        });
        Ctx.Undo.Push(cmd);
        if (!cmd.DidChange) Ctx.SetHelp?.Invoke("Insert Edge Loop: this edge has no quad ring to cut.");
        SetHover(null);
        return true;
    }
}
