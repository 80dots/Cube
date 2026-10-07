using System.Numerics;
using Cube.Core.Commands;
using Cube.Core.Mesh;
using Cube.Core.Scene;
using Godot;
using NVec2 = System.Numerics.Vector2;
using NVec3 = System.Numerics.Vector3;

namespace Cube.App.Tools;

/// <summary>
/// Maya Create Polygon Tool: 뷰포트에 점을 찍어 n각형 하나를 만든다. 원근 뷰는 지면 평면(y=0), 직교 뷰는 원점을 지나는 화면 평면.
/// Enter = 완성(3점 이상), Esc/Q = 취소. 완성된 면은 카메라를 향하는 쪽(직교)/위(원근)가 앞면이다.
/// </summary>
public sealed class CreatePolygonTool : ToolBase
{
    public override string Id => "createPolygon";
    public override string Label => "Create Polygon Tool";
    public override string HelpText => "Create Polygon Tool: click to place vertices, Enter to finish (3+ points), Esc to cancel.";

    private readonly List<NVec3> _points = new();
    private NVec3 _normal = NVec3.UnitY;

    public override void Activate(ToolContext ctx) { base.Activate(ctx); _points.Clear(); UpdateOverlay(); }
    public override void Deactivate() { base.Deactivate(); _points.Clear(); ClearOverlay(); }
    public override void Cancel() { _points.Clear(); ClearOverlay(); }

    public override bool HandleInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } mb:
                AddPoint(new NVec2(mb.Position.X, mb.Position.Y));
                return true;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false }:
                return true;
            case InputEventMouseMotion:
                UpdateOverlay();
                return false;
            case InputEventKey { Pressed: true, Echo: false } k when k.Keycode is Key.Enter or Key.KpEnter:
                Finish();
                return true;
            case InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape }:
                Cancel();
                Ctx.SetHelp?.Invoke(HelpText);
                return true;
        }
        return false;
    }

    private bool Plane(out NVec3 point, out NVec3 normal)
    {
        var proj = Ctx.Viewport.Picker.Projection();
        if (proj.IsOrtho) { normal = -proj.Forward; point = NVec3.Zero; }
        else { normal = NVec3.UnitY; point = NVec3.Zero; }
        return true;
    }

    private void AddPoint(NVec2 px)
    {
        var proj = Ctx.Viewport.Picker.Projection();
        Plane(out var pp, out var n);
        var ray = proj.Unproject(px);
        if (MathF.Abs(NVec3.Dot(ray.Direction, n)) < 1e-3f) n = -proj.Forward;
        if (!Core.Geometry.DragMath.RayPlane(ray, pp, n, out var hit)) return;
        _points.Add(hit);
        _normal = proj.IsOrtho ? -proj.Forward : NVec3.UnitY;
        UpdateOverlay();
        Ctx.SetHelp?.Invoke($"Create Polygon Tool: {_points.Count} point(s). Enter to finish, Esc to cancel.");
    }

    private void Finish()
    {
        if (_points.Count < 3) { Ctx.SetHelp?.Invoke("Create Polygon Tool: need at least 3 points."); return; }
        var mesh = MeshBuilder.Polygon(_points.ToList(), _normal);
        if (mesh.AliveFaceCount == 0) { Ctx.SetHelp?.Invoke("Create Polygon Tool: points are degenerate."); _points.Clear(); ClearOverlay(); return; }
        var node = new SceneNode { Name = Ctx.Doc.UniqueName("polySurface1"), Shape = new MeshShape(mesh) };
        Ctx.Undo.Push(new AddNodeCommand("Create Polygon", node));
        _points.Clear();
        ClearOverlay();
        Ctx.SetHelp?.Invoke($"Create Polygon Tool: {node.Name} created. Click to start another, Q to return to Select.");
    }

    private void UpdateOverlay()
    {
        var overlay = Ctx.Viewport.Overlay;
        var proj = Ctx.Viewport.Picker.Projection();
        var pts = new List<Godot.Vector2>();
        foreach (var p in _points) { var sp = proj.Project(p, out _); if (sp != null) pts.Add(new Godot.Vector2(sp.Value.X, sp.Value.Y)); }
        overlay.Polyline = pts.Count > 0 ? pts : null;
    }

    private void ClearOverlay() { foreach (var p in UI.Shell.Instance.Layout.Panels) p.Overlay.Polyline = null; }
}
