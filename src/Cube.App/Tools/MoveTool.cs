using Cube.App.Viewport.Gizmos;
using Cube.Core.Geometry;
using Cube.Core.Picking;
using NVec2 = System.Numerics.Vector2;
using NVec3 = System.Numerics.Vector3;

namespace Cube.App.Tools;

/// <summary>Maya Move Tool (W). 축/평면/중앙(화면 평행) 드래그.</summary>
public class MoveTool : TransformToolBase
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

    /// <summary>점 스냅 후보에서 움직이는 정점/오브젝트를 뺄지(Edit Pivot은 자기 정점에도 붙는다).</summary>
    protected virtual bool ExcludeMovingFromPointSnap => true;
    /// <summary>스냅까지 끝난 월드 델타를 적용한다. Edit Pivot은 피벗만 옮긴다.</summary>
    protected virtual void ApplyMove(NVec3 worldDelta) => ApplyTranslation(worldDelta);
    /// <summary>점 스냅(Retain component spacing off): 모두 한 점으로.</summary>
    protected virtual void ApplyCollapse(NVec3 worldTarget) => ApplyCollapseTo(worldTarget);

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
        delta = ApplySnap(delta, px, proj, out var collapse);
        if (collapse != null) ApplyCollapse(collapse.Value);
        else ApplyMove(delta);
    }

    /// <summary>
    /// Maya 스냅: V/Snap to Points = 커서 근처 점(정점, 조인트·라이트 위치)으로, X/Snap to Grid = 그리드 간격(Preferences) 단위로.
    /// 축 드래그면 축 성분만 취한다. 점 스냅에서 Retain Component Spacing이 꺼져 있으면 collapse 대상(모든 선택을 그 점으로)을 돌려준다.
    /// </summary>
    private NVec3 ApplySnap(NVec3 delta, NVec2 px, CameraProjection proj, out NVec3? collapse)
    {
        collapse = null;
        var vp = Ctx.Viewport;
        var settings = CubeApp.Instance.Settings;
        NVec3? target = null;
        if (vp.IsPointSnapHeld)
        {
            float best = 30f * CubeApp.Instance.UiScale; best *= best;
            bool exclude = ExcludeMovingFromPointSnap;
            var movingVerts = new HashSet<(Core.Scene.NodeId, int)>();
            if (exclude) foreach (var (id, verts, _, _, _) in ComponentTargets) foreach (int v in verts) movingVerts.Add((id, v));
            var movingNodes = new HashSet<Core.Scene.NodeId>(exclude ? ObjectTargets.Select(t => t.node.Id) : Enumerable.Empty<Core.Scene.NodeId>());
            void Consider(NVec3 w)
            {
                var p = proj.Project(w, out _);
                if (p == null) return;
                float d2 = NVec2.DistanceSquared(p.Value, px);
                if (d2 < best) { best = d2; target = w; }
            }
            foreach (var t in Picker.Targets())
            {
                if (movingNodes.Contains(t.Id)) continue;
                var m = t.Mesh;
                for (int v = 0; v < m.VertexCount; v++)
                {
                    if (!m.Verts[v].Alive || movingVerts.Contains((t.Id, v))) continue;
                    Consider(NVec3.Transform(m.Verts[v].Position, t.World));
                }
            }
            foreach (var n in Ctx.Doc.Nodes.Values)
                if ((n.IsJoint || n.Light != null) && !movingNodes.Contains(n.Id)) Consider(n.WorldMatrix.Translation);
            if (target != null && !settings.RetainComponentSpacing && !_axisMode) { collapse = target; return delta; }
        }
        else if (vp.IsGridSnapHeld)
        {
            float step = MathF.Max(settings.GridSpacingCm, 1f) / 100f;
            var np = PivotWorld + delta;
            target = new NVec3(MathF.Round(np.X / step) * step, MathF.Round(np.Y / step) * step, MathF.Round(np.Z / step) * step);
        }
        if (target == null) return delta;
        var snapped = target.Value - PivotWorld;
        if (_axisMode) { var axis = Gizmo.AxisOf(DragPart); return axis * NVec3.Dot(snapped, axis); }
        return snapped;
    }
}
