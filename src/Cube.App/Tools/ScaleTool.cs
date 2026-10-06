using System.Numerics;
using Cube.App.Viewport.Gizmos;
using Cube.Core.Geometry;
using Cube.Core.Picking;
using NVec2 = System.Numerics.Vector2;
using NVec3 = System.Numerics.Vector3;

namespace Cube.App.Tools;

/// <summary>Maya Scale Tool (R). 축 핸들은 축 방향 스케일, 중앙은 균등 스케일.</summary>
public sealed class ScaleTool : TransformToolBase
{
    public override string Id => "scale";
    public override string Label => "Scale";
    public override string HelpText => "Scale Tool: drag an axis handle to scale along it, the center to scale uniformly.";

    private NVec2 _centerPx;
    private float _startT;
    private float _startDist;

    protected override GizmoBase CreateGizmo() => new ScaleGizmo();

    protected override void OnDragBegin(CameraProjection proj)
    {
        _centerPx = proj.Project(PivotWorld, out _) ?? PressPx;
        _startDist = MathF.Max(NVec2.Distance(_centerPx, PressPx), 1f);
        if (DragPart != GizmoPart.Center)
        {
            if (!DragMath.ClosestParamOnAxis(PivotWorld, Gizmo.AxisOf(DragPart), PressRay, out _startT) || MathF.Abs(_startT) < 1e-4f)
                _startT = float.NaN;
        }
    }

    protected override void UpdateDrag(NVec2 px, CameraProjection proj)
    {
        NVec3 scale;
        if (DragPart == GizmoPart.Center)
        {
            float ratio = MathF.Max(NVec2.Distance(_centerPx, px), 1f) / _startDist;
            scale = new NVec3(ratio);
        }
        else
        {
            float ratio;
            if (float.IsNaN(_startT))
            {
                // 축이 시선과 평행: 피벗으로부터의 화면 거리 비율
                ratio = MathF.Max(NVec2.Distance(_centerPx, px), 1f) / _startDist;
            }
            else
            {
                if (!DragMath.ClosestParamOnAxis(PivotWorld, Gizmo.AxisOf(DragPart), proj.Unproject(px), out float t)) return;
                ratio = t / _startT;
            }
            if (MathF.Abs(ratio) < 1e-3f) ratio = 1e-3f;
            scale = DragPart switch { GizmoPart.X => new NVec3(ratio, 1, 1), GizmoPart.Y => new NVec3(1, ratio, 1), _ => new NVec3(1, 1, ratio) };
        }
        if (Ctx.Viewport.IsSnapHeld) scale = new NVec3(Snap(scale.X), Snap(scale.Y), Snap(scale.Z));
        // 기즈모 축 기저에서 스케일: T(-p) · Bᵀ · S · B · T(p)
        var b = new Matrix4x4(
            Gizmo.AxisX.X, Gizmo.AxisX.Y, Gizmo.AxisX.Z, 0,
            Gizmo.AxisY.X, Gizmo.AxisY.Y, Gizmo.AxisY.Z, 0,
            Gizmo.AxisZ.X, Gizmo.AxisZ.Y, Gizmo.AxisZ.Z, 0,
            0, 0, 0, 1);
        var m = Matrix4x4.CreateTranslation(-PivotWorld) * Matrix4x4.Transpose(b) * Matrix4x4.CreateScale(scale) * b * Matrix4x4.CreateTranslation(PivotWorld);
        ApplyWorldMatrixAboutPivot(m);
    }

    private static float Snap(float v) => MathF.Max(MathF.Round(v * 4f) / 4f, 0.25f);
}
