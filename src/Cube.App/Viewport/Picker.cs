
using Cube.App.Bridge;
using Cube.Core.Picking;
using Cube.Core.Scene;
using Cube.Core.Selection;
using Godot;
using GVec2 = Godot.Vector2;
using NVec2 = System.Numerics.Vector2;

namespace Cube.App.Viewport;

/// <summary>Godot 카메라/뷰에서 CameraProjection과 PickTarget 목록을 만들어 RayPicker를 호출한다.</summary>
public sealed class Picker
{
    private readonly ViewportPanel _panel;
    public Picker(ViewportPanel panel) { _panel = panel; }

    public float Scale => CubeApp.Instance.UiScale;

    public CameraProjection Projection()
    {
        var cam = _panel.Camera;
        var size = _panel.Size;
        return new CameraProjection(
            cam.GlobalTransform.ToNumerics(),
            cam.Projection == Camera3D.ProjectionType.Orthogonal,
            cam.Fov * MathF.PI / 180f,
            cam.Size,
            cam.Near, cam.Far,
            new NVec2(size.X, size.Y));
    }

    public List<PickTarget> Targets()
    {
        var list = new List<PickTarget>();
        foreach (var (id, mv) in _panel.Scene.MeshViews)
        {
            if (!mv.Visible || mv.Node.Mesh == null) continue;
            list.Add(new PickTarget { Id = id, Mesh = mv.Node.Mesh, Render = mv.Render, World = mv.GlobalTransform.ToNumerics() });
        }
        return list;
    }

    public PickHit? Pick(GVec2 px, SelectMode mode, bool cameraBased)
    {
        var p = new NVec2(px.X, px.Y);
        var targets = Targets();
        return mode switch
        {
            SelectMode.Vertex => RayPicker.PickVertex(targets, Projection(), p, cameraBased, RayPicker.VertexThresholdPx * Scale),
            SelectMode.Edge => RayPicker.PickEdge(targets, Projection(), p, cameraBased, RayPicker.EdgeThresholdPx * Scale),
            _ => RayPicker.Pick(targets, Projection(), p, mode, cameraBased),
        };
    }

    public List<SelItem> Marquee(Rect2 rect, SelectMode mode, bool cameraBased)
    {
        var min = new NVec2(rect.Position.X, rect.Position.Y);
        var max = new NVec2(rect.End.X, rect.End.Y);
        return RayPicker.Marquee(Targets(), Projection(), min, max, mode, cameraBased);
    }
}
