using Cube.Core.Commands;
using Cube.Core.Mesh;
using Cube.Core.Scene;
using Cube.Core.Selection;
using Godot;
using NVec2 = System.Numerics.Vector2;
using NVec3 = System.Numerics.Vector3;

namespace Cube.App.Tools;

/// <summary>
/// Maya Multi-Cut Tool(간이판):
/// - 클릭: 엣지 위(그 지점에서 나눔) 또는 정점을 찍는다. 같은 면 안의 앞 점과 이어져 면이 나뉜다. Enter/Esc = 체인 끝.
/// - Ctrl+클릭(엣지): 그 지점을 지나는 엣지 루프 삽입(Insert Edge Loop).
/// - Shift+드래그: 화면에 그은 선으로 메시 전체를 슬라이스(평면 컷).
/// </summary>
/// <remarks>
/// 체인 컷은 체인 전체가 'Multi-Cut' MeshOpCommand 하나(Undo 한 단계)다: 클릭마다 직전 체인 명령을 되돌리고 모든 점을 다시 적용한다.
/// 엣지를 찍으면 SplitEdge로 새 정점을 만들고, 직전 점(<see cref="_lastVertex"/>)과 같은 면을 공유하면 SplitFaceBetween으로 면을 나눈다.
/// 슬라이스는 화면 선의 두 끝을 역투영한 두 레이가 이루는 평면으로 대상 메시들을 SliceWithPlane한다(한 Undo 그룹).
/// 선택 모드는 엣지로 유지하며, 결과 새 엣지를 선택한다.
/// </remarks>
public sealed class MultiCutTool : SelectTool
{
    /// <summary>툴 ID("multiCut").</summary>
    public override string Id => "multiCut";
    /// <summary>표시 이름.</summary>
    public override string Label => "Multi-Cut";
    /// <summary>헬프 라인 안내.</summary>
    public override string HelpText => "Multi-Cut: click edges/vertices to cut across faces (Enter ends the cut), Ctrl+click an edge to insert an edge loop, Shift+drag to slice. Q returns to Select.";

    /// <summary>현재 체인 컷 중인 노드(다른 노드를 찍으면 체인이 새로 시작).</summary>
    private NodeId _chainNode = NodeId.None;
    /// <summary>체인의 마지막 정점 ID(-1 = 체인 없음). 명령 람다 안에서 갱신되어 다음 클릭의 연결 시작점이 된다.</summary>
    private int _lastVertex = -1;
    /// <summary>현재 체인의 점들(맞은 정점 또는 엣지+비율). 클릭마다 체인 전체를 다시 적용하는 명령 하나로 바꾼다.</summary>
    private readonly List<(int vertex, int edge, float t)> _steps = new();
    /// <summary>현재 체인을 적용한 마지막 명령(Undo 스택의 마지막이면 다음 클릭이 이것을 되돌리고 교체한다).</summary>
    private MeshOpCommand? _chainCmd;
    /// <summary>체인에서 찍은 점들의 월드 위치(오버레이 폴리라인 표시용).</summary>
    private readonly List<NVec3> _chainWorld = new();
    /// <summary>_slicing = Shift 드래그 슬라이스 중, _sliceStart/_sliceEnd = 화면 선의 시작·끝(뷰포트 로컬 픽셀).</summary>
    private bool _slicing; private NVec2 _sliceStart, _sliceEnd;

    /// <summary>활성화: 엣지 모드로 전환하고 체인 초기화.</summary>
    public override void Activate(ToolContext ctx)
    {
        base.Activate(ctx);
        if (ctx.Sel.Mode != SelectMode.Edge) ctx.Sel.Mode = SelectMode.Edge;
        ResetChain();
    }

    /// <summary>비활성화: 체인과 오버레이 정리.</summary>
    public override void Deactivate() { base.Deactivate(); ResetChain(); ClearOverlay(); }
    /// <summary>취소: 체인과 오버레이 정리 후 기반 취소.</summary>
    public override void Cancel() { ResetChain(); ClearOverlay(); base.Cancel(); }

    /// <summary>체인 상태와 슬라이스 상태를 비우고 오버레이를 갱신(빈 선)한다.</summary>
    private void ResetChain() { _chainNode = NodeId.None; _lastVertex = -1; _chainWorld.Clear(); _steps.Clear(); _chainCmd = null; _slicing = false; UpdateOverlay(); }

    /// <summary>Enter = 체인 끝, Esc = 체인 초기화, 슬라이스 중 이동 = 선 갱신, 슬라이스 중 뗌 = 슬라이스 실행. 그 외는 SelectTool.</summary>
    public override bool HandleInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventKey { Pressed: true, Echo: false } k when k.Keycode is Key.Enter or Key.KpEnter:
                ResetChain(); Ctx.SetHelp?.Invoke("Multi-Cut: cut finished. Click to start another cut.");
                return true;
            case InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape }:
                ResetChain(); return true;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } when _slicing:
                FinishSlice(); return true;
            case InputEventMouseMotion mm when _slicing:
                _sliceEnd = new NVec2(mm.Position.X, mm.Position.Y); UpdateOverlay(); return true;
        }
        return base.HandleInput(e);
    }

    /// <summary>
    /// 왼쪽 누름: Shift = 슬라이스 시작, Ctrl+엣지 = 엣지 루프 삽입, 그 외 = 정점(우선) 또는 엣지 위 점으로 체인에 점 추가.
    /// 아무것도 맞지 않으면 체인을 끝내고 일반 선택으로 넘긴다(false).
    /// </summary>
    protected override bool OnPrimaryPress(InputEventMouseButton mb)
    {
        var px = new NVec2(mb.Position.X, mb.Position.Y);
        if (mb.ShiftPressed) { _slicing = true; _sliceStart = _sliceEnd = px; return true; }
        // 정점 우선, 그다음 엣지
        var vh = Picker.Pick(mb.Position, SelectMode.Vertex, Ctx.CameraBasedSelection);
        var eh = Picker.Pick(mb.Position, SelectMode.Edge, Ctx.CameraBasedSelection);
        if (vh == null && eh == null) { ResetChain(); return false; }
        if (mb.CtrlPressed && eh != null) { InsertLoopAt(eh.Value, px); return true; }
        // 대상 노드 결정. 다른 노드로 넘어가면 체인을 새로 시작
        NodeId nodeId = vh?.Node ?? eh!.Value.Node;
        var node = Ctx.Doc.Find(nodeId); var mesh = node?.Mesh;
        if (node == null || mesh == null) return false;
        if (_chainNode != NodeId.None && _chainNode != nodeId) ResetChain();
        // 이번 점: 엣지와 비율(정점이 맞았으면 정점 그대로)
        int edge = eh?.Component ?? -1; float t = 0.5f;
        if (vh == null && edge >= 0) t = ParamOnEdge(node, mesh, edge, px);
        int vertexHit = vh?.Component ?? -1;
        // 체인 전체를 Undo 한 단계로(Maya와 같음, v0.0.57; 전에는 클릭마다 한 단계라 Undo하면 엣지 위 정점만 남았다):
        // 같은 체인의 직전 명령이 아직 마지막이면 되돌리고, 지금까지의 모든 점을 처음부터 다시 적용하는 명령 하나로 바꾼다.
        // 슬롯 ID는 재사용되지 않으므로 같은 순서로 다시 적용하면 앞 점들이 만든 정점·엣지 ID가 그대로 재현된다.
        bool continuing = _chainCmd != null && _chainNode == nodeId && ReferenceEquals(Ctx.Undo.LastCommand, _chainCmd);
        if (continuing) Ctx.Undo.Undo(); else { _steps.Clear(); _chainWorld.Clear(); }
        _steps.Add((vertexHit, edge, t));
        var steps = _steps.ToArray();
        var cmd = new MeshOpCommand("Multi-Cut", nodeId, m =>
        {
            int prev = -1; bool any = false;
            var sel = new List<int>();
            foreach (var (vHit, e, tt) in steps)
            {
                // 정점이 아니면 엣지를 t 위치에서 나눠 새 정점을 만든다
                int v = vHit >= 0 && vHit < m.VertexCount && m.Verts[vHit].Alive ? vHit : -1;
                if (v < 0 && e >= 0 && e < m.EdgeCount && m.Edges[e].Alive) v = MeshOps.SplitEdge(m, e, tt);
                if (v < 0) continue;
                any = true;
                // 직전 점과 같은 면에 있으면 두 정점 사이로 면을 나누고 새 엣지를 선택
                if (prev >= 0 && prev != v && m.Verts[prev].Alive)
                {
                    int ne = MeshOps.SplitFaceBetween(m, prev, v);
                    if (ne >= 0) sel.Add(ne);
                }
                prev = v;
            }
            _lastVertex = prev;
            return (any, sel.Count > 0 ? SelectMode.Edge : null, sel.Count > 0 ? sel : null);
        });
        Ctx.Undo.Push(cmd);
        if (!cmd.DidChange)
        {
            // 이번 점이 아무것도 못 했으면 이전 체인 상태로 되돌린다
            _steps.RemoveAt(_steps.Count - 1);
            if (continuing) Ctx.Undo.Redo();
            return true;
        }
        _chainCmd = cmd;
        // 성공 시 체인 상태 갱신(오버레이에 새 점 추가)
        _chainNode = nodeId;
        _chainWorld.Add(NVec3.Transform(mesh.Verts[_lastVertex].Position, node.WorldMatrix));
        UpdateOverlay();
        Ctx.SetHelp?.Invoke($"Multi-Cut: {_chainWorld.Count} point(s). Click the next edge/vertex, Enter to finish.");
        SetHover(null);
        return true;
    }

    /// <summary>
    /// 클릭 화면 점을 엣지의 화면 투영 선분에 사영한 비율 t(0.02~0.98). 투영 실패 시 0.5, J 홀드면 중앙(0.5).
    /// </summary>
    private float ParamOnEdge(SceneNode node, PolyMesh mesh, int edge, NVec2 px)
    {
        var (a, b) = mesh.EdgeVertices(edge);
        var proj = Picker.Projection();
        var pa = proj.Project(NVec3.Transform(mesh.Verts[a].Position, node.WorldMatrix), out _);
        var pb = proj.Project(NVec3.Transform(mesh.Verts[b].Position, node.WorldMatrix), out _);
        if (pa == null || pb == null) return 0.5f;
        var d = pb.Value - pa.Value; float len2 = d.LengthSquared();
        float t = len2 > 1e-6f ? Math.Clamp(NVec2.Dot(px - pa.Value, d) / len2, 0.02f, 0.98f) : 0.5f;
        return Ctx.Viewport.IsSnapHeld ? 0.5f : t;
    }

    /// <summary>Ctrl+클릭: 엣지 위 비율 t를 지나는 엣지 루프 삽입('Insert Edge Loop' 히스토리, Position 파라미터). 체인은 끝낸다.</summary>
    private void InsertLoopAt(Core.Picking.PickHit hit, NVec2 px)
    {
        var node = Ctx.Doc.Find(hit.Node); var mesh = node?.Mesh; if (node == null || mesh == null) return;
        int edge = hit.Component; float t = ParamOnEdge(node, mesh, edge, px);
        var cmd = new MeshOpCommand("Insert Edge Loop", node.Id, new HistoryParams(HistoryParam.F("Position", t, 0.01f, 0.99f, 0.01f)), (m, p) =>
        {
            var ne = MeshOps.InsertEdgeLoop(m, edge, p.Float("Position"));
            return (ne.Count > 0, SelectMode.Edge, ne);
        });
        Ctx.Undo.Push(cmd);
        ResetChain();
    }

    /// <summary>
    /// 슬라이스 실행: 화면 선이 너무 짧으면(4px 미만) 무시. 선택 노드(없으면 모든 메시 노드)마다 월드 평면을 메시 로컬로 바꿔 SliceWithPlane한다.
    /// </summary>
    private void FinishSlice()
    {
        _slicing = false;
        var start = _sliceStart; var end = _sliceEnd;
        ClearOverlay();
        if (NVec2.Distance(start, end) < 4f * CubeApp.Instance.UiScale) return;
        var proj = Picker.Projection();
        var r0 = proj.Unproject(start); var r1 = proj.Unproject(end);
        // 두 광선이 만드는 평면(원근: 눈을 지남, 직교: 두 원점과 시선)
        NVec3 normal = proj.IsOrtho ? NVec3.Cross(r0.Direction, r1.Origin - r0.Origin) : NVec3.Cross(r0.Direction, r1.Direction);
        if (normal.LengthSquared() < 1e-12f) return;
        normal = NVec3.Normalize(normal);
        // 대상 노드: 오브젝트 선택 또는 컴포넌트가 있는 노드, 없으면 모든 메시
        var sel = Ctx.Sel; var doc = Ctx.Doc;
        var ids = sel.Mode == SelectMode.Object ? sel.Objects.ToList() : sel.NodesWithComponents(sel.Mode).ToList();
        if (ids.Count == 0) ids = doc.MeshNodes().Select(n => n.Id).ToList();
        int cuts = 0;
        using (doc.Undo.BeginGroup("Multi-Cut Slice"))
            foreach (var id in ids)
            {
                var node = doc.Find(id); if (node?.Mesh == null) continue;
                // 평면 점은 역행렬로, 법선은 월드 행렬의 전치(역전치의 역)로 로컬 변환
                System.Numerics.Matrix4x4.Invert(node.WorldMatrix, out var inv);
                var lp = NVec3.Transform(r0.Origin, inv);
                var ln = NVec3.Normalize(NVec3.TransformNormal(normal, System.Numerics.Matrix4x4.Transpose(node.WorldMatrix)));
                var cmd = new MeshOpCommand("Slice", id, m => { var ne = MeshOps.SliceWithPlane(m, lp, ln); cuts += ne.Count; return (ne.Count > 0, SelectMode.Edge, ne); });
                doc.Undo.Push(cmd);
            }
        Ctx.SetHelp?.Invoke(cuts > 0 ? $"Multi-Cut: sliced {cuts} edge(s)." : "Multi-Cut: the slice line did not cross any faces.");
        ResetChain();
    }

    /// <summary>슬라이스 중이면 화면 선을, 아니면 체인 점들의 화면 투영을 활성 패널 오버레이 폴리라인으로 그린다.</summary>
    private void UpdateOverlay()
    {
        if (Ctx == null) return;
        var overlay = Ctx.Viewport.Overlay;
        if (_slicing)
        {
            // 슬라이스 선은 화면 공간 그대로
            overlay.Polyline = new List<Godot.Vector2> { new(_sliceStart.X, _sliceStart.Y), new(_sliceEnd.X, _sliceEnd.Y) };
            return;
        }
        if (_chainWorld.Count == 0) { overlay.Polyline = null; return; }
        // 체인 점은 월드 좌표 → 매 프레임 그 패널의 카메라로 다시 투영(뷰를 돌려도 자른 위치를 따라감)
        var picker = Picker;
        overlay.PolylineSource = () =>
        {
            var proj = picker.Projection();
            var pts = new List<Godot.Vector2>(_chainWorld.Count);
            foreach (var p in _chainWorld) { var sp = proj.Project(p, out _); if (sp != null) pts.Add(new Godot.Vector2(sp.Value.X, sp.Value.Y)); }
            return pts.Count > 0 ? pts : null;
        };
    }

    /// <summary>모든 패널의 오버레이 폴리라인을 지운다.</summary>
    private void ClearOverlay() { foreach (var p in UI.Shell.Instance.Layout.Panels) p.Overlay.Polyline = null; }
}
