using System.Numerics;
using Cube.App.Viewport.Gizmos;
using Cube.Core.Geometry;
using Cube.Core.Picking;
using NVec2 = System.Numerics.Vector2;
using NVec3 = System.Numerics.Vector3;

namespace Cube.App.Tools;

/// <summary>Maya Rotate Tool (E). 링 드래그 → 피벗 기준 축 회전. 외곽 링은 화면 축 회전.</summary>
/// <remarks>
/// 각도는 화면에서 피벗 투영점 기준으로 누른 점 → 현재 점이 이루는 각(<see cref="DragMath.ScreenAngle"/>)이다.
/// 축이 카메라를 향하는지에 따라 부호를 보정해 화면에서 돌린 방향과 3D 회전 방향이 일치하게 한다.
/// J 홀드(또는 스냅 토글)면 Preferences의 각도 단위로 반올림한다. 실제 적용은 <see cref="TransformToolBase.ApplyRotation"/>.
/// </remarks>
public sealed class RotateTool : TransformToolBase
{
    /// <summary>툴 ID("rotate", E).</summary>
    public override string Id => "rotate";
    /// <summary>표시 이름(커밋 명령 이름).</summary>
    public override string Label => "Rotate";
    /// <summary>헬프 라인 안내.</summary>
    public override string HelpText => "Rotate Tool: drag a ring to rotate the selection about that axis.";

    /// <summary>회전 축(월드 단위 벡터). 외곽(Screen) 링이면 카메라 시선 반대 방향.</summary>
    private NVec3 _axis;
    /// <summary>피벗의 화면 투영 위치(뷰포트 로컬 픽셀). 화면 각도의 중심.</summary>
    private NVec2 _centerPx;
    /// <summary>화면 각 → 3D 회전 각 부호(+1/-1).</summary>
    private float _sign;

    /// <summary>회전 조작기(축 링 3개 + 화면 링)를 만든다.</summary>
    protected override GizmoBase CreateGizmo() => new RotateGizmo();

    /// <summary>드래그 시작: 회전 축, 화면 중심, 각도 부호를 정한다.</summary>
    protected override void OnDragBegin(CameraProjection proj)
    {
        _axis = DragPart == GizmoPart.Screen ? -proj.Forward : Gizmo.AxisOf(DragPart);
        _centerPx = proj.Project(PivotWorld, out _) ?? PressPx;
        // 화면 y가 아래로 커지므로, 축이 카메라를 향하면 화면 반시계가 음의 각으로 나온다 → 부호 보정
        _sign = NVec3.Dot(_axis, proj.Eye - PivotWorld) >= 0 || proj.IsOrtho && NVec3.Dot(_axis, -proj.Forward) >= 0 ? -1f : 1f;
    }

    /// <summary>드래그 갱신: 누른 점 대비 현재 점의 화면 각도(라디안)를 구해 스냅 후 피벗 기준 회전을 프리뷰한다.</summary>
    protected override void UpdateDrag(NVec2 px, CameraProjection proj)
    {
        float angle = DragMath.ScreenAngle(_centerPx, PressPx, px) * _sign;
        if (Ctx.Viewport.IsSnapHeld)
        {
            float step = MathF.Max(CubeApp.Instance.Settings.RotateSnapDegrees, 0.1f) * MathF.PI / 180f; // J: 증분 회전(Preferences, 기본 15°)
            angle = MathF.Round(angle / step) * step;
        }
        ApplyRotation(_axis, angle);
    }
}
