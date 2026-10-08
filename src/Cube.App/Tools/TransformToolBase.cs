using System.Numerics;
using Cube.App.Viewport;
using Cube.App.Viewport.Gizmos;
using Cube.Core.Commands;
using Cube.Core.Geometry;
using Cube.Core.Picking;
using Cube.Core.Scene;
using Cube.Core.Selection;
using Godot;
using NVec2 = System.Numerics.Vector2;
using NVec3 = System.Numerics.Vector3;
using NQuat = System.Numerics.Quaternion;

namespace Cube.App.Tools;

/// <summary>
/// 조작기를 가진 변형 툴의 공통 기반. 선택이 있으면 피벗에 조작기를 띄우고, 조작기 밖 클릭은 선택으로 처리한다.
/// 드래그 시작 시 초기값을 캡처하고, 드래그 중 문서를 직접 갱신(프리뷰)하며, 놓을 때 하나의 명령으로 커밋한다.
/// 오브젝트는 TRS 속성을 직접 갱신한다(행렬 분해를 쓰지 않으므로 비균등 스케일+회전에서도 안전).
/// </summary>
/// <remarks>
/// 흐름: 왼쪽 버튼 누름(<see cref="OnPrimaryPress"/>) → 조작기 핸들 히트면 <see cref="BeginDrag"/>(대상과 초기값 캡처)
/// → 마우스 이동마다 파생 툴의 <see cref="UpdateDrag"/>가 Apply* 헬퍼로 프리뷰 → 버튼 뗌이면 <see cref="EndDrag"/>(commit: true)로
/// TransformNodesCommand/MoveVerticesCommand를 alreadyApplied로 푸시, Esc/취소면 초기값으로 복원한다.
/// 조작기 밖 클릭·마키·호버 프리셀렉션은 기반 <see cref="SelectTool"/>이 처리한다.
/// 좌표: 화면 좌표는 뷰포트 로컬 픽셀(NVec2), 3D는 월드 공간(m)이며 행벡터 행렬 규약(v * M)을 쓴다.
/// </remarks>
public abstract class TransformToolBase : SelectTool
{
    /// <summary>이 툴의 조작기 노드(Move = 화살표, Rotate = 링, Scale = 상자). 처음 활성화될 때 <see cref="CreateGizmo"/>로 만든다.</summary>
    protected GizmoBase Gizmo = null!;
    /// <summary>외부(DebugDriver의 gizmo/axisdrag 등)에서 조작기 상태를 읽기 위한 공개 접근자.</summary>
    public GizmoBase? GizmoPublic => Gizmo;
    /// <summary>조작기 핸들을 잡고 드래그 중인지.</summary>
    protected bool Dragging { get; private set; }
    /// <summary>드래그 중인 조작기 부분(축/평면/중앙 등).</summary>
    protected GizmoPart DragPart { get; private set; }
    /// <summary>드래그를 시작한 화면 위치(뷰포트 로컬 픽셀).</summary>
    protected NVec2 PressPx;
    /// <summary>드래그 시작 위치의 카메라 레이(월드). 축 투영 등 드래그 수학의 기준.</summary>
    protected Ray PressRay;
    /// <summary>Shift 정밀 조정: 실제 커서 대신 느리게 따라오는 가상 포인터를 드래그 수학에 넘긴다.</summary>
    /// <remarks>_virtualPx = 드래그 수학에 넘기는 가상 포인터, _lastPx = 직전 실제 커서 위치(이동 델타 계산용).</remarks>
    private NVec2 _virtualPx, _lastPx;
    /// <summary>Shift 정밀 조정 시 커서 이동 대비 가상 포인터 이동 비율(1/10).</summary>
    public const float PrecisionFactor = 0.1f;

    /// <summary>
    /// 오브젝트 모드 드래그 대상: (노드, 드래그 시작 시 Local, 부모 월드 행렬, 그 역행렬).
    /// 선택된 조상이 있는 노드는 제외한다(부모와 함께 움직이므로 이중 적용 방지).
    /// </summary>
    protected readonly List<(SceneNode node, Transform3 initial, Matrix4x4 parentWorld, Matrix4x4 parentInv)> ObjectTargets = new();
    /// <summary>
    /// 컴포넌트 모드 드래그 대상: (노드 ID, 움직일 정점 ID 배열, 시작 시 로컬 위치, 메시 월드 행렬, 그 역행렬).
    /// 엣지/면 선택은 구성 정점으로 펼쳐서 담는다.
    /// </summary>
    protected readonly List<(NodeId node, int[] verts, NVec3[] initial, Matrix4x4 world, Matrix4x4 worldInv)> ComponentTargets = new();
    /// <summary>드래그 시작 시 조작기 피벗(월드). 회전/스케일의 중심.</summary>
    protected NVec3 PivotWorld;
    /// <summary>마지막으로 적용한 컴포넌트 변형(히스토리 기록용): 종류/피벗/축/기저와 파라미터.</summary>
    private ComponentTransformOp? _lastOp;
    /// <summary>마지막 적용 변형의 파라미터(Translate/Angle/Scale). 히스토리 항목 파라미터로 복제되어 들어간다.</summary>
    private HistoryParams? _lastParams;
    /// <summary>Ctrl 드래그: 선택 조인트만 움직이고 자식의 월드 트랜스폼은 유지(자식 로컬 보정).</summary>
    private bool _isolate;
    /// <summary>Ctrl 드래그 시 보정할 자식 조인트 목록: (자식, 시작 Local, 시작 월드 행렬). 이 월드를 유지하도록 로컬을 다시 푼다.</summary>
    private readonly List<(SceneNode child, Transform3 initial, Matrix4x4 world)> _childComp = new();

    /// <summary>파생 툴이 자기 조작기 노드를 만든다(한 번만 호출).</summary>
    protected abstract GizmoBase CreateGizmo();

    /// <summary>
    /// 툴 활성화: 조작기를 만들고(최초 1회) 활성 패널에 붙인 뒤, 선택/문서/축 방향 변경을 구독하고 조작기를 배치한다.
    /// </summary>
    public override void Activate(ToolContext ctx)
    {
        base.Activate(ctx);
        if (Gizmo == null) { Gizmo = CreateGizmo(); }
        AttachGizmo(ctx.Viewport);
        ctx.Doc.Selection.Changed += OnSelectionChanged;
        ctx.Doc.Changed += OnDocChanged;
        ctx.AxisOrientationChanged += OnAxisChanged;
        RefreshGizmo();
    }

    /// <summary>구독을 해제하고 조작기를 숨긴다.</summary>
    public override void Deactivate()
    {
        Ctx.Doc.Selection.Changed -= OnSelectionChanged;
        Ctx.Doc.Changed -= OnDocChanged;
        Ctx.AxisOrientationChanged -= OnAxisChanged;
        Gizmo.Visible = false;
        base.Deactivate();
    }

    /// <summary>
    /// 조작기 노드를 주어진 패널의 GizmoRoot 아래로 옮긴다. 이미 그 패널에 있으면 아무것도 하지 않는다.
    /// 다른 패널에서 떼어 내 새 패널로 Setup(카메라 등 참조 갱신) 후 추가한다.
    /// </summary>
    private void AttachGizmo(ViewportPanel panel)
    {
        if (Gizmo.GetParent() == panel.GizmoRoot) return;
        if (Gizmo.GetParent() != null) Gizmo.GetParent().RemoveChild(Gizmo);
        else Gizmo.Setup(panel);
        Gizmo.Setup(panel);
        panel.GizmoRoot.AddChild(Gizmo);
    }

    /// <summary>활성 패널이 바뀌면 진행 중 드래그를 커밋하고 조작기를 새 패널로 옮긴다.</summary>
    protected override void OnViewportChanged(ViewportPanel panel)
    {
        base.OnViewportChanged(panel);
        if (Dragging) EndDrag(commit: true);
        AttachGizmo(panel);
        RefreshGizmo();
    }

    /// <summary>선택이 바뀌면(드래그 중이 아닐 때) 조작기를 다시 배치한다.</summary>
    private void OnSelectionChanged() { if (!Dragging) RefreshGizmo(); }
    /// <summary>축 방향 기준이 바뀌면 조작기 축을 다시 계산한다.</summary>
    private void OnAxisChanged(AxisOrientation _) { if (!Dragging) RefreshGizmo(); }
    /// <summary>트랜스폼·메시·노드 삭제·리셋 등 피벗 위치가 바뀔 수 있는 문서 변경에 조작기를 다시 배치한다(드래그 중 제외: 프리뷰가 스스로 갱신).</summary>
    private void OnDocChanged(DocChange c)
    {
        if (!Dragging && c.Kind is ChangeKind.TransformChanged or ChangeKind.MeshGeometry or ChangeKind.MeshTopology or ChangeKind.NodeRemoved or ChangeKind.Reset) RefreshGizmo();
    }

    /// <summary>선택 피벗과 축 방향으로 조작기를 배치한다.</summary>
    protected void RefreshGizmo()
    {
        // 피벗이 없으면(선택 없음) 숨김. 있으면 위치·축을 설정하고 호버/드로잉 상태를 초기화
        if (!TryComputePivot(out var pivot)) { Gizmo.Visible = false; return; }
        Gizmo.Pivot = pivot;
        var (x, y, z) = ComputeAxes(pivot);
        Gizmo.AxisX = x; Gizmo.AxisY = y; Gizmo.AxisZ = z;
        Gizmo.Visible = true;
        Gizmo.SetHover(GizmoPart.None);
        Gizmo.MarkDirty();
        Gizmo.ForceUpdate();
    }

    /// <summary>
    /// 조작기 피벗(월드)을 계산한다. 오브젝트 모드 = 활성 오브젝트의 PivotWorld, 컴포넌트 모드 = 선택 컴포넌트가 가리키는 정점들의 월드 평균.
    /// </summary>
    /// <param name="pivot">계산된 월드 피벗.</param>
    /// <returns>선택이 없어 피벗을 정할 수 없으면 false.</returns>
    protected bool TryComputePivot(out NVec3 pivot)
    {
        var sel = Ctx.Sel; var doc = Ctx.Doc;
        pivot = default;
        if (sel.Mode == SelectMode.Object)
        {
            var active = doc.Find(sel.ActiveObject);
            if (active == null) return false;
            pivot = active.PivotWorld;
            return true;
        }
        // 컴포넌트 모드: 선택 컴포넌트를 정점으로 펼쳐 월드 좌표 평균(같은 정점은 노드당 한 번)
        var sum = NVec3.Zero; int n = 0;
        foreach (var id in sel.NodesWithComponents(sel.Mode))
        {
            var node = doc.Find(id); if (node?.Mesh == null) continue;
            var verts = SelectedVertices(node.Mesh, sel.GetComponents(id), sel.Mode);
            var w = node.WorldMatrix;
            foreach (int v in verts) { sum += NVec3.Transform(node.Mesh.Verts[v].Position, w); n++; }
        }
        if (n == 0) return false;
        pivot = sum / n;
        return true;
    }

    /// <summary>
    /// 조작기 축(X, Y, Z 월드 단위 벡터)을 계산한다.
    /// Object = 대상 노드 월드 행렬의 직교 정규화 축, Normal(컴포넌트) = 선택에 닿는 면 법선 합으로 만든 기저(Z = 법선),
    /// Normal(오브젝트 모드)은 Object와 같다. 계산 실패 시 월드 축.
    /// </summary>
    protected (NVec3 x, NVec3 y, NVec3 z) ComputeAxes(NVec3 pivot)
    {
        var sel = Ctx.Sel; var doc = Ctx.Doc;
        switch (Ctx.AxisOrientation)
        {
            case AxisOrientation.Object:
            case AxisOrientation.Normal when sel.Mode == SelectMode.Object:
                {
                    var node = doc.Find(sel.Mode == SelectMode.Object ? sel.ActiveObject : sel.NodesWithComponents(sel.Mode).FirstOrDefault());
                    if (node != null) return DragMath.OrthonormalAxes(node.WorldMatrix);
                    break;
                }
            case AxisOrientation.Normal:
                {
                    // 정점/엣지 선택은 그것에 닿는 면으로 바꿔 면 월드 법선을 모두 더한다
                    var normal = NVec3.Zero;
                    foreach (var id in sel.NodesWithComponents(sel.Mode))
                    {
                        var node = doc.Find(id); if (node?.Mesh == null) continue;
                        var m = node.Mesh; var w = node.WorldMatrix;
                        var faces = new HashSet<int>(sel.GetComponents(id).Faces);
                        var tmp = new List<int>();
                        if (sel.Mode == SelectMode.Vertex) foreach (int v in sel.GetComponents(id).Verts) { m.GetVertexFaces(v, tmp); faces.UnionWith(tmp); }
                        if (sel.Mode == SelectMode.Edge) foreach (int e in sel.GetComponents(id).Edges) { var (f0, f1) = m.EdgeFaces(e); if (f0 >= 0) faces.Add(f0); if (f1 >= 0) faces.Add(f1); }
                        foreach (int f in faces) normal += FaceNormalOrNeighbors(m, f, w);
                    }
                    // 법선 합이 0이면(서로 상쇄) 노드 축으로 대체
                    if (normal.LengthSquared() > 1e-8f) return DragMath.BasisFromNormal(normal);
                    var node2 = doc.Find(sel.NodesWithComponents(sel.Mode).FirstOrDefault());
                    if (node2 != null) return DragMath.OrthonormalAxes(node2.WorldMatrix);
                    break;
                }
        }
        return (NVec3.UnitX, NVec3.UnitY, NVec3.UnitZ);
    }

    /// <summary>면의 월드 법선. 면적이 0인 퇴화 면(엣지 Extrude 직후의 쿼드)은 트윈 면들의 법선 합으로 대신한다.</summary>
    private static NVec3 FaceNormalOrNeighbors(Core.Mesh.PolyMesh m, int f, Matrix4x4 w)
    {
        // 정상 면은 저장된 면 법선을 월드로 변환해 그대로 사용
        if (Core.Mesh.MeshNormals.FaceNormalUnnormalized(m, f).LengthSquared() > 1e-14f)
            return NVec3.Normalize(NVec3.TransformNormal(m.Faces[f].Normal, w));
        // 퇴화 면: 하프에지를 한 바퀴 돌며 트윈 쪽 이웃 면 중 면적이 있는 면의 법선을 더한다
        var sum = NVec3.Zero;
        int start = m.Faces[f].HalfEdge, he = start;
        do
        {
            int t = m.Hes[he].Twin;
            if (t >= 0)
            {
                int nf = m.Hes[t].Face;
                if (Core.Mesh.MeshNormals.FaceNormalUnnormalized(m, nf).LengthSquared() > 1e-14f) sum += NVec3.Normalize(NVec3.TransformNormal(m.Faces[nf].Normal, w));
            }
            he = m.Hes[he].Next;
        } while (he != start);
        return sum;
    }

    /// <summary>
    /// 선택 모드에 따라 컴포넌트 집합을 정점 ID 집합으로 펼친다(정점 = 그대로, 엣지 = 양끝, 면 = 면의 모든 정점).
    /// </summary>
    /// <param name="mesh">대상 메시.</param>
    /// <param name="comps">노드의 선택 컴포넌트.</param>
    /// <param name="mode">현재 선택 모드(Object/Uv면 빈 집합).</param>
    public static HashSet<int> SelectedVertices(Core.Mesh.PolyMesh mesh, ComponentSet comps, SelectMode mode)
    {
        var verts = new HashSet<int>();
        var tmp = new List<int>();
        switch (mode)
        {
            case SelectMode.Vertex: verts.UnionWith(comps.Verts); break;
            case SelectMode.Edge: foreach (int e in comps.Edges) { var (a, b) = mesh.EdgeVertices(e); verts.Add(a); verts.Add(b); } break;
            case SelectMode.Face: foreach (int f in comps.Faces) { mesh.GetFaceVertices(f, tmp); verts.UnionWith(tmp); } break;
        }
        return verts;
    }

    // ---------------------------------------------------------------- 입력

    /// <summary>
    /// 왼쪽 버튼 누름: 조작기가 보이고 핸들에 맞으면 드래그를 시작하고 true(선택 처리 생략).
    /// Ctrl을 누른 채 시작하면 조인트 격리 모드(<see cref="_isolate"/>).
    /// </summary>
    protected override bool OnPrimaryPress(InputEventMouseButton mb)
    {
        if (!Gizmo.Visible) return false;
        var proj = Picker.Projection();
        var px = new NVec2(mb.Position.X, mb.Position.Y);
        var part = Gizmo.HitTest(px, proj);
        if (part == GizmoPart.None) return false;
        _isolate = mb.CtrlPressed;
        BeginDrag(part, px, proj);
        return true;
    }

    /// <summary>
    /// 마우스 이동: 드래그 중이면 Shift 정밀 조정을 반영한 가상 포인터로 <see cref="UpdateDrag"/>를 호출하고,
    /// 아니면 조작기 핸들 호버 강조를 갱신한다.
    /// </summary>
    protected override bool OnHoverMotion(InputEventMouseMotion mm)
    {
        var px = new NVec2(mm.Position.X, mm.Position.Y);
        if (Dragging)
        {
            // 실제 커서 이동 델타를 Shift면 1/10로 줄여 가상 포인터에 누적
            var d = px - _lastPx; _lastPx = px;
            _virtualPx += mm.ShiftPressed ? d * PrecisionFactor : d;
            UpdateDrag(_virtualPx, Picker.Projection());
            return true;
        }
        if (Gizmo.Visible) Gizmo.SetHover(Gizmo.HitTest(px, Picker.Projection()));
        return false;
    }

    /// <summary>드래그 중 왼쪽 버튼 뗌 = 커밋, Esc = 취소. 그 외는 SelectTool 처리.</summary>
    public override bool HandleInput(InputEvent e)
    {
        if (Dragging && e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false })
        {
            EndDrag(commit: true);
            return true;
        }
        if (Dragging && e is InputEventKey { Keycode: Key.Escape, Pressed: true }) { EndDrag(commit: false); return true; }
        return base.HandleInput(e);
    }

    /// <summary>진행 중 드래그를 되돌린 뒤(커밋 안 함) 기반 취소.</summary>
    public override void Cancel()
    {
        if (Dragging) EndDrag(commit: false);
        base.Cancel();
    }

    /// <summary>
    /// 드래그 시작: 재생 포즈 해제, 드래그 상태·가상 포인터 초기화, 조작기 활성 표시, 대상 캡처 후 <see cref="OnDragBegin"/>.
    /// 오브젝트 모드는 선택 조상이 없는 노드의 시작 Local/부모 행렬을, 컴포넌트 모드는 정점 ID와 시작 위치를 저장한다.
    /// </summary>
    protected virtual void BeginDrag(GizmoPart part, NVec2 px, CameraProjection proj)
    {
        // 애니메이션 재생 포즈는 표시 전용: 편집은 항상 rest(바인드) 포즈 기준이므로 먼저 되돌리고 조작기를 다시 놓는다
        if (Anim.AnimationPlayback.Current is { } pb && (pb.Posed || pb.Playing)) { pb.Rest(); RefreshGizmo(); }
        // 드래그 상태 초기화
        Dragging = true; DragPart = part; PressPx = px; PressRay = proj.Unproject(px);
        _virtualPx = px; _lastPx = px;
        Gizmo.SetActive(part);
        PivotWorld = Gizmo.Pivot;
        ObjectTargets.Clear(); ComponentTargets.Clear();
        var sel = Ctx.Sel; var doc = Ctx.Doc;
        if (sel.Mode == SelectMode.Object)
        {
            // 오브젝트 모드: 선택된 조상이 있는 노드는 건너뛰고(부모가 움직이면 따라옴) 시작 상태 캡처
            var set = new HashSet<NodeId>(sel.Objects);
            foreach (var id in sel.Objects)
            {
                var n = doc.Find(id); if (n == null) continue;
                bool ancestorSel = false;
                for (var p = n.Parent; p != null && !p.IsRoot; p = p.Parent) if (set.Contains(p.Id)) { ancestorSel = true; break; }
                if (ancestorSel) continue;
                var parentWorld = n.Parent != null && !n.Parent.IsRoot ? n.Parent.WorldMatrix : Matrix4x4.Identity;
                Matrix4x4.Invert(parentWorld, out var inv);
                ObjectTargets.Add((n, n.Local, parentWorld, inv));
            }
            // Ctrl 격리: 대상 조인트의 자식 중 선택되지 않은 것들의 시작 월드를 기록(드래그 중 월드 유지용)
            _childComp.Clear();
            if (_isolate)
                foreach (var (n, _, _, _) in ObjectTargets)
                    if (n.IsJoint) foreach (var c in n.Children) if (!set.Contains(c.Id)) _childComp.Add((c, c.Local, c.WorldMatrix));
        }
        else
        {
            // 컴포넌트 모드: 노드별 대상 정점과 시작 위치, 월드 행렬 캡처
            foreach (var id in sel.NodesWithComponents(sel.Mode))
            {
                var n = doc.Find(id); if (n?.Mesh == null) continue;
                var verts = SelectedVertices(n.Mesh, sel.GetComponents(id), sel.Mode).ToArray();
                if (verts.Length == 0) continue;
                var init = new NVec3[verts.Length];
                for (int i = 0; i < verts.Length; i++) init[i] = n.Mesh.Verts[verts[i]].Position;
                var world = n.WorldMatrix;
                Matrix4x4.Invert(world, out var inv);
                ComponentTargets.Add((id, verts, init, world, inv));
            }
        }
        OnDragBegin(proj);
    }

    /// <summary>파생 툴용 드래그 시작 훅(대상 캡처 후 호출). 핸들별 초기 투영값 등을 계산한다.</summary>
    protected virtual void OnDragBegin(CameraProjection proj) { }
    /// <summary>파생 툴이 컴포넌트 드래그 결과를 다른 명령(히스토리 파라미터)으로 커밋하려면 반환한다. null이면 기본(이동/회전/스케일 op).</summary>
    protected virtual MoveVerticesCommand? MakeComponentCommand(NodeId id, int[] verts, NVec3[] init, NVec3[] after) => null;
    /// <summary>
    /// 파생 툴이 드래그 중 포인터 위치로 변형을 계산해 Apply* 헬퍼로 프리뷰 적용한다.
    /// </summary>
    /// <param name="px">Shift 정밀 조정이 반영된 가상 포인터(뷰포트 로컬 픽셀).</param>
    /// <param name="proj">현재 카메라 투영.</param>
    protected abstract void UpdateDrag(NVec2 px, CameraProjection proj);

    /// <summary>
    /// 드래그 종료. commit이면 오브젝트는 TransformNodesCommand 하나(자식 보정 포함), 컴포넌트는 노드별 MoveVerticesCommand를
    /// 한 Undo 그룹으로 alreadyApplied 푸시한다. 컴포넌트 명령에는 마지막 변형 op/파라미터를 붙여 구성 이력에 기록한다.
    /// commit이 아니면 모든 대상을 시작 상태로 되돌린다. 끝나면 캡처 상태를 비우고 조작기를 다시 배치한다.
    /// </summary>
    protected virtual void EndDrag(bool commit)
    {
        if (!Dragging) return;
        Dragging = false;
        Gizmo.SetActive(GizmoPart.None);
        var doc = Ctx.Doc;
        if (ObjectTargets.Count > 0)
        {
            // 오브젝트: 대상 + 보정 자식의 ID/시작/현재 Local을 나란히 모은다
            var ids = ObjectTargets.Select(t => t.node.Id).Concat(_childComp.Select(c => c.child.Id)).ToArray();
            var before = ObjectTargets.Select(t => t.initial).Concat(_childComp.Select(c => c.initial)).ToArray();
            var after = ObjectTargets.Select(t => t.node.Local).Concat(_childComp.Select(c => c.child.Local)).ToArray();
            if (!commit)
            {
                // 취소: 시작 Local로 복원하고 변경 통지
                for (int i = 0; i < ObjectTargets.Count; i++) { ObjectTargets[i].node.Local = before[i]; doc.Notify(new DocChange(ChangeKind.TransformChanged, ids[i])); }
                foreach (var (c, init, _) in _childComp) { c.Local = init; doc.Notify(new DocChange(ChangeKind.TransformChanged, c.Id)); }
            }
            else
            {
                // 커밋: 이미 적용된 상태이므로 alreadyApplied로 푸시(변화 없으면 생략)
                var cmd = new TransformNodesCommand(Label, ids, before, after);
                if (!cmd.IsNoop) doc.Undo.Push(cmd, alreadyApplied: true);
            }
        }
        if (ComponentTargets.Count > 0)
        {
            // 컴포넌트: 노드별 명령을 한 Undo 그룹으로 묶는다
            using var g = doc.Undo.BeginGroup(Label);
            foreach (var (id, verts, init, _, _) in ComponentTargets)
            {
                var mesh = doc.Get(id).Mesh!;
                var after = new NVec3[verts.Length];
                for (int i = 0; i < verts.Length; i++) after[i] = mesh.Verts[verts[i]].Position;
                if (!commit) { MoveVerticesCommand.Preview(doc, id, verts, init); continue; }
                // 파생 툴(Extrude 두께 등)이 자체 명령을 주면 그것을 사용
                var custom = MakeComponentCommand(id, verts, init, after);
                if (custom != null) { if (!custom.IsNoop) doc.Undo.Push(custom, alreadyApplied: true); continue; }
                // 기본: 마지막 op를 이 노드의 월드 행렬로 복제해 이동/회전/스케일 히스토리 항목으로 기록
                ComponentTransformOp? op = null;
                if (_lastOp != null) op = new ComponentTransformOp { Type = _lastOp.Type, Pivot = _lastOp.Pivot, Axis = _lastOp.Axis, BasisX = _lastOp.BasisX, BasisY = _lastOp.BasisY, BasisZ = _lastOp.BasisZ, MeshWorld = doc.Get(id).WorldMatrix };
                var cmd = new MoveVerticesCommand(Label, id, verts, init, after, op, _lastParams?.Clone());
                if (!cmd.IsNoop) doc.Undo.Push(cmd, alreadyApplied: true);
            }
        }
        // 캡처 상태 정리
        ObjectTargets.Clear(); ComponentTargets.Clear(); _childComp.Clear(); _isolate = false;
        _lastOp = null; _lastParams = null;
        RefreshGizmo();
    }

    /// <summary>Ctrl 드래그: 자식들의 월드 트랜스폼을 유지하도록 로컬을 다시 계산한다.</summary>
    private void CompensateChildren()
    /// <remarks>자식의 새 로컬 = 시작 월드 × inv(부모의 현재 월드). 피벗은 유지(FromMatrix(m, pivot) 규약).</remarks>
    {
        if (_childComp.Count == 0) return;
        var doc = Ctx.Doc;
        foreach (var (c, _, world) in _childComp)
        {
            Matrix4x4.Invert(c.Parent!.WorldMatrix, out var inv);
            c.Local = Transform3.FromMatrix(world * inv, c.Local.Pivot);
            doc.Notify(new DocChange(ChangeKind.TransformChanged, c.Id));
        }
    }

    // ---------------------------------------------------------------- 적용 헬퍼

    /// <summary>월드 델타(이동)를 대상에 적용(프리뷰).</summary>
    /// <remarks>
    /// 오브젝트는 델타를 부모 공간 방향으로 바꿔 Translation에 더하고, 컴포넌트는 메시 로컬 방향으로 바꿔 시작 위치에 더한다.
    /// 히스토리용 op(Move)와 파라미터를 기록하고 조작기 피벗도 함께 옮긴다.
    /// </remarks>
    protected void ApplyTranslation(NVec3 worldDelta)
    {
        var doc = Ctx.Doc;
        _lastOp = new ComponentTransformOp { Type = ComponentTransformOp.Kind.Move, Pivot = PivotWorld };
        _lastParams = _lastOp.DefaultParams(worldDelta, 0, NVec3.One);
        // 오브젝트: 부모 공간 델타를 Translation에 더함
        foreach (var (node, initial, _, parentInv) in ObjectTargets)
        {
            var local = initial;
            local.Translation = initial.Translation + NVec3.TransformNormal(worldDelta, parentInv);
            node.Local = local;
            doc.Notify(new DocChange(ChangeKind.TransformChanged, node.Id));
        }
        CompensateChildren();
        // 컴포넌트: 메시 로컬 공간 델타를 정점 시작 위치에 더함
        foreach (var (id, verts, init, _, worldInv) in ComponentTargets)
        {
            var d = NVec3.TransformNormal(worldDelta, worldInv);
            var pos = new NVec3[verts.Length];
            for (int i = 0; i < verts.Length; i++) pos[i] = init[i] + d;
            MoveVerticesCommand.Preview(doc, id, verts, pos);
        }
        Gizmo.Pivot = PivotWorld + worldDelta;
        Gizmo.MarkDirty();
    }

    /// <summary>피벗 기준 월드 축 회전을 적용(프리뷰). 오브젝트는 회전 속성과 위치만 바꾼다(스케일 유지).</summary>
    protected void ApplyRotation(NVec3 worldAxis, float angle)
    /// <remarks>angle은 라디안(히스토리 파라미터는 도로 저장). 컴포넌트는 T(-P)·R·T(P) 월드 행렬로 정점을 변환한다.</remarks>
    {
        var doc = Ctx.Doc;
        _lastOp = new ComponentTransformOp { Type = ComponentTransformOp.Kind.Rotate, Pivot = PivotWorld, Axis = NVec3.Normalize(worldAxis) };
        _lastParams = _lastOp.DefaultParams(NVec3.Zero, angle * 180f / MathF.PI, NVec3.One);
        var q = NQuat.CreateFromAxisAngle(NVec3.Normalize(worldAxis), angle);
        foreach (var (node, initial, parentWorld, parentInv) in ObjectTargets)
        {
            // 부모 공간에서의 회전/피벗
            var axisParent = NVec3.Normalize(NVec3.TransformNormal(worldAxis, parentInv));
            var qParent = NQuat.CreateFromAxisAngle(axisParent, angle);
            var pivotParent = NVec3.Transform(PivotWorld, parentInv);
            var local = initial;
            local.RotationDegrees = Transform3.QuaternionToEulerXYZDegrees(NQuat.Concatenate(initial.Rotation, qParent));
            // 오브젝트 피벗의 부모 공간 위치는 Pivot + Translation. 그 점을 조작기 피벗 기준으로 회전시킨다
            local.Translation = pivotParent + NVec3.Transform(initial.Pivot + initial.Translation - pivotParent, qParent) - initial.Pivot;
            node.Local = local;
            doc.Notify(new DocChange(ChangeKind.TransformChanged, node.Id));
        }
        CompensateChildren();
        // 컴포넌트: 피벗 기준 회전 행렬(행벡터 규약: 왼쪽부터 적용)
        var m = Matrix4x4.CreateTranslation(-PivotWorld) * Matrix4x4.CreateFromQuaternion(q) * Matrix4x4.CreateTranslation(PivotWorld);
        ApplyComponentsWorldMatrix(m);
    }

    /// <summary>기즈모 축 기저에서의 스케일을 적용(프리뷰). 오브젝트는 Scale 속성에 반영(축은 가장 가까운 로컬 축으로 매핑).</summary>
    /// <remarks>
    /// 오브젝트는 기즈모 축과 가장 정렬된 로컬 축에 배율을 곱하고, 위치(피벗의 부모 공간 위치)도 조작기 피벗 기준으로 스케일한다.
    /// 컴포넌트는 T(-P)·Bᵀ·S·B·T(P)(B = 기즈모 축 기저)로 기즈모 축 방향 비균등 스케일을 한다.
    /// </remarks>
    protected void ApplyScale(NVec3 scaleInGizmoAxes)
    {
        var doc = Ctx.Doc;
        // 기즈모 축과 히스토리용 op/파라미터 기록
        var gx = Gizmo.AxisX; var gy = Gizmo.AxisY; var gz = Gizmo.AxisZ;
        _lastOp = new ComponentTransformOp { Type = ComponentTransformOp.Kind.Scale, Pivot = PivotWorld, BasisX = gx, BasisY = gy, BasisZ = gz };
        _lastParams = _lastOp.DefaultParams(NVec3.Zero, 0, scaleInGizmoAxes);
        foreach (var (node, initial, parentWorld, parentInv) in ObjectTargets)
        {
            // 대상의 현재 월드 축(직교 정규화)
            var (lx, ly, lz) = DragMath.OrthonormalAxes(initial.ToMatrix() * parentWorld);
            var localScale = NVec3.One;
            // 기즈모 축별 배율을 가장 가까운 로컬 축에 적용
            foreach (var (axis, s) in new[] { (gx, scaleInGizmoAxes.X), (gy, scaleInGizmoAxes.Y), (gz, scaleInGizmoAxes.Z) })
            {
                if (MathF.Abs(s - 1f) < 1e-6f) continue;
                float dx = MathF.Abs(NVec3.Dot(axis, lx)), dy = MathF.Abs(NVec3.Dot(axis, ly)), dz = MathF.Abs(NVec3.Dot(axis, lz));
                if (dx >= dy && dx >= dz) localScale.X *= s; else if (dy >= dz) localScale.Y *= s; else localScale.Z *= s;
            }
            var local = initial;
            local.Scale = initial.Scale * localScale;
            // 피벗이 오브젝트 원점이 아니면(다중 선택) 위치도 피벗 기준으로 스케일
            var pivotParent = NVec3.Transform(PivotWorld, parentInv);
            var rel = initial.Pivot + initial.Translation - pivotParent;
            var relWorld = NVec3.TransformNormal(rel, parentWorld);
            var scaledWorld = gx * (NVec3.Dot(relWorld, gx) * scaleInGizmoAxes.X) + gy * (NVec3.Dot(relWorld, gy) * scaleInGizmoAxes.Y) + gz * (NVec3.Dot(relWorld, gz) * scaleInGizmoAxes.Z);
            local.Translation = pivotParent + NVec3.TransformNormal(scaledWorld, parentInv) - initial.Pivot;
            node.Local = local;
            doc.Notify(new DocChange(ChangeKind.TransformChanged, node.Id));
        }
        CompensateChildren();
        // 컴포넌트: 기즈모 기저로 회전 → 스케일 → 원래 기저로 되돌리는 피벗 기준 행렬
        var b = new Matrix4x4(gx.X, gx.Y, gx.Z, 0, gy.X, gy.Y, gy.Z, 0, gz.X, gz.Y, gz.Z, 0, 0, 0, 0, 1);
        var m = Matrix4x4.CreateTranslation(-PivotWorld) * Matrix4x4.Transpose(b) * Matrix4x4.CreateScale(scaleInGizmoAxes) * b * Matrix4x4.CreateTranslation(PivotWorld);
        ApplyComponentsWorldMatrix(m);
    }

    /// <summary>점 스냅(Retain component spacing off): 선택 정점/오브젝트 피벗을 모두 한 월드 점으로 모은다(프리뷰).</summary>
    protected void ApplyCollapseTo(NVec3 worldTarget)
    /// <remarks>오브젝트는 피벗의 월드 위치가 목표점에 오도록 Translation을 옮기고, 컴포넌트는 모든 정점을 목표점(로컬)으로 옮긴다.</remarks>
    {
        var doc = Ctx.Doc;
        _lastOp = new ComponentTransformOp { Type = ComponentTransformOp.Kind.Move, Pivot = PivotWorld };
        _lastParams = _lastOp.DefaultParams(worldTarget - PivotWorld, 0, NVec3.One);
        foreach (var (node, initial, parentWorld, parentInv) in ObjectTargets)
        {
            var pivotWorld = NVec3.Transform(initial.Pivot, initial.ToMatrix() * parentWorld);
            var local = initial;
            local.Translation = initial.Translation + NVec3.TransformNormal(worldTarget - pivotWorld, parentInv);
            node.Local = local;
            doc.Notify(new DocChange(ChangeKind.TransformChanged, node.Id));
        }
        CompensateChildren();
        foreach (var (id, verts, _, _, worldInv) in ComponentTargets)
        {
            var p = NVec3.Transform(worldTarget, worldInv);
            var pos = new NVec3[verts.Length];
            for (int i = 0; i < verts.Length; i++) pos[i] = p;
            MoveVerticesCommand.Preview(doc, id, verts, pos);
        }
        Gizmo.Pivot = worldTarget;
        Gizmo.MarkDirty();
    }

    /// <summary>Edit Pivot: 오브젝트는 그대로 두고 피벗만 월드 델타만큼 옮긴다(프리뷰).</summary>
    protected void ApplyPivotMove(NVec3 worldDelta)
    /// <remarks>WithPivotKeepingMatrix로 월드 행렬은 유지한 채 Pivot/Translation만 다시 푼다. 컴포넌트에는 적용하지 않는다.</remarks>
    {
        var doc = Ctx.Doc;
        foreach (var (node, initial, parentWorld, _) in ObjectTargets)
        {
            var world = initial.ToMatrix() * parentWorld;
            Matrix4x4.Invert(world, out var inv);
            var pivotWorld = NVec3.Transform(initial.Pivot, world) + worldDelta;
            node.Local = initial.WithPivotKeepingMatrix(NVec3.Transform(pivotWorld, inv));
            doc.Notify(new DocChange(ChangeKind.TransformChanged, node.Id));
        }
        Gizmo.Pivot = PivotWorld + worldDelta;
        Gizmo.MarkDirty();
    }

    /// <summary>Edit Pivot + 점 스냅: 선택 오브젝트의 피벗을 모두 한 월드 점으로.</summary>
    protected void ApplyPivotTo(NVec3 worldTarget)
    /// <remarks>목표 월드 점을 오브젝트 로컬로 바꿔 새 피벗으로 쓴다(월드 행렬 유지).</remarks>
    {
        var doc = Ctx.Doc;
        foreach (var (node, initial, parentWorld, _) in ObjectTargets)
        {
            var world = initial.ToMatrix() * parentWorld;
            Matrix4x4.Invert(world, out var inv);
            node.Local = initial.WithPivotKeepingMatrix(NVec3.Transform(worldTarget, inv));
            doc.Notify(new DocChange(ChangeKind.TransformChanged, node.Id));
        }
        Gizmo.Pivot = worldTarget;
        Gizmo.MarkDirty();
    }

    /// <summary>
    /// 컴포넌트 대상 정점에 월드 공간 변환 행렬을 적용(프리뷰)한다: 로컬 시작 위치 → 월드 → 변환 → 다시 로컬.
    /// </summary>
    /// <param name="worldDelta">월드 공간 변환 행렬(행벡터 규약).</param>
    private void ApplyComponentsWorldMatrix(Matrix4x4 worldDelta)
    {
        var doc = Ctx.Doc;
        foreach (var (id, verts, init, world, worldInv) in ComponentTargets)
        {
            var pos = new NVec3[verts.Length];
            for (int i = 0; i < verts.Length; i++)
            {
                var w = NVec3.Transform(init[i], world);
                w = NVec3.Transform(w, worldDelta);
                pos[i] = NVec3.Transform(w, worldInv);
            }
            MoveVerticesCommand.Preview(doc, id, verts, pos);
        }
        Gizmo.MarkDirty();
    }
}
