
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
            // 컴포넌트 모드: 편집 대상 개체(하나)만. 대상이 아직 없으면 처음 집을 개체를 고를 수 있게 전부.
            var sel = UI.Shell.Instance?.Document.Selection;
            if (sel != null && sel.IsComponentMode && sel.ComponentTarget != Core.Scene.NodeId.None && !sel.IsComponentEditable(id)) continue;
            list.Add(new PickTarget { Id = id, Mesh = mv.Node.Mesh, Render = mv.Render, World = mv.GlobalTransform.ToNumerics() });
        }
        return list;
    }

    /// <summary>오브젝트 모드 조인트 피킹: 조인트 구(10px) 또는 본 선분(6px)에 가까우면 그 조인트.</summary>
    public PickHit? PickJoint(NVec2 p)
    {
        var proj = Projection();
        float best = float.MaxValue; PickHit? hit = null;
        float rJoint = 10f * Scale, rBone = 6f * Scale;
        foreach (var (id, jv) in _panel.Scene.JointViews)
        {
            if (!jv.Visible) continue;
            var w = jv.GlobalPosition.ToNumerics();
            var sp = proj.Project(w, out float depth);
            if (sp == null) continue;
            float d = NVec2.Distance(sp.Value, p);
            if (d <= rJoint && d < best) { best = d; hit = new PickHit(id, -1, depth, w); }
            foreach (var c in jv.Node.Children)
            {
                if (!c.IsJoint || !_panel.Scene.JointViews.TryGetValue(c.Id, out var cv)) continue;
                var cw = cv.GlobalPosition.ToNumerics();
                var cp = proj.Project(cw, out _);
                if (cp == null) continue;
                float t = RayPicker.ClosestParam(sp.Value, cp.Value, p);
                float db = NVec2.Distance(sp.Value + (cp.Value - sp.Value) * t, p) + 2f * Scale; // 본은 구보다 약간 낮은 우선순위
                if (db <= rBone + 2f * Scale && db < best) { best = db; hit = new PickHit(id, -1, depth, w); }
            }
        }
        return hit;
    }

    /// <summary>정점 항목을 그 정점의 모든 UV 점 항목으로 바꾼다(뷰포트 UV 모드 선택).</summary>
    public List<SelItem> ExpandUv(IEnumerable<SelItem> vertexItems)
    {
        var result = new List<SelItem>();
        foreach (var it in vertexItems)
        {
            var mv = _panel.Scene.GetMeshView(it.Node); if (mv == null) continue;
            var topo = mv.UvTopo;
            for (int p = 0; p < topo.Points.Count; p++) if (topo.Points[p].Vertex == it.Component) result.Add(new SelItem(it.Node, p));
        }
        return result;
    }

    /// <summary>본 피킹: (parent, child, 본 위 비율 t). Insert Joint Tool용.</summary>
    public bool PickBone(NVec2 p, out NodeId parent, out NodeId child, out float t)
    {
        parent = child = NodeId.None; t = 0.5f;
        var proj = Projection();
        float best = 8f * Scale;
        foreach (var (id, jv) in _panel.Scene.JointViews)
        {
            if (!jv.Visible) continue;
            var sp = proj.Project(jv.GlobalPosition.ToNumerics(), out _);
            if (sp == null) continue;
            foreach (var c in jv.Node.Children)
            {
                if (!c.IsJoint || !_panel.Scene.JointViews.TryGetValue(c.Id, out var cv)) continue;
                var cp = proj.Project(cv.GlobalPosition.ToNumerics(), out _);
                if (cp == null) continue;
                float tt = RayPicker.ClosestParam(sp.Value, cp.Value, p);
                float d = NVec2.Distance(sp.Value + (cp.Value - sp.Value) * tt, p);
                if (d < best) { best = d; parent = id; child = c.Id; t = tt; }
            }
        }
        return !parent.IsNone;
    }

    /// <summary>라이트 아이콘 피킹(아이콘 중심 12px).</summary>
    public PickHit? PickLight(NVec2 p)
    {
        var proj = Projection();
        float best = 12f * Scale; PickHit? hit = null;
        foreach (var (id, lv) in _panel.Scene.LightViews)
        {
            if (!lv.Visible) continue;
            var w = lv.GlobalPosition.ToNumerics();
            var sp = proj.Project(w, out float depth);
            if (sp == null) continue;
            float d = NVec2.Distance(sp.Value, p);
            if (d < best) { best = d; hit = new PickHit(id, -1, depth, w); }
        }
        return hit;
    }

    public PickHit? Pick(GVec2 px, SelectMode mode, bool cameraBased)
    {
        var p = new NVec2(px.X, px.Y);
        var targets = Targets();
        if (mode == SelectMode.Object && PickJoint(p) is { } jh) return jh;
        if (mode == SelectMode.Object && PickLight(p) is { } lh) return lh;
        return mode switch
        {
            SelectMode.Vertex or SelectMode.Uv => RayPicker.PickVertex(targets, Projection(), p, cameraBased, RayPicker.VertexThresholdPx * Scale),
            SelectMode.Edge => RayPicker.PickEdge(targets, Projection(), p, cameraBased, RayPicker.EdgeThresholdPx * Scale),
            _ => RayPicker.Pick(targets, Projection(), p, mode, cameraBased),
        };
    }

    public List<SelItem> Marquee(Rect2 rect, SelectMode mode, bool cameraBased)
    {
        var min = new NVec2(rect.Position.X, rect.Position.Y);
        var max = new NVec2(rect.End.X, rect.End.Y);
        var items = RayPicker.Marquee(Targets(), Projection(), min, max, mode == SelectMode.Uv ? SelectMode.Vertex : mode, cameraBased);
        if (mode == SelectMode.Uv) items = ExpandUv(items);
        if (mode == SelectMode.Object)
        {
            var proj = Projection();
            foreach (var (id, jv) in _panel.Scene.JointViews)
            {
                if (!jv.Visible) continue;
                var sp = proj.Project(jv.GlobalPosition.ToNumerics(), out _);
                if (sp != null && RayPicker.Inside(sp.Value, min, max)) items.Add(new SelItem(id, -1));
            }
            foreach (var (id, lv) in _panel.Scene.LightViews)
            {
                if (!lv.Visible) continue;
                var sp = proj.Project(lv.GlobalPosition.ToNumerics(), out _);
                if (sp != null && RayPicker.Inside(sp.Value, min, max)) items.Add(new SelItem(id, -1));
            }
        }
        return items;
    }
}
