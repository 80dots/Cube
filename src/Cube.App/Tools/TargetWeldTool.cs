using Cube.Core.Commands;
using Cube.Core.Mesh;
using Cube.Core.Scene;
using Cube.Core.Selection;
using Godot;
using NVec2 = System.Numerics.Vector2;
using NVec3 = System.Numerics.Vector3;

namespace Cube.App.Tools;

/// <summary>Maya Target Weld Tool: 정점을 눌러 다른 정점 위로 끌어다 놓으면 그 정점 자리로 합쳐진다(같은 메시 안에서만).</summary>
/// <remarks>
/// 놓을 때 원본 정점을 대상 정점 위치로 옮긴 뒤 <see cref="MeshOps.MergeVertices"/>(임계 1e-6)로 합치는 'Target Weld' MeshOpCommand를 푸시한다.
/// 비매니폴드가 될 경우 병합이 거부되어 아무 변화가 없다. 드래그 중에는 원본 정점 → 커서의 선을 오버레이로 그린다.
/// </remarks>
public sealed class TargetWeldTool : SelectTool
{
    /// <summary>툴 ID("targetWeld").</summary>
    public override string Id => "targetWeld";
    /// <summary>표시 이름.</summary>
    public override string Label => "Target Weld";
    /// <summary>헬프 라인 안내.</summary>
    public override string HelpText => "Target Weld: drag a vertex onto another vertex of the same mesh to merge them. Q returns to Select.";

    /// <summary>_node/_source = 끌고 있는 정점의 노드와 ID, _dragging = 드래그 중, _px = 누른 화면 위치.</summary>
    private NodeId _node = NodeId.None; private int _source = -1; private bool _dragging; private NVec2 _px;

    /// <summary>활성화: 정점 모드로 전환.</summary>
    public override void Activate(ToolContext ctx)
    {
        base.Activate(ctx);
        if (ctx.Sel.Mode != SelectMode.Vertex) ctx.Sel.Mode = SelectMode.Vertex;
    }

    /// <summary>비활성화: 드래그 상태·오버레이 정리.</summary>
    public override void Deactivate() { base.Deactivate(); End(); }
    /// <summary>취소: 드래그 상태 정리 후 기반 취소.</summary>
    public override void Cancel() { End(); base.Cancel(); }
    /// <summary>드래그 상태를 비우고 모든 패널의 오버레이 선을 지운다.</summary>
    private void End() { _dragging = false; _source = -1; _node = NodeId.None; foreach (var p in UI.Shell.Instance.Layout.Panels) p.Overlay.Polyline = null; }

    /// <summary>왼쪽 누름: 커서 아래 정점이 있으면 드래그를 시작하고 그 정점을 선택한다(없으면 일반 선택).</summary>
    protected override bool OnPrimaryPress(InputEventMouseButton mb)
    {
        var hit = Picker.Pick(mb.Position, SelectMode.Vertex, Ctx.CameraBasedSelection);
        if (hit == null) return false;
        _node = hit.Value.Node; _source = hit.Value.Component; _dragging = true; _px = new NVec2(mb.Position.X, mb.Position.Y);
        Ctx.Sel.SelectComponents(_node, SelectMode.Vertex, new[] { _source }, replace: true);
        return true;
    }

    /// <summary>드래그 중 이동 = 오버레이 선·호버 갱신, 뗌 = 같은 메시의 다른 정점 위면 병합 명령 실행. 그 외는 SelectTool.</summary>
    public override bool HandleInput(InputEvent e)
    {
        if (_dragging)
        {
            switch (e)
            {
                case InputEventMouseMotion mm:
                    {
                        var node = Ctx.Doc.Find(_node); var mesh = node?.Mesh;
                        if (node != null && mesh != null && _source < mesh.VertexCount)
                        {
                            var sp = Picker.Projection().Project(NVec3.Transform(mesh.Verts[_source].Position, node.WorldMatrix), out _);
                            Ctx.Viewport.Overlay.Polyline = sp != null ? new List<Godot.Vector2> { new(sp.Value.X, sp.Value.Y), new(mm.Position.X, mm.Position.Y) } : null;
                        }
                        UpdateHover(mm.Position);
                        return true;
                    }
                case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } mb:
                    {
                        // 놓은 위치의 정점을 찾고 드래그 상태는 먼저 정리
                        var target = Picker.Pick(mb.Position, SelectMode.Vertex, Ctx.CameraBasedSelection);
                        int src = _source; var nodeId = _node;
                        End();
                        if (target == null || target.Value.Node != nodeId || target.Value.Component == src) { Ctx.SetHelp?.Invoke("Target Weld: release over another vertex of the same mesh."); return true; }
                        int dst = target.Value.Component;
                        // 명령 본문: 두 정점이 살아 있으면 원본을 대상 위치로 옮긴 뒤 병합, 결과로 대상 정점을 선택
                        var cmd = new MeshOpCommand("Target Weld", nodeId, m =>
                        {
                            if (src >= m.VertexCount || dst >= m.VertexCount || !m.Verts[src].Alive || !m.Verts[dst].Alive) return (false, null, null);
                            var vs = m.Verts[src]; vs.Position = m.Verts[dst].Position; m.Verts[src] = vs;
                            int merged = MeshOps.MergeVertices(m, new[] { dst, src }, 1e-6f);
                            return (merged > 0, SelectMode.Vertex, new[] { dst });
                        });
                        Ctx.Undo.Push(cmd);
                        Ctx.SetHelp?.Invoke(cmd.DidChange ? "Target Weld: merged." : "Target Weld: could not merge (would create a non-manifold).");
                        return true;
                    }
            }
        }
        return base.HandleInput(e);
    }
}
