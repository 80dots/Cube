using Cube.Core.Geometry;
using Cube.Core.Picking;
using Godot;
using NVec2 = System.Numerics.Vector2;

namespace Cube.App.Viewport.Gizmos;

/// <summary>Maya 스케일 조작기: 큐브 팁 축 3개 + 중앙 큐브(균등).</summary>
/// <remarks>축 선은 0..<see cref="TipAt"/>, 끝에 작은 큐브. 중앙 큐브(Center)는 균등 스케일 핸들이다.</remarks>
public partial class ScaleGizmo : GizmoBase
{
    /// <summary>축 끝 큐브의 위치(로컬 단위).</summary>
    public const float TipAt = 0.9f;

    /// <summary>축 선 3개(선 서피스)와 축 끝 큐브 3개 + 중앙 큐브(삼각형 서피스)를 다시 만든다.</summary>
    protected override void Rebuild()
    {
        Mesh.ClearSurfaces();
        var cx = PartColor(GizmoPart.X, ColX); var cy = PartColor(GizmoPart.Y, ColY); var cz = PartColor(GizmoPart.Z, ColZ);
        Mesh.SurfaceBegin(Godot.Mesh.PrimitiveType.Lines);
        Line(Vector3.Zero, Vector3.Right * TipAt, cx);
        Line(Vector3.Zero, Vector3.Up * TipAt, cy);
        Line(Vector3.Zero, Vector3.Back * TipAt, cz);
        Mesh.SurfaceEnd();
        Mesh.SurfaceBegin(Godot.Mesh.PrimitiveType.Triangles);
        Cube(Vector3.Right * TipAt, 0.06f, cx);
        Cube(Vector3.Up * TipAt, 0.06f, cy);
        Cube(Vector3.Back * TipAt, 0.06f, cz);
        Cube(Vector3.Zero, 0.07f, PartColor(GizmoPart.Center, ColCenter));
        Mesh.SurfaceEnd();
    }

    /// <summary>중앙(10px 이내) 우선, 아니면 피벗~축 끝 선분까지 HitPx 이내로 가장 가까운 축.</summary>
    public override GizmoPart HitTest(NVec2 px, CameraProjection proj)
    {
        float s = CubeApp.Instance.UiScale;
        var o = Proj(proj, Vector3.Zero);
        if (o == null) return GizmoPart.None;
        if (NVec2.Distance(o.Value, px) <= 10f * s) return GizmoPart.Center;
        GizmoPart best = GizmoPart.None; float bestD = HitPx * s;
        foreach (var (part, axis) in new[] { (GizmoPart.X, Vector3.Right), (GizmoPart.Y, Vector3.Up), (GizmoPart.Z, Vector3.Back) })
        {
            var e = Proj(proj, axis * TipAt);
            if (e == null) continue;
            float d = DragMath.DistanceToSegment(px, o.Value, e.Value);
            if (d < bestD) { bestD = d; best = part; }
        }
        return best;
    }
}
