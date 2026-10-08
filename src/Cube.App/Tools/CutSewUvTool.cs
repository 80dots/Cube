using Cube.Core.Commands;
using Cube.Core.Selection;
using Cube.Core.Uv;
using Godot;

namespace Cube.App.Tools;

/// <summary>Maya 3D Cut and Sew UV Tool: 뷰포트에서 엣지를 클릭하면 UV 심으로 자르고(Cut), Ctrl+클릭이면 꿰맨다(Sew). 드래그하면 지나간 엣지에 연속 적용.</summary>
/// <remarks>
/// 엣지 하나마다 별도 UvEditCommand(Undo 한 단계)로 <see cref="UvOps.CutEdges"/>/<see cref="UvOps.SewEdges"/>를 실행하고,
/// 한 스트로크에서 같은 엣지는 한 번만 처리한다. 이미 원하는 상태(심이면 Cut 생략, 심이 아니면 Sew 생략)인 엣지는 건너뛴다.
/// </remarks>
public sealed class CutSewUvTool : SelectTool
{
    /// <summary>툴 ID("cutSewUv").</summary>
    public override string Id => "cutSewUv";
    /// <summary>표시 이름.</summary>
    public override string Label => "3D Cut and Sew UV";
    /// <summary>헬프 라인 안내.</summary>
    public override string HelpText => "3D Cut and Sew UV Tool: click or drag over edges to cut UV seams, Ctrl+click to sew. Q returns to Select.";

    /// <summary>_painting = 왼쪽 버튼 드래그 중, _sew = 이번 스트로크가 꿰매기(Ctrl), _done = 이번 스트로크에서 처리한 (노드, 엣지).</summary>
    private bool _painting; private bool _sew; private readonly HashSet<(Core.Scene.NodeId, int)> _done = new();

    /// <summary>활성화: 엣지 모드로 전환.</summary>
    public override void Activate(ToolContext ctx) { base.Activate(ctx); if (ctx.Sel.Mode != SelectMode.Edge) ctx.Sel.Mode = SelectMode.Edge; }
    /// <summary>비활성화: 스트로크 상태 해제.</summary>
    public override void Deactivate() { base.Deactivate(); _painting = false; }

    /// <summary>왼쪽 누름: 스트로크 시작(Ctrl = Sew)하고 커서 아래 엣지에 바로 적용. 선택 처리는 하지 않는다.</summary>
    protected override bool OnPrimaryPress(InputEventMouseButton mb)
    {
        _painting = true; _sew = mb.CtrlPressed; _done.Clear();
        Apply(mb.Position);
        return true;
    }

    /// <summary>스트로크 중 이동 = 지나간 엣지에 적용 + 호버 갱신, 뗌 = 스트로크 종료. 그 외는 SelectTool.</summary>
    public override bool HandleInput(InputEvent e)
    {
        if (_painting)
        {
            if (e is InputEventMouseMotion mm) { Apply(mm.Position); UpdateHover(mm.Position); return true; }
            if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false }) { _painting = false; return true; }
        }
        return base.HandleInput(e);
    }

    /// <summary>커서 아래 엣지를 Cut/Sew하고 UV 편집기를 다시 그리게 한다.</summary>
    private void Apply(Vector2 px)
    {
        // 엣지 피킹, 이번 스트로크에서 이미 처리한 엣지면 무시
        var hit = Picker.Pick(px, SelectMode.Edge, Ctx.CameraBasedSelection);
        if (hit == null) return;
        var key = (hit.Value.Node, hit.Value.Component);
        if (!_done.Add(key)) return;
        int edge = hit.Value.Component; bool sew = _sew;
        var node = Ctx.Doc.Find(hit.Value.Node); if (node?.Mesh == null) return;
        // 범위 밖/죽은 엣지는 무시
        if (edge >= node.Mesh.EdgeCount || node.Mesh.Edges[edge].He1 < 0) return;
        if (node.Mesh.Edges[edge].Seam == !sew) return; // 이미 그 상태
        var cmd = new UvEditCommand(sew ? "Sew UV Edge" : "Cut UV Edge", node.Id, m => { if (sew) UvOps.SewEdges(m, new[] { edge }); else UvOps.CutEdges(m, new[] { edge }); });
        Ctx.Undo.Push(cmd);
        UI.Shell.Instance.UvEditorWindow?.Canvas.Invalidate();
    }
}
