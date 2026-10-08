using System.Numerics;
using Cube.App.Viewport.Gizmos;
using Cube.Core.Geometry;
using Cube.Core.Picking;
using NVec2 = System.Numerics.Vector2;
using NVec3 = System.Numerics.Vector3;

namespace Cube.App.Tools;

/// <summary>Maya Scale Tool (R). 축 핸들은 축 방향 스케일, 중앙은 균등 스케일.</summary>
/// <remarks>
/// 축 핸들 = 시작 레이의 축 매개변수 대비 현재 매개변수 비율(축이 시선과 평행하거나 피벗에 너무 가까우면 화면 거리 비율),
/// 중앙 = 피벗 화면 투영점으로부터의 거리 비율. 비율은 <see cref="Sensitivity"/>로 1 주변에서 완화한 뒤 적용하며,
/// J 홀드면 Preferences 단위(기본 0.25)로 반올림한다. 실제 적용은 <see cref="TransformToolBase.ApplyScale"/>(기즈모 축 기저).
/// </remarks>
public sealed class ScaleTool : TransformToolBase
{
    /// <summary>툴 ID("scale", R).</summary>
    public override string Id => "scale";
    /// <summary>표시 이름(커밋 명령 이름).</summary>
    public override string Label => "Scale";
    /// <summary>헬프 라인 안내.</summary>
    public override string HelpText => "Scale Tool: drag an axis handle to scale along it, the center to scale uniformly.";

    /// <summary>마우스 이동 대비 스케일 변화량(사용자 요청: 기존의 40%).</summary>
    public const float Sensitivity = 0.4f;
    /// <summary>피벗의 화면 투영 위치(뷰포트 로컬 픽셀).</summary>
    private NVec2 _centerPx;
    /// <summary>축 드래그 시작 시 축 위 매개변수(피벗 기준). NaN이면 화면 거리 비율 폴백.</summary>
    private float _startT;
    /// <summary>시작 시 피벗 화면점과 누른 점의 거리(최소 1px). 중앙/폴백 비율의 분모.</summary>
    private float _startDist;

    /// <summary>스케일 조작기(축 상자 3개 + 중앙)를 만든다.</summary>
    protected override GizmoBase CreateGizmo() => new ScaleGizmo();

    /// <summary>드래그 시작: 화면 중심·시작 거리와(축 핸들이면) 축 시작 매개변수를 구한다. 0에 가까우면 비율이 불안정하므로 NaN.</summary>
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

    /// <summary>드래그 갱신: 핸들별 비율을 계산해 감도·최소값·스냅을 적용한 뒤 기즈모 축 기준 스케일을 프리뷰한다.</summary>
    protected override void UpdateDrag(NVec2 px, CameraProjection proj)
    {
        NVec3 scale;
        if (DragPart == GizmoPart.Center)
        {
            // 균등 스케일: 화면 거리 비율을 감도로 완화
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
            // 감도 완화와 0/음수 붕괴 방지(최소 0.001), 해당 축 성분에만 배율
            ratio = 1f + (ratio - 1f) * Sensitivity;
            if (MathF.Abs(ratio) < 1e-3f) ratio = 1e-3f;
            scale = DragPart switch { GizmoPart.X => new NVec3(ratio, 1, 1), GizmoPart.Y => new NVec3(1, ratio, 1), _ => new NVec3(1, 1, ratio) };
        }
        if (Ctx.Viewport.IsSnapHeld) scale = new NVec3(Snap(scale.X), Snap(scale.Y), Snap(scale.Z));
        ApplyScale(scale);
    }

    /// <summary>스케일 값을 증분 단위로 반올림한다(최소 한 단위, 0이 되지 않도록).</summary>
    private static float Snap(float v)
    {
        float step = MathF.Max(CubeApp.Instance.Settings.ScaleSnapStep, 0.001f); // J: 증분 스케일(Preferences, 기본 0.25)
        return MathF.Max(MathF.Round(v / step) * step, step);
    }
}
