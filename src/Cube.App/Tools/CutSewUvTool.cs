using Cube.Core.Commands;
using Cube.Core.Selection;
using Cube.Core.Uv;
using Godot;

namespace Cube.App.Tools;

/// <summary>Maya 3D Cut and Sew UV Tool: 뷰포트에서 엣지를 클릭하면 UV 심으로 자르고(Cut), Ctrl+클릭이면 꿰맨다(Sew). 드래그하면 지나간 엣지에 연속 적용.</summary>
public sealed class CutSewUvTool : SelectTool
{
    public override string Id => "cutSewUv";
    public override string Label => "3D Cut and Sew UV";
    public override string HelpText => "3D Cut and Sew UV Tool: click or drag over edges to cut UV seams, Ctrl+click to sew. Q returns to Select.";

    private bool _painting; private bool _sew; private readonly HashSet<(Core.Scene.NodeId, int)> _done = new();

    public override void Activate(ToolContext ctx) { base.Activate(ctx); if (ctx.Sel.Mode != SelectMode.Edge) ctx.Sel.Mode = SelectMode.Edge; }
    public override void Deactivate() { base.Deactivate(); _painting = false; }

    protected override bool OnPrimaryPress(InputEventMouseButton mb)
    {
        _painting = true; _sew = mb.CtrlPressed; _done.Clear();
        Apply(mb.Position);
        return true;
    }

    public override bool HandleInput(InputEvent e)
    {
        if (_painting)
        {
            if (e is InputEventMouseMotion mm) { Apply(mm.Position); UpdateHover(mm.Position); return true; }
            if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false }) { _painting = false; return true; }
        }
        return base.HandleInput(e);
    }

    private void Apply(Vector2 px)
    {
        var hit = Picker.Pick(px, SelectMode.Edge, Ctx.CameraBasedSelection);
        if (hit == null) return;
        var key = (hit.Value.Node, hit.Value.Component);
        if (!_done.Add(key)) return;
        int edge = hit.Value.Component; bool sew = _sew;
        var node = Ctx.Doc.Find(hit.Value.Node); if (node?.Mesh == null) return;
        if (edge >= node.Mesh.EdgeCount || node.Mesh.Edges[edge].He1 < 0) return;
        if (node.Mesh.Edges[edge].Seam == !sew) return; // 이미 그 상태
        var cmd = new UvEditCommand(sew ? "Sew UV Edge" : "Cut UV Edge", node.Id, m => { if (sew) UvOps.SewEdges(m, new[] { edge }); else UvOps.CutEdges(m, new[] { edge }); });
        Ctx.Undo.Push(cmd);
        UI.Shell.Instance.UvEditorWindow?.Canvas.Invalidate();
    }
}
