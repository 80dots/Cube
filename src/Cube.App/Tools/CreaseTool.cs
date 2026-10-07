using Cube.Core.Commands;
using Cube.Core.Mesh;
using Cube.Core.Scene;
using Cube.Core.Selection;
using Godot;

namespace Cube.App.Tools;

/// <summary>
/// Maya Crease Tool: 엣지(또는 정점)를 선택한 뒤 **MMB를 좌우로 드래그**해 크리즈 값을 올리고 내린다(Shift = 미세 조정).
/// 드래그 중에는 메시에 직접 값을 써서 보라색 엣지로 미리 보여 주고, 놓을 때 Crease 히스토리 항목(MeshOpCommand)으로 커밋한다.
/// LMB 클릭/마키/더블클릭 루프 선택은 SelectTool 그대로다.
/// </summary>
public sealed class CreaseTool : SelectTool
{
    public override string Id => "creaseTool";
    public override string Label => "Crease Tool";
    public override string HelpText => "Crease Tool: select edges, then drag with the middle mouse button left/right to set the crease (Shift = fine). Double-click selects a loop.";

    private const float Step = 0.02f;   // px당 크리즈 변화
    private bool _dragging;
    private float _pressX, _start, _value;
    private readonly List<(NodeId id, int[] edges, float[] before)> _targets = new();

    public override void Activate(ToolContext ctx)
    {
        base.Activate(ctx);
        if (ctx.Sel.Mode is not (SelectMode.Edge or SelectMode.Vertex)) ctx.Sel.Mode = SelectMode.Edge;
    }

    public override void Deactivate()
    {
        if (_dragging) EndDrag(commit: false);
        base.Deactivate();
    }

    public override void Cancel()
    {
        if (_dragging) EndDrag(commit: false);
        base.Cancel();
    }

    public override bool HandleInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Middle } mb when !mb.AltPressed || _dragging:
                if (mb.Pressed && !_dragging) { if (BeginDrag(mb.Position.X)) return true; return false; }
                if (!mb.Pressed && _dragging) { EndDrag(commit: true); return true; }
                return false;
            case InputEventMouseMotion mm when _dragging:
                {
                    float px = mm.Position.X;
                    float v = _start + (px - _pressX) * Step * (mm.ShiftPressed ? 0.1f : 1f);
                    SetValue(Mathf.Clamp(v, 0f, 10f));
                    return true;
                }
            case InputEventKey { Keycode: Key.Escape, Pressed: true } when _dragging:
                EndDrag(commit: false);
                return true;
        }
        return base.HandleInput(e);
    }

    private bool BeginDrag(float x)
    {
        _targets.Clear();
        var sel = Ctx.Sel; var doc = Ctx.Doc;
        float start = 0f;
        foreach (var id in sel.NodesWithComponents(sel.Mode))
        {
            var mesh = doc.Find(id)?.Mesh; if (mesh == null) continue;
            var comps = sel.GetComponents(id);
            var edges = (sel.Mode == SelectMode.Edge ? comps.Edges : SelectionOps.EdgesOfVertices(mesh, comps.Verts)).Where(ed => ed >= 0 && ed < mesh.EdgeCount && mesh.Edges[ed].Alive).Distinct().ToArray();
            if (edges.Length == 0) continue;
            var before = new float[edges.Length];
            for (int i = 0; i < edges.Length; i++) { before[i] = mesh.Edges[edges[i]].Crease; start = MathF.Max(start, before[i]); }
            _targets.Add((id, edges, before));
        }
        if (_targets.Count == 0) { Ctx.SetHelp?.Invoke("Crease Tool: select edges (or vertices) first, then MMB-drag."); return false; }
        _dragging = true; _pressX = x; _start = start; _value = start;
        SetValue(start);
        return true;
    }

    private void SetValue(float v)
    {
        _value = v;
        var doc = Ctx.Doc;
        foreach (var (id, edges, _) in _targets)
        {
            var mesh = doc.Find(id)?.Mesh; if (mesh == null) continue;
            MeshOps.SetCrease(mesh, edges, v);
            doc.Notify(new DocChange(ChangeKind.MeshAttributes, id));
        }
        Ctx.SetHelp?.Invoke($"Crease: {v:0.00}  (drag left/right, Shift = fine, Esc = cancel)");
    }

    private void EndDrag(bool commit)
    {
        _dragging = false;
        var doc = Ctx.Doc;
        // 프리뷰로 쓴 값을 되돌린 뒤 명령으로 적용한다(Undo 스냅샷이 올바른 before를 갖도록)
        foreach (var (id, edges, before) in _targets)
        {
            var mesh = doc.Find(id)?.Mesh; if (mesh == null) continue;
            for (int i = 0; i < edges.Length; i++) { var ed = mesh.Edges[edges[i]]; ed.Crease = before[i]; mesh.Edges[edges[i]] = ed; }
            doc.Notify(new DocChange(ChangeKind.MeshAttributes, id));
        }
        if (commit)
        {
            float v = _value;
            using (doc.Undo.BeginGroup("Crease"))
                foreach (var (id, edges, _) in _targets)
                {
                    var e = edges;
                    doc.Undo.Push(new MeshOpCommand("Crease", id, new HistoryParams(HistoryParam.F("Crease", v, 0f, 10f, 0.5f)),
                        (m, p) => { MeshOps.SetCrease(m, e, p.Float("Crease")); return (true, null, null); }));
                }
            Ctx.SetHelp?.Invoke($"Crease set to {v:0.00}. " + HelpText);
        }
        else Ctx.SetHelp?.Invoke(HelpText);
        _targets.Clear();
    }
}
