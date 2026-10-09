using Cube.Core.Commands;
using Cube.Core.Mesh;
using Cube.Core.Scene;
using Cube.Core.Selection;
using Godot;
using NVec2 = System.Numerics.Vector2;
using NVec3 = System.Numerics.Vector3;

namespace Cube.App.Tools;

/// <summary>
/// Maya Append to Polygon Tool: 경계 엣지를 먼저 클릭하고, 이어서 점(또는 기존 정점)을 찍은 뒤 Enter로 새 면을 붙인다.
/// 점은 그 경계 엣지가 속한 면의 평면 위에 놓인다. Esc = 취소.
/// </summary>
/// <remarks>
/// 상태: <see cref="_edge"/> &lt; 0이면 경계 엣지 고르기 단계, 아니면 점 찍기 단계. 찍은 점은 월드 좌표로 모으고(기존 정점이면 그 ID도),
/// Enter 때 메시 로컬로 바꿔 <see cref="MeshOps.AppendPolygon"/>을 MeshOpCommand로 실행한다(새 면을 면 모드로 선택).
/// 오버레이 폴리라인은 경계 엣지(b → a 순서) + 찍은 점들을 화면에 이어 그린다.
/// </remarks>
public sealed class AppendPolygonTool : SelectTool
{
    /// <summary>툴 ID("appendPolygon").</summary>
    public override string Id => "appendPolygon";
    /// <summary>표시 이름(명령 이름).</summary>
    public override string Label => "Append to Polygon";
    /// <summary>헬프 라인 안내.</summary>
    public override string HelpText => "Append to Polygon: click a border edge, then click points (or existing vertices), Enter to create the face. Esc cancels.";

    /// <summary>_node = 고른 경계 엣지의 노드, _edge = 그 엣지 ID(-1 = 아직 안 고름).</summary>
    private NodeId _node = NodeId.None; private int _edge = -1;
    /// <summary>점을 놓을 평면(월드): 경계 엣지 중점과, 엣지가 속한 면의 월드 법선.</summary>
    private NVec3 _planePoint, _planeNormal;
    /// <summary>_points = 찍은 점(월드), _existing = 같은 순서의 기존 정점 ID(새 점이면 -1).</summary>
    private readonly List<NVec3> _points = new(); private readonly List<int> _existing = new();

    /// <summary>활성화: 엣지 모드로 바꾸고 상태 초기화.</summary>
    public override void Activate(ToolContext ctx) { base.Activate(ctx); if (ctx.Sel.Mode != SelectMode.Edge) ctx.Sel.Mode = SelectMode.Edge; Reset(); }
    /// <summary>비활성화: 상태와 오버레이 정리.</summary>
    public override void Deactivate() { base.Deactivate(); Reset(); }
    /// <summary>취소: 상태 초기화 후 기반 취소(마키 정리).</summary>
    public override void Cancel() { Reset(); base.Cancel(); }
    /// <summary>상태를 비우고 모든 패널의 오버레이 폴리라인을 지운다.</summary>
    private void Reset() { _node = NodeId.None; _edge = -1; _points.Clear(); _existing.Clear(); foreach (var p in UI.Shell.Instance.Layout.Panels) p.Overlay.Polyline = null; }

    /// <summary>Enter = 면 생성, Esc = 처음부터, 마우스 이동 = 오버레이 갱신. 나머지(클릭·호버)는 SelectTool 경유.</summary>
    public override bool HandleInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventKey { Pressed: true, Echo: false } k when k.Keycode is Key.Enter or Key.KpEnter: Finish(); return true;
            case InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape }: Reset(); Ctx.SetHelp?.Invoke(HelpText); return true;
            case InputEventMouseMotion: UpdateOverlay(); break;
        }
        return base.HandleInput(e);
    }

    /// <summary>
    /// 왼쪽 클릭: 1단계면 커서 아래 경계 엣지를 골라 평면을 정하고 그 엣지를 선택한다(경계가 아니면 안내만).
    /// 2단계면 같은 노드의 기존 정점이면 그 정점을, 아니면 레이와 평면의 교점을 점으로 추가한다.
    /// </summary>
    protected override bool OnPrimaryPress(InputEventMouseButton mb)
    {
        if (_edge < 0)
        {
            // 1단계: 엣지 피킹 → 경계(면 하나) 엣지만 허용
            var hit = Picker.Pick(mb.Position, SelectMode.Edge, Ctx.CameraBasedSelection);
            if (hit == null) return false;
            var node = Ctx.Doc.Find(hit.Value.Node); var mesh = node?.Mesh; if (node == null || mesh == null) return false;
            int edge = hit.Value.Component;
            if (!mesh.IsBoundaryEdge(edge)) { Ctx.SetHelp?.Invoke("Append to Polygon: click a border edge (an edge with only one face)."); return true; }
            _node = node.Id; _edge = edge;
            // 평면 = 엣지 중점(월드) + 유일한 이웃 면의 법선(월드)
            var (a, b) = mesh.EdgeVertices(edge);
            _planePoint = NVec3.Transform((mesh.Verts[a].Position + mesh.Verts[b].Position) * 0.5f, node.WorldMatrix);
            int f = mesh.EdgeFaces(edge).f0;
            _planeNormal = NVec3.Normalize(NVec3.TransformNormal(mesh.Faces[f].Normal, node.WorldMatrix));
            Ctx.Sel.SelectComponents(_node, SelectMode.Edge, new[] { edge }, replace: true);
            Ctx.SetHelp?.Invoke("Append to Polygon: click points for the new face (existing vertices snap), Enter to finish.");
            return true;
        }
        // 기존 정점 클릭이면 그 정점, 아니면 평면 위 점
        var vh = Picker.Pick(mb.Position, SelectMode.Vertex, Ctx.CameraBasedSelection);
        if (vh != null && vh.Value.Node == _node) { _points.Add(vh.Value.WorldPos); _existing.Add(vh.Value.Component); }
        else
        {
            // 평면이 시선과 거의 평행하면 화면 평면으로 대체해 교점을 구한다
            var proj = Picker.Projection();
            var ray = proj.Unproject(new NVec2(mb.Position.X, mb.Position.Y));
            var n = MathF.Abs(NVec3.Dot(ray.Direction, _planeNormal)) < 1e-3f ? -proj.Forward : _planeNormal;
            if (!Core.Geometry.DragMath.RayPlane(ray, _planePoint, n, out var hit)) return true;
            _points.Add(hit); _existing.Add(-1);
        }
        UpdateOverlay();
        return true;
    }

    /// <summary>
    /// Enter: 점들을 메시 로컬로 변환해 AppendPolygon 명령을 푸시한다. 실패(자기 겹침·비매니폴드)하면 안내만 하고, 어느 쪽이든 상태를 초기화한다.
    /// </summary>
    private void Finish()
    {
        if (_edge < 0 || _points.Count == 0) { Ctx.SetHelp?.Invoke("Append to Polygon: need a border edge and at least one point."); return; }
        var node = Ctx.Doc.Find(_node); if (node?.Mesh == null) { Reset(); return; }
        System.Numerics.Matrix4x4.Invert(node.WorldMatrix, out var inv);
        var local = _points.Select(p => NVec3.Transform(p, inv)).ToList();
        var existing = _existing.ToList(); int edge = _edge;
        var cmd = new MeshOpCommand("Append to Polygon", _node, m => { int nf = MeshOps.AppendPolygon(m, edge, local, existing); return (nf >= 0, SelectMode.Face, nf >= 0 ? new[] { nf } : null); });
        Ctx.Undo.Push(cmd);
        Ctx.SetHelp?.Invoke(cmd.DidChange ? "Append to Polygon: face added. Click another border edge to continue." : "Append to Polygon: could not add the face (self-overlap or non-manifold).");
        Reset();
    }

    /// <summary>현재 경계 엣지와 찍은 점들을 화면에 투영해 활성 패널 오버레이 폴리라인으로 그린다.</summary>
    private void UpdateOverlay()
    {
        var overlay = Ctx.Viewport.Overlay;
        if (_node == NodeId.None && _points.Count == 0) { overlay.Polyline = null; return; }
        // 매 프레임 그 패널의 카메라로 다시 투영(뷰를 돌려도 경계 엣지·찍은 점을 따라감)
        var picker = Picker;
        overlay.PolylineSource = () => BuildOverlay(picker.Projection());
    }

    /// <summary>경계 엣지 두 끝점과 찍은 점들의 화면 투영 목록(없으면 null).</summary>
    private List<Godot.Vector2>? BuildOverlay(Core.Picking.CameraProjection proj)
    {
        var pts = new List<Godot.Vector2>();
        var node = Ctx.Doc.Find(_node);
        if (node?.Mesh != null && _edge >= 0 && _edge < node.Mesh.EdgeCount)
        {
            var (a, b) = node.Mesh.EdgeVertices(_edge);
            foreach (int v in new[] { b, a }) { var sp = proj.Project(NVec3.Transform(node.Mesh.Verts[v].Position, node.WorldMatrix), out _); if (sp != null) pts.Add(new Godot.Vector2(sp.Value.X, sp.Value.Y)); }
        }
        foreach (var p in _points) { var sp = proj.Project(p, out _); if (sp != null) pts.Add(new Godot.Vector2(sp.Value.X, sp.Value.Y)); }
        return pts.Count > 0 ? pts : null;
    }
}
