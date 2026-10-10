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
/// <remarks>
/// 위치 t는 클릭 점을 엣지의 화면 투영 선분에 사영한 비율(0.02~0.98로 제한, J 홀드면 0.5)이며,
/// 'Insert Edge Loop' 히스토리 항목의 Position 파라미터로 기록되어 나중에 Properties/Action Popup에서 바꿀 수 있다.
/// 새 루프 엣지들이 엣지 모드로 선택된다.
/// </remarks>
public sealed class InsertEdgeLoopTool : SelectTool
{
    /// <summary>툴 ID("insertLoop").</summary>
    public override string Id => "insertLoop";
    /// <summary>표시 이름.</summary>
    public override string Label => "Insert Edge Loop";
    /// <summary>헬프 라인 안내.</summary>
    public override string HelpText => "Insert Edge Loop Tool: click an edge to insert a loop through its ring at that position. Q returns to Select.";

    /// <summary>활성화: 엣지 프리셀렉션을 보이도록 엣지 모드로 전환.</summary>
    public override void Activate(ToolContext ctx)
    {
        base.Activate(ctx);
        if (ctx.Sel.Mode != SelectMode.Edge) ctx.Sel.Mode = SelectMode.Edge;
    }

    /// <summary>
    /// 왼쪽 누름: 커서 아래 살아 있는 엣지가 있으면 비율 t를 계산해 InsertEdgeLoop 명령을 푸시하고 true.
    /// 엣지가 없으면 false를 돌려 일반 선택(클릭/마키)으로 넘긴다.
    /// </summary>
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

        // 히스토리 파라미터(Position)를 가진 위상 명령: 재생 시 같은 시작 엣지에서 새 비율로 다시 끼운다
        // Symmetry: 거울 엣지에도 같은 비율(거울 위치를 그 엣지에 투영)로 루프를 넣는다(같은 명령 안, 같은 루프면 한 번만)
        var plane = UI.Shell.Instance.SymmetryPlaneFor(node);
        int mirrorEdge = -1; float mirrorT = t;
        if (plane != null) { var map = SymmetryMap.Get(mesh, plane); mirrorEdge = map.MirrorEdge(mesh, edge); if (mirrorEdge >= 0) mirrorT = SymmetryOps.MirrorParam(mesh, plane, edge, t, mirrorEdge); }
        var cmd = new MeshOpCommand("Insert Edge Loop", node.Id, new HistoryParams(HistoryParam.F("Position", t, 0.01f, 0.99f, 0.01f)), (m, p) =>
        {
            var newEdges = MeshOps.InsertEdgeLoop(m, edge, p.Float("Position"));
            if (mirrorEdge >= 0 && mirrorEdge != edge && !newEdges.Contains(mirrorEdge) && mirrorEdge < m.EdgeCount && m.Edges[mirrorEdge].Alive)
            {
                // 첫 루프가 거울 엣지를 이미 지났으면(링이 평면을 가로지름) 두 번째는 건너뛴다
                var ring = MeshOps.EdgeRing(m, mirrorEdge).entries.Select(x => x.edge);
                if (!ring.Any(re => newEdges.Contains(re))) newEdges.AddRange(MeshOps.InsertEdgeLoop(m, mirrorEdge, mirrorT + (p.Float("Position") - t)));
            }
            return (newEdges.Count > 0, SelectMode.Edge, newEdges);
        });
        Ctx.Undo.Push(cmd);
        if (!cmd.DidChange) Ctx.SetHelp?.Invoke("Insert Edge Loop: this edge has no quad ring to cut.");
        SetHover(null);
        return true;
    }
}
