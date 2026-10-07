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

    /// <summary>마우스 이동 대비 스케일 변화량(사용자 요청: 기존의 40%).</summary>
    public const float Sensitivity = 0.4f;
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
            float ratio = 1f + (MathF.Max(NVec2.Distance(_centerPx, px), 1f) / _startDist - 1f) * Sensitivity;
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
            ratio = 1f + (ratio - 1f) * Sensitivity;
            if (MathF.Abs(ratio) < 1e-3f) ratio = 1e-3f;
            scale = DragPart switch { GizmoPart.X => new NVec3(ratio, 1, 1), GizmoPart.Y => new NVec3(1, ratio, 1), _ => new NVec3(1, 1, ratio) };
        }
        if (Ctx.Viewport.IsSnapHeld) scale = new NVec3(Snap(scale.X), Snap(scale.Y), Snap(scale.Z));
        ApplyScale(scale);
    }

    private static float Snap(float v)
    {
        float step = MathF.Max(CubeApp.Instance.Settings.ScaleSnapStep, 0.001f); // J: 증분 스케일(Preferences, 기본 0.25)
        return MathF.Max(MathF.Round(v / step) * step, step);
    }
}
