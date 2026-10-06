using System.Numerics;
using Cube.App.Viewport.Gizmos;
using Cube.Core.Commands;
using Cube.Core.Geometry;
using Cube.Core.Picking;
using Cube.Core.Scene;
using Cube.Core.Selection;
using Godot;
using NVec2 = System.Numerics.Vector2;
using NVec3 = System.Numerics.Vector3;

namespace Cube.App.Tools;

/// <summary>
/// 조작기를 가진 변형 툴의 공통 기반. 선택이 있으면 피벗에 조작기를 띄우고, 조작기 밖 클릭은 선택으로 처리한다.
/// 드래그 시작 시 초기값을 캡처하고, 드래그 중 문서를 직접 갱신(프리뷰)하며, 놓을 때 하나의 명령으로 커밋한다.
/// </summary>
public abstract class TransformToolBase : SelectTool
{
    protected GizmoBase Gizmo = null!;
    protected bool Dragging { get; private set; }
    protected GizmoPart DragPart { get; private set; }
    protected NVec2 PressPx;
    protected Ray PressRay;

    // 드래그 대상 캡처
    protected readonly List<(SceneNode node, Transform3 initial, Matrix4x4 parentInv)> ObjectTargets = new();
    protected readonly List<(NodeId node, int[] verts, NVec3[] initial, Matrix4x4 worldInv)> ComponentTargets = new();
    protected NVec3 PivotWorld;

    protected abstract GizmoBase CreateGizmo();

    public override void Activate(ToolContext ctx)
    {
        base.Activate(ctx);
        if (Gizmo == null)
        {
            Gizmo = CreateGizmo();
            Gizmo.Setup(ctx.Viewport);
            ctx.Viewport.GizmoRoot.AddChild(Gizmo);
        }
        ctx.Doc.Selection.Changed += OnSelectionChanged;
        ctx.Doc.Changed += OnDocChanged;
        RefreshGizmo();
    }

    public override void Deactivate()
    {
        Ctx.Doc.Selection.Changed -= OnSelectionChanged;
        Ctx.Doc.Changed -= OnDocChanged;
        Gizmo.Visible = false;
        base.Deactivate();
    }

    private void OnSelectionChanged() { if (!Dragging) RefreshGizmo(); }
    private void OnDocChanged(DocChange c) { if (!Dragging && c.Kind is ChangeKind.TransformChanged or ChangeKind.MeshGeometry or ChangeKind.MeshTopology or ChangeKind.NodeRemoved or ChangeKind.Reset) RefreshGizmo(); }

    /// <summary>선택 피벗과 축 방향으로 조작기를 배치한다.</summary>
    protected void RefreshGizmo()
    {
        if (!TryComputePivot(out var pivot)) { Gizmo.Visible = false; return; }
        Gizmo.Pivot = pivot;
        var (x, y, z) = ComputeAxes(pivot);
        Gizmo.AxisX = x; Gizmo.AxisY = y; Gizmo.AxisZ = z;
        Gizmo.Visible = true;
        Gizmo.SetHover(GizmoPart.None);
        Gizmo.MarkDirty();
    }

    protected bool TryComputePivot(out NVec3 pivot)
    {
        var sel = Ctx.Sel; var doc = Ctx.Doc;
        pivot = default;
        if (sel.Mode == SelectMode.Object)
        {
            var active = doc.Find(sel.ActiveObject);
            if (active == null) return false;
            pivot = active.WorldMatrix.Translation;
            return true;
        }
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

    protected (NVec3 x, NVec3 y, NVec3 z) ComputeAxes(NVec3 pivot)
    {
        var sel = Ctx.Sel; var doc = Ctx.Doc;
        switch (Ctx.AxisOrientation)
        {
            case AxisOrientation.Object:
                {
                    var node = doc.Find(sel.Mode == SelectMode.Object ? sel.ActiveObject : sel.NodesWithComponents(sel.Mode).FirstOrDefault());
                    if (node != null) return DragMath.OrthonormalAxes(node.WorldMatrix);
                    break;
                }
            case AxisOrientation.Normal:
                {
                    var normal = NVec3.Zero;
                    foreach (var id in sel.NodesWithComponents(sel.Mode))
                    {
                        var node = doc.Find(id); if (node?.Mesh == null) continue;
                        var m = node.Mesh; var w = node.WorldMatrix;
                        var faces = new HashSet<int>(sel.GetComponents(id).Faces);
                        var tmp = new List<int>();
                        if (sel.Mode == SelectMode.Vertex) foreach (int v in sel.GetComponents(id).Verts) { m.GetVertexFaces(v, tmp); faces.UnionWith(tmp); }
                        if (sel.Mode == SelectMode.Edge) foreach (int e in sel.GetComponents(id).Edges) { var (f0, f1) = m.EdgeFaces(e); if (f0 >= 0) faces.Add(f0); if (f1 >= 0) faces.Add(f1); }
                        foreach (int f in faces) normal += NVec3.Normalize(NVec3.TransformNormal(m.Faces[f].Normal, w));
                    }
                    if (normal.LengthSquared() > 1e-8f) return DragMath.BasisFromNormal(normal);
                    var node2 = doc.Find(sel.ActiveObject);
                    if (node2 != null) return DragMath.OrthonormalAxes(node2.WorldMatrix);
                    break;
                }
        }
        return (NVec3.UnitX, NVec3.UnitY, NVec3.UnitZ);
    }

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

    protected override bool OnPrimaryPress(InputEventMouseButton mb)
    {
        if (!Gizmo.Visible) return false;
        var proj = Picker.Projection();
        var px = new NVec2(mb.Position.X, mb.Position.Y);
        var part = Gizmo.HitTest(px, proj);
        if (part == GizmoPart.None) return false;
        BeginDrag(part, px, proj);
        return true;
    }

    protected override bool OnHoverMotion(InputEventMouseMotion mm)
    {
        var px = new NVec2(mm.Position.X, mm.Position.Y);
        if (Dragging) { UpdateDrag(px, Picker.Projection()); return true; }
        if (Gizmo.Visible) Gizmo.SetHover(Gizmo.HitTest(px, Picker.Projection()));
        return false;
    }

    public override bool HandleInput(InputEvent e)
    {
        if (Dragging && e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false })
        {
            EndDrag(commit: true);
            return true;
        }
        return base.HandleInput(e);
    }

    public override void Cancel()
    {
        if (Dragging) EndDrag(commit: false);
        base.Cancel();
    }

    protected virtual void BeginDrag(GizmoPart part, NVec2 px, CameraProjection proj)
    {
        Dragging = true; DragPart = part; PressPx = px; PressRay = proj.Unproject(px);
        Gizmo.SetActive(part);
        PivotWorld = Gizmo.Pivot;
        ObjectTargets.Clear(); ComponentTargets.Clear();
        var sel = Ctx.Sel; var doc = Ctx.Doc;
        if (sel.Mode == SelectMode.Object)
        {
            // 선택된 노드 중 조상이 함께 선택된 것은 제외(부모 이동에 따라감)
            var set = new HashSet<NodeId>(sel.Objects);
            foreach (var id in sel.Objects)
            {
                var n = doc.Find(id); if (n == null) continue;
                bool ancestorSel = false;
                for (var p = n.Parent; p != null && !p.IsRoot; p = p.Parent) if (set.Contains(p.Id)) { ancestorSel = true; break; }
                if (ancestorSel) continue;
                var parentWorld = n.Parent != null && !n.Parent.IsRoot ? n.Parent.WorldMatrix : Matrix4x4.Identity;
                Matrix4x4.Invert(parentWorld, out var inv);
                ObjectTargets.Add((n, n.Local, inv));
            }
        }
        else
        {
            foreach (var id in sel.NodesWithComponents(sel.Mode))
            {
                var n = doc.Find(id); if (n?.Mesh == null) continue;
                var verts = SelectedVertices(n.Mesh, sel.GetComponents(id), sel.Mode).ToArray();
                var init = new NVec3[verts.Length];
                for (int i = 0; i < verts.Length; i++) init[i] = n.Mesh.Verts[verts[i]].Position;
                Matrix4x4.Invert(n.WorldMatrix, out var inv);
                ComponentTargets.Add((id, verts, init, inv));
            }
        }
        OnDragBegin(proj);
    }

    protected virtual void OnDragBegin(CameraProjection proj) { }
    protected abstract void UpdateDrag(NVec2 px, CameraProjection proj);

    protected virtual void EndDrag(bool commit)
    {
        Dragging = false;
        Gizmo.SetActive(GizmoPart.None);
        var doc = Ctx.Doc;
        if (ObjectTargets.Count > 0)
        {
            var ids = ObjectTargets.Select(t => t.node.Id).ToArray();
            var before = ObjectTargets.Select(t => t.initial).ToArray();
            var after = ObjectTargets.Select(t => t.node.Local).ToArray();
            if (!commit)
            {
                for (int i = 0; i < ids.Length; i++) { ObjectTargets[i].node.Local = before[i]; doc.Notify(new DocChange(ChangeKind.TransformChanged, ids[i])); }
            }
            else
            {
                var cmd = new TransformNodesCommand(Label, ids, before, after);
                if (!cmd.IsNoop) doc.Undo.Push(cmd, alreadyApplied: true);
            }
        }
        if (ComponentTargets.Count > 0)
        {
            using var g = doc.Undo.BeginGroup(Label);
            foreach (var (id, verts, init, _) in ComponentTargets)
            {
                var mesh = doc.Get(id).Mesh!;
                var after = new NVec3[verts.Length];
                for (int i = 0; i < verts.Length; i++) after[i] = mesh.Verts[verts[i]].Position;
                if (!commit) { MoveVerticesCommand.Preview(doc, id, verts, init); continue; }
                var cmd = new MoveVerticesCommand(Label, id, verts, init, after);
                if (!cmd.IsNoop) doc.Undo.Push(cmd, alreadyApplied: true);
            }
        }
        ObjectTargets.Clear(); ComponentTargets.Clear();
        RefreshGizmo();
    }

    // ---------------------------------------------------------------- 적용 헬퍼

    /// <summary>월드 델타(이동)를 대상에 적용(프리뷰).</summary>
    protected void ApplyTranslation(NVec3 worldDelta)
    {
        var doc = Ctx.Doc;
        foreach (var (node, initial, parentInv) in ObjectTargets)
        {
            var local = initial;
            local.Translation = initial.Translation + NVec3.TransformNormal(worldDelta, parentInv);
            node.Local = local;
            doc.Notify(new DocChange(ChangeKind.TransformChanged, node.Id));
        }
        foreach (var (id, verts, init, worldInv) in ComponentTargets)
        {
            var d = NVec3.TransformNormal(worldDelta, worldInv);
            var pos = new NVec3[verts.Length];
            for (int i = 0; i < verts.Length; i++) pos[i] = init[i] + d;
            MoveVerticesCommand.Preview(doc, id, verts, pos);
        }
        Gizmo.Pivot = PivotWorld + worldDelta;
        Gizmo.MarkDirty();
    }

    /// <summary>피벗 기준 월드 변환 행렬(회전/스케일)을 대상에 적용(프리뷰).</summary>
    protected void ApplyWorldMatrixAboutPivot(Matrix4x4 worldDeltaAboutPivot)
    {
        var doc = Ctx.Doc;
        foreach (var (node, initial, parentInv) in ObjectTargets)
        {
            var parentWorld = node.Parent != null && !node.Parent.IsRoot ? node.Parent.WorldMatrix : Matrix4x4.Identity;
            var world = initial.ToMatrix() * parentWorld;
            var newWorld = world * worldDeltaAboutPivot;
            var newLocal = newWorld * parentInv;
            node.Local = Transform3.FromMatrix(newLocal);
            doc.Notify(new DocChange(ChangeKind.TransformChanged, node.Id));
        }
        foreach (var (id, verts, init, worldInv) in ComponentTargets)
        {
            var node = doc.Get(id);
            var world = node.WorldMatrix;
            var pos = new NVec3[verts.Length];
            for (int i = 0; i < verts.Length; i++)
            {
                var w = NVec3.Transform(init[i], world);
                w = NVec3.Transform(w, worldDeltaAboutPivot);
                pos[i] = NVec3.Transform(w, worldInv);
            }
            MoveVerticesCommand.Preview(doc, id, verts, pos);
        }
        Gizmo.MarkDirty();
    }
}
