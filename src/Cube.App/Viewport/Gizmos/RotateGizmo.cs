using Cube.App.Bridge;
using Cube.Core.Geometry;
using Cube.Core.Picking;
using Godot;
using NVec2 = System.Numerics.Vector2;
using NVec3 = System.Numerics.Vector3;

namespace Cube.App.Viewport.Gizmos;

/// <summary>Maya 회전 조작기: 축별 링 3개 + 화면 평행 외곽 링.</summary>
public partial class RotateGizmo : GizmoBase
{
    public const int Segments = 48;
    public const float OuterRadius = 1.25f;

    private NVec3 _camRight = NVec3.UnitX, _camUp = NVec3.UnitY;

    protected override void Rebuild()
    {
        Mesh.ClearSurfaces();
        Mesh.SurfaceBegin(Godot.Mesh.PrimitiveType.Lines);
        Ring(Vector3.Right, PartColor(GizmoPart.X, ColX));
        Ring(Vector3.Up, PartColor(GizmoPart.Y, ColY));
        Ring(Vector3.Back, PartColor(GizmoPart.Z, ColZ));
        // 외곽 링은 월드 공간 카메라 기저로: 로컬 좌표로 변환해 그린다
        var proj = Panel.Picker.Projection();
        _camRight = proj.Right; _camUp = proj.Up;
        var col = PartColor(GizmoPart.Screen, ColCenter);
        for (int i = 0; i < Segments; i++)
        {
            float a0 = MathF.Tau * i / Segments, a1 = MathF.Tau * (i + 1) / Segments;
            Line(ToLocal(OuterPoint(a0)), ToLocal(OuterPoint(a1)), col);
        }
        Mesh.SurfaceEnd();
        Mesh.SurfaceBegin(Godot.Mesh.PrimitiveType.Triangles);
        Cube(Vector3.Zero, 0.04f, ColCenter);
        Mesh.SurfaceEnd();
    }

    private void Ring(Vector3 axis, Color col)
    {
        var (u, v) = Perp(axis);
        for (int i = 0; i < Segments; i++)
        {
            float a0 = MathF.Tau * i / Segments, a1 = MathF.Tau * (i + 1) / Segments;
            Line(u * MathF.Cos(a0) + v * MathF.Sin(a0), u * MathF.Cos(a1) + v * MathF.Sin(a1), col);
        }
    }

    /// <summary>외곽 링의 월드 점(피벗 기준 오프셋, 단위 길이 기준).</summary>
    private NVec3 OuterPoint(float a) => (_camRight * MathF.Cos(a) + _camUp * MathF.Sin(a)) * OuterRadius;

    /// <summary>월드 오프셋 → 기즈모 로컬(축 기저, 단위 길이).</summary>
    private Vector3 ToLocal(NVec3 worldOffset)
        => new(NVec3.Dot(worldOffset, AxisX), NVec3.Dot(worldOffset, AxisY), NVec3.Dot(worldOffset, AxisZ));

    public override GizmoPart HitTest(NVec2 px, CameraProjection proj)
    {
        float s = CubeApp.Instance.UiScale;
        float thr = HitPx * s;
        var c = proj.Project(Pivot, out _);
        if (c == null) return GizmoPart.None;
        // 외곽 링: 화면 반지름
        float rPx = ScreenSizePx * s * OuterRadius;
        if (MathF.Abs(NVec2.Distance(c.Value, px) - rPx) <= thr) return GizmoPart.Screen;

        GizmoPart best = GizmoPart.None; float bestD = thr;
        foreach (var (part, axis) in new[] { (GizmoPart.X, Vector3.Right), (GizmoPart.Y, Vector3.Up), (GizmoPart.Z, Vector3.Back) })
        {
            var (u, v) = Perp(axis);
            NVec2? prev = null;
            for (int i = 0; i <= Segments; i++)
            {
                float a = MathF.Tau * i / Segments;
                var p = Proj(proj, u * MathF.Cos(a) + v * MathF.Sin(a));
                if (p != null && prev != null)
                {
                    float d = DragMath.DistanceToSegment(px, prev.Value, p.Value);
                    if (d < bestD) { bestD = d; best = part; }
                }
                prev = p;
            }
        }
        return best;
    }
}
