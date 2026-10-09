
using Cube.App.Bridge;
using Cube.Core.Picking;
using Cube.Core.Scene;
using Cube.Core.Selection;
using Godot;
using GVec2 = Godot.Vector2;
using NVec2 = System.Numerics.Vector2;

namespace Cube.App.Viewport;

/// <summary>Godot 카메라/뷰에서 CameraProjection과 PickTarget 목록을 만들어 RayPicker를 호출한다.</summary>
/// <remarks>
/// 패널마다 하나 있으며 툴(SelectTool, 변형 툴, 모델링 툴)과 조작기가 화면 좌표 → 컴포넌트/오브젝트 변환에 쓴다.
/// 실제 판정(레이-삼각형, 화면 거리 임계, 가림 처리, 마키)은 코어 <c>RayPicker</c>가 하고, 여기서는 Godot 카메라·뷰 노드에서 입력을 모으고
/// 조인트/라이트처럼 메시가 아닌 대상을 화면 거리로 직접 판정한다. 모든 픽셀 임계값은 UI 배율(<see cref="Scale"/>)을 곱한다.
/// </remarks>
public sealed class Picker
{
    /// <summary>피킹 대상 뷰(카메라·SceneView)를 가진 패널.</summary>
    private readonly ViewportPanel _panel;
    /// <summary>패널을 받아 피커를 만든다.</summary>
    public Picker(ViewportPanel panel) { _panel = panel; }

    /// <summary>픽셀 임계값에 곱할 UI 배율(Hi-DPI × 사용자 배율).</summary>
    public float Scale => CubeApp.Instance.UiScale;

    /// <summary>
    /// 현재 Godot 카메라 상태로 코어 <c>CameraProjection</c>(월드 행렬, 직교 여부, 수직 FOV 라디안, 직교 크기, near/far, 뷰포트 픽셀 크기)을 만든다.
    /// 호출할 때마다 새로 만들므로 카메라가 움직인 직후에도 정확하다.
    /// </summary>
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

    /// <summary>
    /// 메시 피킹 대상 목록(노드 ID, 메시, 케이지 렌더 데이터, 월드 행렬)을 만든다. 숨긴 노드는 제외하고,
    /// 컴포넌트 모드에서 편집 대상이 정해져 있으면 그 개체(또는 컴포넌트를 가진 노드)만 넣는다.
    /// </summary>
    /// <param name="componentFilter">false면 컴포넌트 편집 대상 제한 없이 보이는 메시 전부(점 스냅 후보 등).</param>
    public List<PickTarget> Targets(bool componentFilter = true)
    {
        var list = new List<PickTarget>();
        foreach (var (id, mv) in _panel.Scene.MeshViews)
        {
            if (!mv.IsVisibleInTree() || mv.Node.Mesh == null) continue; // 부모 그룹이 숨겨진 메시도 제외(로컬 Visible만 보면 숨은 계층의 메시가 집혔음)
            // 컴포넌트 모드: 편집 대상 개체(하나)만. 대상이 아직 없으면 처음 집을 개체를 고를 수 있게 전부.
            var sel = UI.Shell.Instance?.Document.Selection;
            if (componentFilter && sel != null && sel.IsComponentMode && sel.ComponentTarget != Core.Scene.NodeId.None && !sel.IsComponentEditable(id)) continue;
            list.Add(new PickTarget { Id = id, Mesh = mv.Node.Mesh, Render = mv.Render, World = mv.GlobalTransform.ToNumerics() });
        }
        return list;
    }

    /// <summary>오브젝트 모드 조인트 피킹: 조인트 구(10px) 또는 본 선분(6px)에 가까우면 그 조인트.</summary>
    public PickHit? PickJoint(NVec2 p)
    {
        // 조인트 구: 화면 중심과의 거리가 반지름 안이면 후보
        var proj = Projection();
        float best = float.MaxValue; PickHit? hit = null;
        float rJoint = 10f * Scale, rBone = 6f * Scale;
        foreach (var (id, jv) in _panel.Scene.JointViews)
        {
            if (!jv.Pickable) continue;
            var w = jv.GlobalPosition.ToNumerics();
            var sp = proj.Project(w, out float depth);
            if (sp == null) continue;
            float d = NVec2.Distance(sp.Value, p);
            if (d <= rJoint && d < best) { best = d; hit = new PickHit(id, -1, depth, w); }
            // 이 조인트에서 자식 조인트로 가는 본 선분: 선분 위 최근접점까지 거리(+2px 페널티)로 비교
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
    /// <param name="vertexItems">정점 컴포넌트 선택 항목들.</param>
    /// <returns>각 정점을 공유하는 UV 점 ID(UvTopology 순서) 항목들.</returns>
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
    /// <param name="p">뷰포트 로컬 픽셀.</param>
    /// <param name="parent">본의 부모(시작) 조인트.</param>
    /// <param name="child">본의 자식(끝) 조인트.</param>
    /// <param name="t">화면에서 본 선분 위 최근접점의 비율(0 = 부모, 1 = 자식).</param>
    /// <returns>8px 안에 본이 있으면 true.</returns>
    public bool PickBone(NVec2 p, out NodeId parent, out NodeId child, out float t)
    {
        parent = child = NodeId.None; t = 0.5f;
        var proj = Projection();
        float best = 8f * Scale;
        foreach (var (id, jv) in _panel.Scene.JointViews)
        {
            if (!jv.Pickable) continue;
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

    /// <summary>라이트 아이콘 피킹: 아이콘 중심 12px 또는 아이콘 선(스포트 원뿔·방향 화살표 등) 6px 이내.</summary>
    /// <returns>가장 가까운 보이는 라이트 노드 히트(컴포넌트 -1), 없으면 null.</returns>
    public PickHit? PickLight(NVec2 p)
    {
        var proj = Projection();
        float best = 12f * Scale, lineTol = 6f * Scale; PickHit? hit = null;
        foreach (var (id, lv) in _panel.Scene.LightViews)
        {
            if (!lv.IsVisibleInTree()) continue;
            var w = lv.GlobalPosition.ToNumerics();
            var sp = proj.Project(w, out float depth);
            if (sp == null) continue;
            float d = NVec2.Distance(sp.Value, p);
            // 아이콘 선분까지의 화면 거리(중심보다 조금 불리하게: 중심 임계 12px 대비 선 임계 6px로 환산)
            foreach (var (a, b) in lv.IconSegmentsWorld())
            {
                var pa = proj.Project(a.ToNumerics(), out _); var pb = proj.Project(b.ToNumerics(), out _);
                if (pa == null || pb == null) continue;
                float t = RayPicker.ClosestParam(pa.Value, pb.Value, p);
                float dl = NVec2.Distance(pa.Value + (pb.Value - pa.Value) * t, p);
                if (dl <= lineTol) d = MathF.Min(d, dl * (12f / 6f));
            }
            if (d < best) { best = d; hit = new PickHit(id, -1, depth, w); }
        }
        return hit;
    }

    /// <summary>
    /// 클릭 한 번의 피킹. 오브젝트 모드는 조인트 → 라이트 → 메시 면 순으로 우선한다.
    /// 정점/UV 모드는 정점 화면 거리, 엣지 모드는 엣지 화면 거리, 면/오브젝트는 레이-삼각형으로 판정한다.
    /// </summary>
    /// <param name="px">뷰포트 로컬 픽셀.</param>
    /// <param name="mode">현재 선택 모드.</param>
    /// <param name="cameraBased">true면 가려진 요소를 제외(보이는 요소 우선).</param>
    /// <returns>히트(노드, 컴포넌트 ID, 깊이, 월드 점) 또는 null. UV 모드는 정점 ID를 돌려주며 호출자가 <see cref="ExpandUv"/>로 바꾼다.</returns>
    public PickHit? Pick(GVec2 px, SelectMode mode, bool cameraBased)
    {
        var p = new NVec2(px.X, px.Y);
        long t0 = AnimPerf.Begin();
        var targets = Targets();
        // 오브젝트 모드: 메시가 아닌 대상(조인트, 라이트)을 먼저 본다
        if (mode == SelectMode.Object && PickJoint(p) is { } jh) { AnimPerf.End("pick", t0); return jh; }
        if (mode == SelectMode.Object && PickLight(p) is { } lh) { AnimPerf.End("pick", t0); return lh; }
        // 모드별 코어 피커 호출(픽셀 임계값은 UI 배율 반영)
        var hit = mode switch
        {
            SelectMode.Vertex or SelectMode.Uv => RayPicker.PickVertex(targets, Projection(), p, cameraBased, RayPicker.VertexThresholdPx * Scale),
            SelectMode.Edge => RayPicker.PickEdge(targets, Projection(), p, cameraBased, RayPicker.EdgeThresholdPx * Scale),
            _ => RayPicker.Pick(targets, Projection(), p, mode, cameraBased),
        };
        AnimPerf.End("pick", t0);
        return hit;
    }

    /// <summary>
    /// 마키(박스) 선택. 코어 RayPicker.Marquee로 메시 요소를 모으고(UV 모드는 정점으로 모은 뒤 UV 점으로 확장),
    /// 오브젝트 모드에서는 화면 중심이 상자 안에 든 조인트·라이트도 더한다.
    /// </summary>
    /// <param name="rect">뷰포트 로컬 픽셀 사각형.</param>
    /// <param name="mode">선택 모드.</param>
    /// <param name="cameraBased">true면 가려진 요소 제외(Settings.MarqueeSelectThrough가 꺼졌을 때).</param>
    public List<SelItem> Marquee(Rect2 rect, SelectMode mode, bool cameraBased)
    {
        var min = new NVec2(rect.Position.X, rect.Position.Y);
        var max = new NVec2(rect.End.X, rect.End.Y);
        var items = RayPicker.Marquee(Targets(), Projection(), min, max, mode == SelectMode.Uv ? SelectMode.Vertex : mode, cameraBased);
        if (mode == SelectMode.Uv) items = ExpandUv(items);
        // 오브젝트 모드: 조인트와 라이트 아이콘의 화면 위치로 판정
        if (mode == SelectMode.Object)
        {
            var proj = Projection();
            foreach (var (id, jv) in _panel.Scene.JointViews)
            {
                if (!jv.Pickable) continue;
                var sp = proj.Project(jv.GlobalPosition.ToNumerics(), out _);
                if (sp != null && RayPicker.Inside(sp.Value, min, max)) items.Add(new SelItem(id, -1));
            }
            foreach (var (id, lv) in _panel.Scene.LightViews)
            {
                if (!lv.IsVisibleInTree()) continue;
                var sp = proj.Project(lv.GlobalPosition.ToNumerics(), out _);
                if (sp != null && RayPicker.Inside(sp.Value, min, max)) items.Add(new SelItem(id, -1));
            }
        }
        return items;
    }

    /// <summary>
    /// 자유 곡선(Lasso) 선택. 코어 RayPicker.Lasso로 화면 다각형 안의 메시 요소를 모으고(UV 모드는 정점 → UV 점 확장),
    /// 오브젝트 모드에서는 화면 위치가 다각형 안에 든 조인트·라이트도 더한다. 판정 규칙은 마키와 같다.
    /// </summary>
    /// <param name="points">드래그 경로(뷰포트 로컬 픽셀, 마지막 → 처음으로 닫힌 것으로 본다).</param>
    public List<SelItem> Lasso(IReadOnlyList<Vector2> points, SelectMode mode, bool cameraBased)
    {
        var poly = new List<NVec2>(points.Count);
        foreach (var p in points) poly.Add(new NVec2(p.X, p.Y));
        var items = RayPicker.Lasso(Targets(), Projection(), poly, mode == SelectMode.Uv ? SelectMode.Vertex : mode, cameraBased);
        if (mode == SelectMode.Uv) items = ExpandUv(items);
        if (mode == SelectMode.Object && poly.Count >= 3)
        {
            var proj = Projection();
            foreach (var (id, jv) in _panel.Scene.JointViews)
            {
                if (!jv.Pickable) continue;
                var sp = proj.Project(jv.GlobalPosition.ToNumerics(), out _);
                if (sp != null && RayPicker.InsidePolygon(sp.Value, poly)) items.Add(new SelItem(id, -1));
            }
            foreach (var (id, lv) in _panel.Scene.LightViews)
            {
                if (!lv.IsVisibleInTree()) continue;
                var sp = proj.Project(lv.GlobalPosition.ToNumerics(), out _);
                if (sp != null && RayPicker.InsidePolygon(sp.Value, poly)) items.Add(new SelItem(id, -1));
            }
        }
        return items;
    }
}
