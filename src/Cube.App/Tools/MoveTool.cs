using Cube.App.Viewport.Gizmos;
using Cube.Core.Commands;
using Cube.Core.Geometry;
using Cube.Core.Mesh;
using Cube.Core.Scene;
using Cube.Core.Selection;
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

    // ---------------------------------------------------------------- Extrude 조작기
    // Maya: Extrude 직후 조작기의 파란(Z, 법선) 화살표를 끌면 면이 "두께"만큼 돌출된다. 여러 면이 서로 다른 방향을 향해도
    // 각 면이 자기 법선 방향으로 평행 이동한다(정점별 마이터 방향). 다른 핸들은 일반 이동. 선택이 바뀌면 끝난다.
    private string? _extrudeKey;
    private bool _offsetMode;
    private readonly Dictionary<NodeId, (NVec3[] dirLocal, float worldPerLocal)> _offsetDirs = new();
    private float _offset;

    /// <summary>ExtrudeSelection이 면 Extrude 직후 부른다. 현재 면 선택이 유지되는 동안 Z 드래그는 두께 오프셋.</summary>
    public void BeginExtrudeManip() => _extrudeKey = SelectionKey();

    private string SelectionKey()
    {
        var sel = Ctx.Sel;
        if (sel.Mode != SelectMode.Face) return "";
        return string.Join(";", sel.NodesWithComponents(SelectMode.Face).Select(id => id + ":" + string.Join(",", sel.GetComponents(id).Faces.OrderBy(f => f))));
    }

    public override void Deactivate() { _extrudeKey = null; base.Deactivate(); }

    /// <summary>점 스냅 후보에서 움직이는 정점/오브젝트를 뺄지(Edit Pivot은 자기 정점에도 붙는다).</summary>
    protected virtual bool ExcludeMovingFromPointSnap => true;
    /// <summary>스냅까지 끝난 월드 델타를 적용한다. Edit Pivot은 피벗만 옮긴다.</summary>
    protected virtual void ApplyMove(NVec3 worldDelta) => ApplyTranslation(worldDelta);
    /// <summary>점 스냅(Retain component spacing off): 모두 한 점으로.</summary>
    protected virtual void ApplyCollapse(NVec3 worldTarget) => ApplyCollapseTo(worldTarget);

    protected override void OnDragBegin(CameraProjection proj)
    {
        _offsetMode = false; _offset = 0f; _offsetDirs.Clear();
        if (_extrudeKey != null && _extrudeKey.Length > 0 && _extrudeKey == SelectionKey() && DragPart == GizmoPart.Z && ComponentTargets.Count > 0)
        {
            _offsetMode = true;
            var sel = Ctx.Sel;
            foreach (var (id, verts, _, world, _) in ComponentTargets)
            {
                var mesh = Ctx.Doc.Get(id).Mesh!;
                var dirs = MeshOps.RegionOffsetDirections(mesh, sel.GetComponents(id).Faces);
                var arr = new NVec3[verts.Length];
                for (int i = 0; i < verts.Length; i++) arr[i] = dirs.TryGetValue(verts[i], out var d) ? d : NVec3.Zero;
                // 월드 거리 → 로컬 거리(균일 스케일 가정; 기즈모 축 방향의 스케일)
                var localAxis = NVec3.TransformNormal(Gizmo.AxisZ, Matrix4x4Inverse(world));
                float worldPerLocal = localAxis.LengthSquared() > 1e-12f ? 1f / localAxis.Length() : 1f;
                _offsetDirs[id] = (arr, worldPerLocal);
            }
        }
        else if (_extrudeKey != null && _extrudeKey != SelectionKey()) _extrudeKey = null;
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
        if (_offsetMode)
        {
            float t = NVec3.Dot(delta, Gizmo.AxisZ);
            if (Ctx.Viewport.IsGridSnapHeld) { float step = MathF.Max(CubeApp.Instance.Settings.GridSpacingCm, 1f) / 100f; t = MathF.Round(t / step) * step; }
            ApplyOffset(t);
            return;
        }
        delta = ApplySnap(delta, px, proj, out var collapse);
        if (collapse != null) ApplyCollapse(collapse.Value);
        else ApplyMove(delta);
    }

    private static System.Numerics.Matrix4x4 Matrix4x4Inverse(System.Numerics.Matrix4x4 m) { System.Numerics.Matrix4x4.Invert(m, out var inv); return inv; }

    private void ApplyOffset(float t)
    {
        _offset = t;
        foreach (var (id, verts, init, _, _) in ComponentTargets)
        {
            if (!_offsetDirs.TryGetValue(id, out var od)) continue;
            float local = t / od.worldPerLocal;
            var pos = new NVec3[verts.Length];
            for (int i = 0; i < verts.Length; i++) pos[i] = init[i] + od.dirLocal[i] * local;
            MoveVerticesCommand.Preview(Ctx.Doc, id, verts, pos);
        }
        Gizmo.Pivot = PivotWorld + Gizmo.AxisZ * t;
        Gizmo.MarkDirty();
        Ctx.SetHelp?.Invoke($"Extrude thickness: {t:0.###}  (Shift = fine, X = grid step)");
    }

    protected override MoveVerticesCommand? MakeComponentCommand(NodeId id, int[] verts, NVec3[] init, NVec3[] after)
    {
        if (!_offsetMode || !_offsetDirs.TryGetValue(id, out var od)) return null;
        var ids = verts; var dirs = od.dirLocal; float wpl = od.worldPerLocal;
        var p = new HistoryParams(HistoryParam.F("Thickness", _offset, -1000f, 1000f, 0.01f));
        return new MoveVerticesCommand("Extrude Thickness", id, verts, init, after, null, p, (m, prm) =>
        {
            float local = prm.Float("Thickness") / wpl;
            for (int i = 0; i < ids.Length; i++)
            {
                if (ids[i] >= m.VertexCount || !m.Verts[ids[i]].Alive) continue;
                var v = m.Verts[ids[i]]; v.Position += dirs[i] * local; m.Verts[ids[i]] = v;
            }
            return true;
        });
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
