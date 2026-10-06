using System.Numerics;
using Cube.App.Viewport.Gizmos;
using Cube.Core.Geometry;
using Cube.Core.Picking;
using NVec2 = System.Numerics.Vector2;
using NVec3 = System.Numerics.Vector3;

namespace Cube.App.Tools;

/// <summary>Maya Rotate Tool (E). 링 드래그 → 피벗 기준 축 회전. 외곽 링은 화면 축 회전.</summary>
public sealed class RotateTool : TransformToolBase
{
    public override string Id => "rotate";
    public override string Label => "Rotate";
    public override string HelpText => "Rotate Tool: drag a ring to rotate the selection about that axis.";

    private NVec3 _axis;
    private NVec2 _centerPx;
    private float _sign;

    protected override GizmoBase CreateGizmo() => new RotateGizmo();

    protected override void OnDragBegin(CameraProjection proj)
    {
        _axis = DragPart == GizmoPart.Screen ? -proj.Forward : Gizmo.AxisOf(DragPart);
        _centerPx = proj.Project(PivotWorld, out _) ?? PressPx;
        // 화면 y가 아래로 커지므로, 축이 카메라를 향하면 화면 반시계가 음의 각으로 나온다 → 부호 보정
        _sign = NVec3.Dot(_axis, proj.Eye - PivotWorld) >= 0 || proj.IsOrtho && NVec3.Dot(_axis, -proj.Forward) >= 0 ? -1f : 1f;
    }

    protected override void UpdateDrag(NVec2 px, CameraProjection proj)
    {
        float angle = DragMath.ScreenAngle(_centerPx, PressPx, px) * _sign;
        if (Ctx.Viewport.IsSnapHeld) angle = MathF.Round(angle / (MathF.PI / 12f)) * (MathF.PI / 12f); // J: 15° 스냅
        var m = Matrix4x4.CreateTranslation(-PivotWorld) * Matrix4x4.CreateFromAxisAngle(_axis, angle) * Matrix4x4.CreateTranslation(PivotWorld);
        ApplyWorldMatrixAboutPivot(m);
    }
}
