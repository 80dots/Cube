using Cube.Core.Geometry;
using Cube.Core.Picking;
using Godot;
using NVec2 = System.Numerics.Vector2;

namespace Cube.App.Viewport.Gizmos;

/// <summary>Maya 이동 조작기: 축 화살표 3개 + 평면 핸들 3개 + 중앙 사각형(화면 평행 이동).</summary>
/// <remarks>
/// 축 선은 0..0.8, 화살촉 원뿔은 0.8..1.0(로컬 단위). 평면 핸들은 두 축의 [PlaneMin, PlaneMax] 사각형이며 색은 법선 축의 색을 쓴다(XY = 파랑).
/// 히트 우선순위는 중앙 &gt; 평면 &gt; 축이다.
/// </remarks>
public partial class MoveGizmo : GizmoBase
{
    /// <summary>평면 핸들 사각형의 축 방향 시작/끝 위치(로컬 단위).</summary>
    public const float PlaneMin = 0.3f, PlaneMax = 0.55f;
    /// <summary>중앙 핸들 크기(로컬 단위). 실제 큐브 반변은 이 값의 0.6배.</summary>
    public const float CenterSize = 0.08f;

    /// <summary>선(축·평면 외곽선) 서피스와 삼각형(화살촉·평면 채움·중앙 큐브) 서피스를 다시 만든다.</summary>
    protected override void Rebuild()
    {
        Mesh.ClearSurfaces();
        var cx = PartColor(GizmoPart.X, ColX); var cy = PartColor(GizmoPart.Y, ColY); var cz = PartColor(GizmoPart.Z, ColZ);

        // 1) 선 서피스: 축 선 3개
        Mesh.SurfaceBegin(Godot.Mesh.PrimitiveType.Lines);
        Line(Vector3.Zero, Vector3.Right * 0.8f, cx);
        Line(Vector3.Zero, Vector3.Up * 0.8f, cy);
        Line(Vector3.Zero, Vector3.Back * 0.8f, cz);
        // 평면 핸들 외곽선
        PlaneOutline(GizmoPart.XY, Vector3.Right, Vector3.Up, PartColor(GizmoPart.XY, ColZ));
        PlaneOutline(GizmoPart.YZ, Vector3.Up, Vector3.Back, PartColor(GizmoPart.YZ, ColX));
        PlaneOutline(GizmoPart.XZ, Vector3.Right, Vector3.Back, PartColor(GizmoPart.XZ, ColY));
        Mesh.SurfaceEnd();

        // 2) 삼각형 서피스: 화살촉 원뿔 + 반투명 평면 채움
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

    /// <summary>평면 핸들 사각형의 외곽선 4개를 그린다(a, b = 두 축 방향).</summary>
    private void PlaneOutline(GizmoPart part, Vector3 a, Vector3 b, Color c)
    {
        var p0 = a * PlaneMin + b * PlaneMin; var p1 = a * PlaneMax + b * PlaneMin; var p2 = a * PlaneMax + b * PlaneMax; var p3 = a * PlaneMin + b * PlaneMax;
        Line(p0, p1, c); Line(p1, p2, c); Line(p2, p3, c); Line(p3, p0, c);
    }

    /// <summary>평면 핸들 사각형을 삼각형 2개로 채운다(반투명 색).</summary>
    private void PlaneFill(Vector3 a, Vector3 b, Color c)
    {
        var p0 = a * PlaneMin + b * PlaneMin; var p1 = a * PlaneMax + b * PlaneMin; var p2 = a * PlaneMax + b * PlaneMax; var p3 = a * PlaneMin + b * PlaneMax;
        Tri(p0, p1, p2, c); Tri(p0, p2, p3, c);
    }

    /// <summary>중앙(10px) → 평면 핸들(투영 사각형 안) → 축 선분(HitPx 이내 가장 가까운 것) 순으로 판정한다.</summary>
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
        // 축: 피벗 화면점 ~ 축 끝 화면점 선분까지의 거리가 가장 짧은 축
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
