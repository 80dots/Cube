using Cube.Core.Commands;
using Cube.Core.Mesh;
using Cube.Core.Scene;
using Cube.Core.Selection;
using Godot;
using NVec2 = System.Numerics.Vector2;
using NVec3 = System.Numerics.Vector3;

namespace Cube.App.Tools;

/// <summary>Maya Target Weld Tool: 정점을 눌러 다른 정점 위로 끌어다 놓으면 그 정점 자리로 합쳐진다(같은 메시 안에서만).</summary>
public sealed class TargetWeldTool : SelectTool
{
    public override string Id => "targetWeld";
    public override string Label => "Target Weld";
    public override string HelpText => "Target Weld: drag a vertex onto another vertex of the same mesh to merge them. Q returns to Select.";

    private NodeId _node = NodeId.None; private int _source = -1; private bool _dragging; private NVec2 _px;

    public override void Activate(ToolContext ctx)
    {
        base.Activate(ctx);
        if (ctx.Sel.Mode != SelectMode.Vertex) ctx.Sel.Mode = SelectMode.Vertex;
    }

    public override void Deactivate() { base.Deactivate(); End(); }
    public override void Cancel() { End(); base.Cancel(); }
    private void End() { _dragging = false; _source = -1; _node = NodeId.None; foreach (var p in UI.Shell.Instance.Layout.Panels) p.Overlay.Polyline = null; }

    protected override bool OnPrimaryPress(InputEventMouseButton mb)
    {
        var hit = Picker.Pick(mb.Position, SelectMode.Vertex, Ctx.CameraBasedSelection);
        if (hit == null) return false;
        _node = hit.Value.Node; _source = hit.Value.Component; _dragging = true; _px = new NVec2(mb.Position.X, mb.Position.Y);
        Ctx.Sel.SelectComponents(_node, SelectMode.Vertex, new[] { _source }, replace: true);
        return true;
    }

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
                        var target = Picker.Pick(mb.Position, SelectMode.Vertex, Ctx.CameraBasedSelection);
                        int src = _source; var nodeId = _node;
                        End();
                        if (target == null || target.Value.Node != nodeId || target.Value.Component == src) { Ctx.SetHelp?.Invoke("Target Weld: release over another vertex of the same mesh."); return true; }
                        int dst = target.Value.Component;
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
