using Cube.Core.Geometry;
using Cube.Core.Picking;
using Godot;
using NVec2 = System.Numerics.Vector2;

namespace Cube.App.Viewport.Gizmos;

/// <summary>Maya 이동 조작기: 축 화살표 3개 + 평면 핸들 3개 + 중앙 사각형(화면 평행 이동).</summary>
public partial class MoveGizmo : GizmoBase
{
    public const float PlaneMin = 0.3f, PlaneMax = 0.55f;
    public const float CenterSize = 0.08f;

    protected override void Rebuild()
    {
        Mesh.ClearSurfaces();
        var cx = PartColor(GizmoPart.X, ColX); var cy = PartColor(GizmoPart.Y, ColY); var cz = PartColor(GizmoPart.Z, ColZ);

        Mesh.SurfaceBegin(Godot.Mesh.PrimitiveType.Lines);
        Line(Vector3.Zero, Vector3.Right * 0.8f, cx);
        Line(Vector3.Zero, Vector3.Up * 0.8f, cy);
        Line(Vector3.Zero, Vector3.Back * 0.8f, cz);
        // 평면 핸들 외곽선
        PlaneOutline(GizmoPart.XY, Vector3.Right, Vector3.Up, PartColor(GizmoPart.XY, ColZ));
        PlaneOutline(GizmoPart.YZ, Vector3.Up, Vector3.Back, PartColor(GizmoPart.YZ, ColX));
        PlaneOutline(GizmoPart.XZ, Vector3.Right, Vector3.Back, PartColor(GizmoPart.XZ, ColY));
        Mesh.SurfaceEnd();

        Mesh.SurfaceBegin(Godot.Mesh.PrimitiveType.Triangles);
        Cone(Vector3.Right, 0.8f, 0.2f, 0.05f, cx);
        Cone(Vector3.Up, 0.8f, 0.2f, 0.05f, cy);
        Cone(Vector3.Back, 0.8f, 0.2f, 0.05f, cz);
        PlaneFill(Vector3.Right, Vector3.Up, PartColor(GizmoPart.XY, ColZ) with { A = 0.35f });
        PlaneFill(Vector3.Up, Vector3.Back, PartColor(GizmoPart.YZ, ColX) with { A = 0.35f });
        PlaneFill(Vector3.Right, Vector3.Back, PartColor(GizmoPart.XZ, ColY) with { A = 0.35f });
        // 중앙 사각형: 카메라를 향한 빌보드 대신 작은 큐브로 근사
        Cube(Vector3.Zero, CenterSize * 0.6f, PartColor(GizmoPart.Center, ColCenter));
        Mesh.SurfaceEnd();
    }

    private void PlaneOutline(GizmoPart part, Vector3 a, Vector3 b, Color c)
    {
        var p0 = a * PlaneMin + b * PlaneMin; var p1 = a * PlaneMax + b * PlaneMin; var p2 = a * PlaneMax + b * PlaneMax; var p3 = a * PlaneMin + b * PlaneMax;
        Line(p0, p1, c); Line(p1, p2, c); Line(p2, p3, c); Line(p3, p0, c);
    }

    private void PlaneFill(Vector3 a, Vector3 b, Color c)
    {
        var p0 = a * PlaneMin + b * PlaneMin; var p1 = a * PlaneMax + b * PlaneMin; var p2 = a * PlaneMax + b * PlaneMax; var p3 = a * PlaneMin + b * PlaneMax;
        Tri(p0, p1, p2, c); Tri(p0, p2, p3, c);
    }

    public override GizmoPart HitTest(NVec2 px, CameraProjection proj)
    {
        float s = CubeApp.Instance.UiScale;
        var o = Proj(proj, Vector3.Zero);
        if (o == null) return GizmoPart.None;
        // 우선순위: 중앙 > 평면 > 축
        if (NVec2.Distance(o.Value, px) <= 10f * s) return GizmoPart.Center;
        foreach (var (part, a, b) in new[] { (GizmoPart.XY, Vector3.Right, Vector3.Up), (GizmoPart.YZ, Vector3.Up, Vector3.Back), (GizmoPart.XZ, Vector3.Right, Vector3.Back) })
        {
            var q = new NVec2[4];
            var corners = new[] { a * PlaneMin + b * PlaneMin, a * PlaneMax + b * PlaneMin, a * PlaneMax + b * PlaneMax, a * PlaneMin + b * PlaneMax };
            bool ok = true;
            for (int i = 0; i < 4; i++) { var p = Proj(proj, corners[i]); if (p == null) { ok = false; break; } q[i] = p.Value; }
            // 뷰와 평행해 선으로 보이는 평면 핸들은 잡을 수 없다(면적이 (6px)² 미만이면 무시)
            if (ok && MathF.Abs(DragMath.PolygonArea(q)) >= 36f * s * s && DragMath.PointInConvexPolygon(px, q)) return part;
        }
        GizmoPart best = GizmoPart.None; float bestD = HitPx * s;
        foreach (var (part, axis) in new[] { (GizmoPart.X, Vector3.Right), (GizmoPart.Y, Vector3.Up), (GizmoPart.Z, Vector3.Back) })
        {
            var e = Proj(proj, axis);
            if (e == null) continue;
            float d = DragMath.DistanceToSegment(px, o.Value, e.Value);
            if (d < bestD) { bestD = d; best = part; }
        }
        return best;
    }
}
