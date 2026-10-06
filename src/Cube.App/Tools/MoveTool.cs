using Cube.App.Viewport.Gizmos;
using Cube.Core.Geometry;
using Cube.Core.Picking;
using NVec2 = System.Numerics.Vector2;
using NVec3 = System.Numerics.Vector3;

namespace Cube.App.Tools;

/// <summary>Maya Move Tool (W). 축/평면/중앙(화면 평행) 드래그.</summary>
public sealed class MoveTool : TransformToolBase
{
    public override string Id => "move";
    public override string Label => "Move";
    public override string HelpText => "Move Tool: drag the manipulator to move the selection. Click elsewhere to select.";

    private NVec3 _planeNormal;
    private NVec3 _startHit;
    private float _startT;
    private bool _axisMode;
    private NVec2 _axisDir2D;

    protected override GizmoBase CreateGizmo() => new MoveGizmo();

    protected override void OnDragBegin(CameraProjection proj)
    {
        _axisMode = DragPart is GizmoPart.X or GizmoPart.Y or GizmoPart.Z;
        if (_axisMode)
        {
            var axis = Gizmo.AxisOf(DragPart);
            if (!DragMath.ClosestParamOnAxis(PivotWorld, axis, PressRay, out _startT))
            {
                // 축이 시선과 평행: 2D 투영 방향으로 폴백
                var p0 = proj.Project(PivotWorld, out _); var p1 = proj.Project(PivotWorld + axis * Gizmo.WorldUnit, out _);
                _axisDir2D = p0 != null && p1 != null && NVec2.Distance(p0.Value, p1.Value) > 1e-3f ? NVec2.Normalize(p1.Value - p0.Value) : NVec2.UnitX;
                _startT = float.NaN;
            }
        }
        else
        {
            _planeNormal = DragPart == GizmoPart.Center ? -proj.Forward : Gizmo.PlaneNormalOf(DragPart);
            if (!DragMath.RayPlane(PressRay, PivotWorld, _planeNormal, out _startHit)) _startHit = PivotWorld;
        }
    }

    protected override void UpdateDrag(NVec2 px, CameraProjection proj)
    {
        var ray = proj.Unproject(px);
        NVec3 delta;
        if (_axisMode)
        {
            var axis = Gizmo.AxisOf(DragPart);
            if (float.IsNaN(_startT))
            {
                float along = NVec2.Dot(px - PressPx, _axisDir2D);
                delta = axis * (along * proj.WorldPerPixel(NVec3.Dot(PivotWorld - proj.Eye, proj.Forward)));
            }
            else
            {
                if (!DragMath.ClosestParamOnAxis(PivotWorld, axis, ray, out float t)) return;
                delta = axis * (t - _startT);
            }
        }
        else
        {
            if (!DragMath.RayPlane(ray, PivotWorld, _planeNormal, out var hit)) return;
            delta = hit - _startHit;
        }
        ApplyTranslation(delta);
    }
}
