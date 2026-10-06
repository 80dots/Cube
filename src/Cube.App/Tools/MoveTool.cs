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
    public override string HelpText => "Move Tool: drag the manipulator to move the selection. Hold X to snap to grid, V to snap to points. Click elsewhere to select.";

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
        delta = ApplySnap(delta, px, proj);
        ApplyTranslation(delta);
    }

    /// <summary>X: 그리드(1단위) 스냅, V: 커서 근처 정점으로 스냅. 축 드래그면 축 성분만 취한다.</summary>
    private NVec3 ApplySnap(NVec3 delta, NVec2 px, CameraProjection proj)
    {
        var vp = Ctx.Viewport;
        NVec3? target = null;
        if (vp.IsPointSnapHeld)
        {
            float best = 30f * CubeApp.Instance.UiScale; best *= best;
            var movingVerts = new HashSet<(Core.Scene.NodeId, int)>();
            foreach (var (id, verts, _, _, _) in ComponentTargets) foreach (int v in verts) movingVerts.Add((id, v));
            var movingNodes = new HashSet<Core.Scene.NodeId>(ObjectTargets.Select(t => t.node.Id));
            foreach (var t in Picker.Targets())
            {
                if (movingNodes.Contains(t.Id)) continue;
                var m = t.Mesh;
                for (int v = 0; v < m.VertexCount; v++)
                {
                    if (!m.Verts[v].Alive || movingVerts.Contains((t.Id, v))) continue;
                    var w = NVec3.Transform(m.Verts[v].Position, t.World);
                    var p = proj.Project(w, out _);
                    if (p == null) continue;
                    float d2 = NVec2.DistanceSquared(p.Value, px);
                    if (d2 < best) { best = d2; target = w; }
                }
            }
        }
        else if (vp.IsGridSnapHeld)
        {
            const float step = 1f;
            var np = PivotWorld + delta;
            target = new NVec3(MathF.Round(np.X / step) * step, MathF.Round(np.Y / step) * step, MathF.Round(np.Z / step) * step);
        }
        if (target == null) return delta;
        var snapped = target.Value - PivotWorld;
        if (_axisMode) { var axis = Gizmo.AxisOf(DragPart); return axis * NVec3.Dot(snapped, axis); }
        return snapped;
    }
}
