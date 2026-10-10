using System.Numerics;
using Cube.Core.Mesh;
using Cube.Core.Scene;

namespace Cube.Core.Commands;

/// <summary>Freeze Transformations 옵션(Maya Modify → Freeze Transformations □): 어떤 채널을 굽는지.</summary>
public sealed class FreezeOptions
{
    public bool Translate = true, Rotate = true, Scale = true;
}

/// <summary>
/// Maya Freeze Transformations(v0.0.66): 선택 노드와 그 자손의 로컬 TRS를 지오메트리에 구워 넣고 채널을 0/0/1로 되돌린다(월드 모양은 그대로).
/// </summary>
/// <remarks>
/// 노드마다 M = L·A(A = 부모가 구운 행렬, 최상위는 I)를 피벗 P 기준으로 분해해 얼리지 않는 채널만 남긴 newL을 만들고(피벗 Q' = 피벗 월드 위치 − T'),
/// B = M·inv(newL)을 메시 정점·잠긴 노멀에 적용한 뒤 자식에 A = B로 넘긴다. det(B) &lt; 0(홀수 음수 스케일)이면 면을 뒤집어 바깥 방향을 유지한다.
/// 메시는 전체 스냅샷(전/후)으로 Undo하고 구성 이력은 지운다(구운 정점에 예전 이력을 재실행하면 어긋나므로; Undo 시 복원).
/// 조인트는 Translate를 얼리지 않는다(본 위치는 Translation에 있어야 내보내기·바인드가 맞다). 스킨이 붙은 메시와 라이트는 건너뛴다(보고).
/// </remarks>
public sealed class FreezeTransformCommand : ICommand
{
    private readonly NodeId[] _roots;
    private readonly FreezeOptions _opts;
    private readonly List<(NodeId id, Transform3 before, Transform3 after)> _xforms = new();
    private readonly List<(NodeId id, PolyMesh before, PolyMesh after, List<HistoryEntry> history)> _meshes = new();
    private bool _computed;

    public string Name => "Freeze Transformations";
    /// <summary>얼린 노드 수 / 스킨 메시라 건너뛴 수 / 라이트라 건너뛴 수.</summary>
    public int Frozen, SkippedSkinned, SkippedLights;
    public bool IsNoop => _computed && _xforms.Count == 0 && _meshes.Count == 0;

    public FreezeTransformCommand(IEnumerable<NodeId> roots, FreezeOptions opts) { _roots = roots.ToArray(); _opts = opts; }

    public void Do(Document doc)
    {
        if (!_computed)
        {
            _computed = true;
            // 선택된 조상이 있는 노드는 그 조상에서 함께 처리된다
            var set = new HashSet<NodeId>(_roots);
            foreach (var id in _roots)
            {
                var n = doc.Find(id); if (n == null) continue;
                bool under = false; for (var p = n.Parent; p != null; p = p.Parent) if (set.Contains(p.Id)) { under = true; break; }
                if (!under) Walk(doc, n, Matrix4x4.Identity);
            }
            return;
        }
        foreach (var (id, _, after) in _xforms) { var n = doc.Find(id); if (n != null) { n.Local = after; doc.Notify(new DocChange(ChangeKind.TransformChanged, id)); } }
        foreach (var (id, _, after, _) in _meshes) ApplyMesh(doc, id, after, clearHistory: true, null);
    }

    public void Undo(Document doc)
    {
        foreach (var (id, before, _, history) in _meshes) ApplyMesh(doc, id, before, clearHistory: false, history);
        foreach (var (id, before, _) in _xforms) { var n = doc.Find(id); if (n != null) { n.Local = before; doc.Notify(new DocChange(ChangeKind.TransformChanged, id)); } }
    }

    private static void ApplyMesh(Document doc, NodeId id, PolyMesh snapshot, bool clearHistory, List<HistoryEntry>? history)
    {
        var shape = doc.Find(id)?.MeshShape; if (shape == null) return;
        shape.Mesh.CopyFrom(snapshot);
        shape.History.Clear();
        if (!clearHistory && history != null) shape.History.AddRange(history);
        doc.Notify(new DocChange(ChangeKind.MeshTopology, id));
        doc.Notify(new DocChange(ChangeKind.HistoryChanged, id));
    }

    /// <summary>노드 하나를 얼리고(부모가 구운 행렬 A 포함) 자식으로 내려간다.</summary>
    private void Walk(Document doc, SceneNode node, Matrix4x4 a)
    {
        if (node.IsLight) { SkippedLights++; return; }
        if (node.Skin != null) { SkippedSkinned++; return; }
        var l = node.Local;
        var m = l.ToMatrix() * a;
        var p = l.Pivot;
        var pw = Vector3.Transform(p, m);
        var dec = Transform3.FromMatrix(m, p);
        bool freezeT = _opts.Translate && !node.IsJoint;
        var t2 = freezeT ? Vector3.Zero : dec.Translation;
        var r2 = _opts.Rotate ? Vector3.Zero : dec.RotationDegrees;
        var s2 = _opts.Scale ? Vector3.One : dec.Scale;
        // 조인트: 피벗 없이 위치(m의 이동 성분)를 Translation에 그대로 두고 R/S만 얼린다. 그 외: 피벗 Q' = 피벗 월드 위치 − T'.
        var newL = node.IsJoint ? new Transform3(m.Translation, r2, s2, Vector3.Zero) : new Transform3(t2, r2, s2, pw - t2);
        var n = newL.ToMatrix();
        if (!Matrix4x4.Invert(n, out var invN)) invN = Matrix4x4.Identity;
        var b = m * invN;
        bool identityB = IsIdentity(b);
        if (newL != l)
        {
            _xforms.Add((node.Id, l, newL));
            node.Local = newL;
            doc.Notify(new DocChange(ChangeKind.TransformChanged, node.Id));
            Frozen++;
        }
        var shape = node.MeshShape;
        if (shape != null && !identityB)
        {
            var before = shape.Mesh.Clone();
            var history = new List<HistoryEntry>(shape.History);
            BakeMesh(shape.Mesh, b);
            shape.History.Clear();
            _meshes.Add((node.Id, before, shape.Mesh.Clone(), history));
            doc.Notify(new DocChange(ChangeKind.MeshTopology, node.Id));
            doc.Notify(new DocChange(ChangeKind.HistoryChanged, node.Id));
        }
        foreach (var c in node.Children.ToArray()) Walk(doc, c, b);
    }

    /// <summary>정점·잠긴 노멀·고정 코너 노멀에 행렬을 적용하고, 거울 행렬이면 면을 뒤집은 뒤 노멀을 다시 계산한다.</summary>
    public static void BakeMesh(PolyMesh m, Matrix4x4 b)
    {
        Matrix4x4.Invert(b, out var inv);
        var normalM = Matrix4x4.Transpose(inv);
        for (int v = 0; v < m.VertexCount; v++)
        {
            var vert = m.Verts[v]; if (!vert.Alive) continue;
            vert.Position = Vector3.Transform(vert.Position, b); m.Verts[v] = vert;
        }
        foreach (var key in m.LockedNormals.Keys.ToArray())
        {
            var tn = Vector3.TransformNormal(m.LockedNormals[key], normalM);
            if (tn.LengthSquared() > 1e-20f) m.LockedNormals[key] = Vector3.Normalize(tn);
        }
        for (int h = 0; h < m.HalfEdgeCount; h++)
        {
            var he = m.Hes[h]; if (!he.Alive || !he.NormalLocked) continue;
            var tn = Vector3.TransformNormal(he.Normal, normalM);
            if (tn.LengthSquared() > 1e-20f) { he.Normal = Vector3.Normalize(tn); m.Hes[h] = he; }
        }
        if (b.GetDeterminant() < 0)
            MeshOps.ReverseFaces(m, Enumerable.Range(0, m.FaceCount).Where(f => m.Faces[f].Alive));
        m.BumpGeometry();
        MeshNormals.Recompute(m);
    }

    private static bool IsIdentity(Matrix4x4 m)
    {
        var d = m - Matrix4x4.Identity;
        return MathF.Abs(d.M11) + MathF.Abs(d.M12) + MathF.Abs(d.M13) + MathF.Abs(d.M21) + MathF.Abs(d.M22) + MathF.Abs(d.M23)
             + MathF.Abs(d.M31) + MathF.Abs(d.M32) + MathF.Abs(d.M33) + MathF.Abs(d.M41) + MathF.Abs(d.M42) + MathF.Abs(d.M43) < 1e-6f;
    }
}
