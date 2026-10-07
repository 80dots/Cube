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
public sealed class AppendPolygonTool : SelectTool
{
    public override string Id => "appendPolygon";
    public override string Label => "Append to Polygon";
    public override string HelpText => "Append to Polygon: click a border edge, then click points (or existing vertices), Enter to create the face. Esc cancels.";

    private NodeId _node = NodeId.None; private int _edge = -1;
    private NVec3 _planePoint, _planeNormal;
    private readonly List<NVec3> _points = new(); private readonly List<int> _existing = new();

    public override void Activate(ToolContext ctx) { base.Activate(ctx); if (ctx.Sel.Mode != SelectMode.Edge) ctx.Sel.Mode = SelectMode.Edge; Reset(); }
    public override void Deactivate() { base.Deactivate(); Reset(); }
    public override void Cancel() { Reset(); base.Cancel(); }
    private void Reset() { _node = NodeId.None; _edge = -1; _points.Clear(); _existing.Clear(); foreach (var p in UI.Shell.Instance.Layout.Panels) p.Overlay.Polyline = null; }

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

    protected override bool OnPrimaryPress(InputEventMouseButton mb)
    {
        if (_edge < 0)
        {
            var hit = Picker.Pick(mb.Position, SelectMode.Edge, Ctx.CameraBasedSelection);
            if (hit == null) return false;
            var node = Ctx.Doc.Find(hit.Value.Node); var mesh = node?.Mesh; if (node == null || mesh == null) return false;
            int edge = hit.Value.Component;
            if (!mesh.IsBoundaryEdge(edge)) { Ctx.SetHelp?.Invoke("Append to Polygon: click a border edge (an edge with only one face)."); return true; }
            _node = node.Id; _edge = edge;
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
            var proj = Picker.Projection();
            var ray = proj.Unproject(new NVec2(mb.Position.X, mb.Position.Y));
            var n = MathF.Abs(NVec3.Dot(ray.Direction, _planeNormal)) < 1e-3f ? -proj.Forward : _planeNormal;
            if (!Core.Geometry.DragMath.RayPlane(ray, _planePoint, n, out var hit)) return true;
            _points.Add(hit); _existing.Add(-1);
        }
        UpdateOverlay();
        return true;
    }

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

    private void UpdateOverlay()
    {
        var overlay = Ctx.Viewport.Overlay;
        var proj = Picker.Projection();
        var pts = new List<Godot.Vector2>();
        var node = Ctx.Doc.Find(_node);
        if (node?.Mesh != null && _edge >= 0 && _edge < node.Mesh.EdgeCount)
        {
            var (a, b) = node.Mesh.EdgeVertices(_edge);
            foreach (int v in new[] { b, a }) { var sp = proj.Project(NVec3.Transform(node.Mesh.Verts[v].Position, node.WorldMatrix), out _); if (sp != null) pts.Add(new Godot.Vector2(sp.Value.X, sp.Value.Y)); }
        }
        foreach (var p in _points) { var sp = proj.Project(p, out _); if (sp != null) pts.Add(new Godot.Vector2(sp.Value.X, sp.Value.Y)); }
        overlay.Polyline = pts.Count > 0 ? pts : null;
    }
}
