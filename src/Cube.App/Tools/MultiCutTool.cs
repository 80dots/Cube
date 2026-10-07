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
public sealed class MultiCutTool : SelectTool
{
    public override string Id => "multiCut";
    public override string Label => "Multi-Cut";
    public override string HelpText => "Multi-Cut: click edges/vertices to cut across faces (Enter ends the cut), Ctrl+click an edge to insert an edge loop, Shift+drag to slice. Q returns to Select.";

    private NodeId _chainNode = NodeId.None;
    private int _lastVertex = -1;
    private readonly List<NVec3> _chainWorld = new();
    private bool _slicing; private NVec2 _sliceStart, _sliceEnd;

    public override void Activate(ToolContext ctx)
    {
        base.Activate(ctx);
        if (ctx.Sel.Mode != SelectMode.Edge) ctx.Sel.Mode = SelectMode.Edge;
        ResetChain();
    }

    public override void Deactivate() { base.Deactivate(); ResetChain(); ClearOverlay(); }
    public override void Cancel() { ResetChain(); ClearOverlay(); base.Cancel(); }

    private void ResetChain() { _chainNode = NodeId.None; _lastVertex = -1; _chainWorld.Clear(); _slicing = false; UpdateOverlay(); }

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

    protected override bool OnPrimaryPress(InputEventMouseButton mb)
    {
        var px = new NVec2(mb.Position.X, mb.Position.Y);
        if (mb.ShiftPressed) { _slicing = true; _sliceStart = _sliceEnd = px; return true; }
        // 정점 우선, 그다음 엣지
        var vh = Picker.Pick(mb.Position, SelectMode.Vertex, Ctx.CameraBasedSelection);
        var eh = Picker.Pick(mb.Position, SelectMode.Edge, Ctx.CameraBasedSelection);
        if (vh == null && eh == null) { ResetChain(); return false; }
        if (mb.CtrlPressed && eh != null) { InsertLoopAt(eh.Value, px); return true; }
        NodeId nodeId = vh?.Node ?? eh!.Value.Node;
        var node = Ctx.Doc.Find(nodeId); var mesh = node?.Mesh;
        if (node == null || mesh == null) return false;
        if (_chainNode != NodeId.None && _chainNode != nodeId) ResetChain();
        int prev = _lastVertex;
        int edge = eh?.Component ?? -1; float t = 0.5f;
        if (vh == null && edge >= 0) t = ParamOnEdge(node, mesh, edge, px);
        int vertexHit = vh?.Component ?? -1;
        var cmd = new MeshOpCommand("Multi-Cut", nodeId, m =>
        {
            int v = vertexHit;
            if (v < 0 && edge >= 0 && edge < m.EdgeCount && m.Edges[edge].Alive) v = MeshOps.SplitEdge(m, edge, t);
            if (v < 0) return (false, null, null);
            _lastVertex = v;
            var sel = new List<int>();
            if (prev >= 0 && prev < m.VertexCount && m.Verts[prev].Alive && prev != v)
            {
                int ne = MeshOps.SplitFaceBetween(m, prev, v);
                if (ne >= 0) sel.Add(ne);
            }
            return (true, sel.Count > 0 ? SelectMode.Edge : null, sel.Count > 0 ? sel : null);
        });
        Ctx.Undo.Push(cmd);
        if (!cmd.DidChange) { return true; }
        _chainNode = nodeId;
        _chainWorld.Add(NVec3.Transform(mesh.Verts[_lastVertex].Position, node.WorldMatrix));
        UpdateOverlay();
        Ctx.SetHelp?.Invoke($"Multi-Cut: {_chainWorld.Count} point(s). Click the next edge/vertex, Enter to finish.");
        SetHover(null);
        return true;
    }

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
        var sel = Ctx.Sel; var doc = Ctx.Doc;
        var ids = sel.Mode == SelectMode.Object ? sel.Objects.ToList() : sel.NodesWithComponents(sel.Mode).ToList();
        if (ids.Count == 0) ids = doc.MeshNodes().Select(n => n.Id).ToList();
        int cuts = 0;
        using (doc.Undo.BeginGroup("Multi-Cut Slice"))
            foreach (var id in ids)
            {
                var node = doc.Find(id); if (node?.Mesh == null) continue;
                System.Numerics.Matrix4x4.Invert(node.WorldMatrix, out var inv);
                var lp = NVec3.Transform(r0.Origin, inv);
                var ln = NVec3.Normalize(NVec3.TransformNormal(normal, System.Numerics.Matrix4x4.Transpose(node.WorldMatrix)));
                var cmd = new MeshOpCommand("Slice", id, m => { var ne = MeshOps.SliceWithPlane(m, lp, ln); cuts += ne.Count; return (ne.Count > 0, SelectMode.Edge, ne); });
                doc.Undo.Push(cmd);
            }
        Ctx.SetHelp?.Invoke(cuts > 0 ? $"Multi-Cut: sliced {cuts} edge(s)." : "Multi-Cut: the slice line did not cross any faces.");
        ResetChain();
    }

    private void UpdateOverlay()
    {
        if (Ctx == null) return;
        var overlay = Ctx.Viewport.Overlay;
        var pts = new List<Godot.Vector2>();
        if (_slicing) { pts.Add(new Godot.Vector2(_sliceStart.X, _sliceStart.Y)); pts.Add(new Godot.Vector2(_sliceEnd.X, _sliceEnd.Y)); }
        else
        {
            var proj = Picker.Projection();
            foreach (var p in _chainWorld) { var sp = proj.Project(p, out _); if (sp != null) pts.Add(new Godot.Vector2(sp.Value.X, sp.Value.Y)); }
        }
        overlay.Polyline = pts.Count > 0 ? pts : null;
    }

    private void ClearOverlay() { foreach (var p in UI.Shell.Instance.Layout.Panels) p.Overlay.Polyline = null; }
}
