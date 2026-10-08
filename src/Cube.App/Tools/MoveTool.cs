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
/// <remarks>
/// 축 핸들(X/Y/Z) = 시작 레이와 축의 최근접 매개변수 차이만큼 축 방향 이동(축이 시선과 평행하면 화면 투영 방향 픽셀 거리로 폴백).
/// 평면 핸들 = 그 평면과 레이 교점의 차이, 중앙 = 카메라를 향한 평면(화면 평행) 위 이동.
/// 스냅(X 그리드 / V 점)과 면 Extrude 직후의 두께 드래그 모드를 지원한다. EditPivotTool이 이 클래스를 상속해 피벗만 옮긴다.
/// </remarks>
public class MoveTool : TransformToolBase
{
    /// <summary>툴 ID("move", W).</summary>
    public override string Id => "move";
    /// <summary>표시 이름. 커밋 명령 이름으로도 쓰인다.</summary>
    public override string Label => "Move";
    /// <summary>헬프 라인 안내.</summary>
    public override string HelpText => "Move Tool: drag the manipulator to move the selection. Hold X to snap to grid, V to snap to points. Click elsewhere to select.";

    /// <summary>평면/중앙 드래그에서 사용하는 이동 평면의 법선(월드).</summary>
    private NVec3 _planeNormal;
    /// <summary>평면 드래그 시작 시 레이-평면 교점(월드). 현재 교점과의 차가 이동 델타.</summary>
    private NVec3 _startHit;
    /// <summary>축 드래그 시작 시 축 위 최근접 매개변수. NaN이면 2D 폴백 모드.</summary>
    private float _startT;
    /// <summary>축 핸들 드래그인지(아니면 평면/중앙).</summary>
    private bool _axisMode;
    /// <summary>축이 시선과 평행할 때 쓰는 화면상 축 방향(단위 2D 벡터).</summary>
    private NVec2 _axisDir2D;

    /// <summary>이동 조작기(화살표 3개 + 평면 사각형 + 중앙)를 만든다.</summary>
    protected override GizmoBase CreateGizmo() => new MoveGizmo();

    // ---------------------------------------------------------------- Extrude 조작기
    // Maya: Extrude 직후 조작기의 파란(Z, 법선) 화살표를 끌면 면이 "두께"만큼 돌출된다. 여러 면이 서로 다른 방향을 향해도
    // 각 면이 자기 법선 방향으로 평행 이동한다(정점별 마이터 방향). 다른 핸들은 일반 이동. 선택이 바뀌면 끝난다.
    /// <summary>Extrude 조작기가 유효한 면 선택의 키(<see cref="SelectionKey"/>). null이면 일반 이동만.</summary>
    private string? _extrudeKey;
    /// <summary>이번 드래그가 두께 오프셋 모드인지(Extrude 직후 + 같은 면 선택 + Z 핸들).</summary>
    private bool _offsetMode;
    /// <summary>노드별 정점 오프셋 방향(로컬, 마이터 보정 포함)과 월드/로컬 길이 비율.</summary>
    private readonly Dictionary<NodeId, (NVec3[] dirLocal, float worldPerLocal)> _offsetDirs = new();
    /// <summary>현재 두께(월드 m). 커밋 시 'Extrude Thickness' 히스토리 파라미터가 된다.</summary>
    private float _offset;

    /// <summary>ExtrudeSelection이 면 Extrude 직후 부른다. 현재 면 선택이 유지되는 동안 Z 드래그는 두께 오프셋.</summary>
    public void BeginExtrudeManip() => _extrudeKey = SelectionKey();

    /// <summary>현재 면 선택을 "노드:면,면;노드:..." 문자열로 만든다(면 모드가 아니면 빈 문자열). 선택 유지 여부 비교용.</summary>
    private string SelectionKey()
    {
        var sel = Ctx.Sel;
        if (sel.Mode != SelectMode.Face) return "";
        return string.Join(";", sel.NodesWithComponents(SelectMode.Face).Select(id => id + ":" + string.Join(",", sel.GetComponents(id).Faces.OrderBy(f => f))));
    }

    /// <summary>툴이 꺼지면 Extrude 조작기 모드도 끝낸다.</summary>
    public override void Deactivate() { _extrudeKey = null; base.Deactivate(); }

    /// <summary>점 스냅 후보에서 움직이는 정점/오브젝트를 뺄지(Edit Pivot은 자기 정점에도 붙는다).</summary>
    protected virtual bool ExcludeMovingFromPointSnap => true;
    /// <summary>스냅까지 끝난 월드 델타를 적용한다. Edit Pivot은 피벗만 옮긴다.</summary>
    protected virtual void ApplyMove(NVec3 worldDelta) => ApplyTranslation(worldDelta);
    /// <summary>점 스냅(Retain component spacing off): 모두 한 점으로.</summary>
    protected virtual void ApplyCollapse(NVec3 worldTarget) => ApplyCollapseTo(worldTarget);

    /// <summary>
    /// 드래그 시작 계산. Extrude 모드 조건이면 정점별 오프셋 방향(<see cref="MeshOps.RegionOffsetDirections"/>)을 준비하고,
    /// 선택이 바뀌었으면 Extrude 모드를 끝낸다. 이어서 축/평면 드래그의 시작 매개변수를 구한다.
    /// </summary>
    protected override void OnDragBegin(CameraProjection proj)
    {
        // 두께 모드 판정: Extrude 직후 같은 면 선택에서 Z(법선) 화살표를 잡았을 때만
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
        // 축 핸들이면 축 위 시작 매개변수, 아니면 이동 평면과 시작 교점
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

    /// <summary>
    /// 드래그 갱신: 포인터 레이로 월드 델타를 구한 뒤, 두께 모드면 Z 성분만 두께로(X 홀드 시 그리드 단위 반올림),
    /// 아니면 스냅을 적용해 이동 또는 한 점으로 모으기를 프리뷰한다.
    /// </summary>
    protected override void UpdateDrag(NVec2 px, CameraProjection proj)
    {
        var ray = proj.Unproject(px);
        NVec3 delta;
        if (_axisMode)
        {
            var axis = Gizmo.AxisOf(DragPart);
            // 2D 폴백: 화면에서 축 방향으로 간 픽셀 수 × 피벗 깊이에서의 픽셀당 월드 길이
            if (float.IsNaN(_startT))
            {
                float along = NVec2.Dot(px - PressPx, _axisDir2D);
                delta = axis * (along * proj.WorldPerPixel(NVec3.Dot(PivotWorld - proj.Eye, proj.Forward)));
            }
            else
            {
                // 일반: 현재 레이의 축 최근접 매개변수 차이
                if (!DragMath.ClosestParamOnAxis(PivotWorld, axis, ray, out float t)) return;
                delta = axis * (t - _startT);
            }
        }
        else
        {
            // 평면/중앙: 레이-평면 교점 차이
            if (!DragMath.RayPlane(ray, PivotWorld, _planeNormal, out var hit)) return;
            delta = hit - _startHit;
        }
        // 두께 모드: 조작기 Z 축 방향 성분만 두께로 사용
        if (_offsetMode)
        {
            float t = NVec3.Dot(delta, Gizmo.AxisZ);
            if (Ctx.Viewport.IsGridSnapHeld) { float step = MathF.Max(CubeApp.Instance.Settings.GridSpacingCm, 1f) / 100f; t = MathF.Round(t / step) * step; }
            ApplyOffset(t);
            return;
        }
        // 일반 이동: 스냅 적용 후 이동(또는 점 스냅 collapse)
        delta = ApplySnap(delta, px, proj, out var collapse);
        if (collapse != null) ApplyCollapse(collapse.Value);
        else ApplyMove(delta);
    }

    /// <summary>행렬 역행렬 헬퍼(실패 시 Invert가 남긴 값 그대로).</summary>
    private static System.Numerics.Matrix4x4 Matrix4x4Inverse(System.Numerics.Matrix4x4 m) { System.Numerics.Matrix4x4.Invert(m, out var inv); return inv; }

    /// <summary>
    /// 두께 t(월드)를 프리뷰한다: 정점마다 시작 위치 + 로컬 오프셋 방향 × (t / worldPerLocal). 조작기도 Z 방향으로 따라 옮기고 헬프 라인에 값을 표시.
    /// </summary>
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

    /// <summary>
    /// 두께 모드 커밋 명령: 'Extrude Thickness' 히스토리 항목(Thickness 파라미터)을 가진 MoveVerticesCommand.
    /// 재실행(replay)은 같은 정점 ID에 저장된 방향 × 두께를 더한다(ID가 슬롯 인덱스라 이력 재생 후에도 유효). 일반 이동이면 null.
    /// </summary>
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
                // 재생 시 삭제되었거나 범위를 벗어난 정점은 건너뜀
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
        /// <returns>스냅이 적용된 월드 델타(스냅 대상이 없으면 원래 델타).</returns>
        collapse = null;
        var vp = Ctx.Viewport;
        var settings = CubeApp.Instance.Settings;
        NVec3? target = null;
        if (vp.IsPointSnapHeld)
        {
            // 점 스냅: 화면 30px(UI 배율) 이내에서 가장 가까운 점 탐색. 움직이는 정점/오브젝트 자신은 후보에서 제외
            float best = 30f * CubeApp.Instance.UiScale; best *= best;
            bool exclude = ExcludeMovingFromPointSnap;
            var movingVerts = new HashSet<(Core.Scene.NodeId, int)>();
            if (exclude) foreach (var (id, verts, _, _, _) in ComponentTargets) foreach (int v in verts) movingVerts.Add((id, v));
            var movingNodes = new HashSet<Core.Scene.NodeId>(exclude ? ObjectTargets.Select(t => t.node.Id) : Enumerable.Empty<Core.Scene.NodeId>());
            // 후보 점을 화면으로 투영해 커서와의 거리 제곱이 최소면 채택
            void Consider(NVec3 w)
            {
                var p = proj.Project(w, out _);
                if (p == null) return;
                float d2 = NVec2.DistanceSquared(p.Value, px);
                if (d2 < best) { best = d2; target = w; }
            }
            // 후보 1: 피킹 대상 메시의 모든 살아 있는 정점(월드)
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
            // 후보 2: 조인트와 라이트의 월드 위치
            foreach (var n in Ctx.Doc.Nodes.Values)
                if ((n.IsJoint || n.Light != null) && !movingNodes.Contains(n.Id)) Consider(n.WorldMatrix.Translation);
            // Retain Component Spacing off(평면 드래그): 모두 그 점으로 모으기
            if (target != null && !settings.RetainComponentSpacing && !_axisMode) { collapse = target; return delta; }
        }
        else if (vp.IsGridSnapHeld)
        {
            // 그리드 스냅: 단위는 Preferences Grid Spacing(cm → m), 이동 후 피벗 위치를 격자에 반올림
            float step = MathF.Max(settings.GridSpacingCm, 1f) / 100f;
            var np = PivotWorld + delta;
            target = new NVec3(MathF.Round(np.X / step) * step, MathF.Round(np.Y / step) * step, MathF.Round(np.Z / step) * step);
        }
        // 목표점까지의 델타. 축 드래그면 축 성분만 남긴다
        if (target == null) return delta;
        var snapped = target.Value - PivotWorld;
        if (_axisMode) { var axis = Gizmo.AxisOf(DragPart); return axis * NVec3.Dot(snapped, axis); }
        return snapped;
    }
}
