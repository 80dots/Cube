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
/// <remarks>
/// 값 범위는 0~10(Maya 크리즈 단계), 드래그 1px당 0.02(Shift 0.002). 시작 값은 대상 엣지 중 최대 크리즈.
/// Alt+MMB는 카메라 트랙이므로 Alt 없는 MMB만 받는다(드래그 중에는 Alt와 무관하게 뗌 처리).
/// </remarks>
public sealed class CreaseTool : SelectTool
{
    /// <summary>툴 ID("creaseTool").</summary>
    public override string Id => "creaseTool";
    /// <summary>표시 이름.</summary>
    public override string Label => "Crease Tool";
    /// <summary>헬프 라인 안내.</summary>
    public override string HelpText => "Crease Tool: select edges, then drag with the middle mouse button left/right to set the crease (Shift = fine). Double-click selects a loop.";

    /// <summary>마우스 x 이동 1px당 크리즈 값 변화량.</summary>
    private const float Step = 0.02f;   // px당 크리즈 변화
    /// <summary>MMB 드래그 중인지.</summary>
    private bool _dragging;
    /// <summary>_pressX = 누른 x 좌표, _start = 시작 크리즈 값, _value = 현재 프리뷰 값.</summary>
    private float _pressX, _start, _value;
    /// <summary>노드별 대상 엣지와 드래그 전 크리즈 값(취소·Undo before 복원용).</summary>
    private readonly List<(NodeId id, int[] edges, float[] before)> _targets = new();

    /// <summary>활성화 시 엣지/정점 모드가 아니면 엣지 모드로 바꾼다.</summary>
    public override void Activate(ToolContext ctx)
    {
        base.Activate(ctx);
        if (ctx.Sel.Mode is not (SelectMode.Edge or SelectMode.Vertex)) ctx.Sel.Mode = SelectMode.Edge;
    }

    /// <summary>비활성화 시 진행 중 드래그는 취소.</summary>
    public override void Deactivate()
    {
        if (_dragging) EndDrag(commit: false);
        base.Deactivate();
    }

    /// <summary>Esc/툴 전환 등 취소 시 진행 중 드래그를 되돌린다.</summary>
    public override void Cancel()
    {
        if (_dragging) EndDrag(commit: false);
        base.Cancel();
    }

    /// <summary>
    /// MMB 누름 = 드래그 시작(대상 없으면 처리 안 함), MMB 뗌 = 커밋, 드래그 중 이동 = 값 갱신, Esc = 취소. 나머지는 SelectTool.
    /// </summary>
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
                    // 시작 값 + x 이동량 × 단위(Shift면 1/10)를 0~10으로 제한
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

    /// <summary>
    /// 드래그 시작: 선택(엣지 또는 정점에 닿는 엣지)에서 살아 있는 엣지를 모으고 이전 크리즈를 저장한다.
    /// </summary>
    /// <param name="x">누른 화면 x 좌표.</param>
    /// <returns>대상 엣지가 있으면 true.</returns>
    private bool BeginDrag(float x)
    {
        _targets.Clear();
        var sel = Ctx.Sel; var doc = Ctx.Doc;
        float start = 0f;
        foreach (var id in sel.NodesWithComponents(sel.Mode))
        {
            var mesh = doc.Find(id)?.Mesh; if (mesh == null) continue;
            var comps = sel.GetComponents(id);
            // 엣지 모드는 선택 엣지, 정점 모드는 정점에 닿는 엣지. 유효·생존 엣지만, 중복 제거
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

    /// <summary>프리뷰: 모든 대상 엣지에 크리즈 값을 직접 쓰고(MeshAttributes 통지) 헬프 라인에 표시한다.</summary>
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

    /// <summary>
    /// 드래그 종료: 프리뷰 값을 원래대로 되돌린 뒤, commit이면 노드별 'Crease' 히스토리 항목(MeshOpCommand)을 한 Undo 그룹으로 푸시한다.
    /// </summary>
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
            // 커밋: 이력 재생 시 파라미터 Crease로 같은 엣지에 다시 설정
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
