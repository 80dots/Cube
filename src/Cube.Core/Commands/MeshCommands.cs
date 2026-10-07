using System.Numerics;
using Cube.Core.Mesh;
using Cube.Core.Scene;
using Cube.Core.Selection;

namespace Cube.Core.Commands;

/// <summary>
/// 위상을 바꾸는 메시 편집의 공통 기반. 실행 전/후 메시 전체 스냅샷과 선택 스냅샷을 보관한다.
/// 파생 클래스는 <see cref="Execute"/>에서 메시를 수정하고 결과 선택을 설정한다.
/// </summary>
public abstract class MeshEditCommand : ICommand
{
    protected readonly NodeId NodeId;
    public NodeId NodeIdPublic => NodeId;
    private PolyMesh? _before, _after;
    private SelectionSnapshot? _selBefore, _selAfter;

    public abstract string Name { get; }

    protected MeshEditCommand(NodeId node) { NodeId = node; }

    /// <summary>메시를 제자리에서 수정한다. 반환값 false면 변경 없음(스택에 넣지 않아도 됨).</summary>
    protected abstract bool Execute(Document doc, SceneNode node, PolyMesh mesh);

    /// <summary>구성 이력 항목(없으면 null). 파라미터가 있으면 Properties의 History에서 편집할 수 있다.</summary>
    protected virtual HistoryEntry? MakeHistoryEntry() => null;
    private HistoryEntry? _entry;

    public void Do(Document doc)
    {
        var node = doc.Get(NodeId);
        var mesh = node.Mesh ?? throw new InvalidOperationException("node has no mesh");
        if (_after != null)
        {
            // 위상이 바뀌는 동안 선택이 옛 ID를 가리키지 않도록 먼저 비운다(조용히), 통지 후 복원
            doc.Selection.GetComponents(NodeId).ClearAll();
            mesh.CopyFrom(_after);
            if (_entry != null) { _entry.Before = _before!; node.MeshShape!.History.Add(_entry); }
            doc.Notify(new DocChange(ChangeKind.MeshTopology, NodeId));
            if (_selAfter != null) doc.Selection.Restore(_selAfter);
            return;
        }
        _before = mesh.Clone();
        _selBefore = doc.Selection.Capture();
        bool changed = Execute(doc, node, mesh);
        if (!changed) { _before = null; _selBefore = null; return; }
        MeshNormals.Recompute(mesh);
        mesh.BumpTopology();
        _after = mesh.Clone();
        _selAfter = doc.Selection.Capture();
        _entry = MakeHistoryEntry();
        if (_entry != null) { _entry.Name = Name; _entry.Before = _before; node.MeshShape!.History.Add(_entry); }
        // 통지 중에는 선택을 비워 두었다가(옛/새 ID 혼동 방지) 뷰가 재빌드된 뒤 복원
        doc.Selection.GetComponents(NodeId).ClearAll();
        doc.Notify(new DocChange(ChangeKind.MeshTopology, NodeId));
        doc.Selection.Restore(_selAfter);
    }

    public bool DidChange => _after != null;

    public void Undo(Document doc)
    {
        if (_before == null) return;
        var node = doc.Get(NodeId); var mesh = node.Mesh!;
        doc.Selection.GetComponents(NodeId).ClearAll();
        mesh.CopyFrom(_before);
        if (_entry != null) node.MeshShape!.History.Remove(_entry);
        doc.Notify(new DocChange(ChangeKind.MeshTopology, NodeId));
        if (_selBefore != null) doc.Selection.Restore(_selBefore);
    }
}

/// <summary>정점 위치만 바꾸는 편집(컴포넌트 이동/회전/스케일). 드래그 후 alreadyApplied로 넣는다.</summary>
public sealed class MoveVerticesCommand : ICommand, IAppliedHook
{
    private readonly NodeId _node; private readonly int[] _ids; private readonly Vector3[] _before, _after;
    private readonly ComponentTransformOp? _op; private readonly HistoryParams? _params;
    private HistoryEntry? _entry;
    public string Name { get; }

    public MoveVerticesCommand(string name, NodeId node, int[] ids, Vector3[] before, Vector3[] after, ComponentTransformOp? op = null, HistoryParams? parameters = null)
    {
        Name = name; _node = node; _ids = ids; _before = before; _after = after; _op = op; _params = parameters;
    }

    public bool IsNoop { get { for (int i = 0; i < _ids.Length; i++) if (_before[i] != _after[i]) return false; return true; } }

    public void Do(Document doc)
    {
        Apply(doc, _after);
        OnPushedApplied(doc);
    }

    /// <summary>드래그로 이미 적용된 상태로 들어올 때 히스토리 항목만 등록한다.</summary>
    public void OnPushedApplied(Document doc)
    {
        if (_op != null && _params != null && doc.Get(_node).MeshShape is { } shape)
        {
            if (_entry == null)
            {
                var ids = _ids; var before = _before; var op = _op;
                var snapshot = shape.Mesh.Clone();
                for (int i = 0; i < ids.Length; i++) { var v = snapshot.Verts[ids[i]]; v.Position = before[i]; snapshot.Verts[ids[i]] = v; }
                _entry = new HistoryEntry
                {
                    Name = Name, Params = _params.Clone(), Before = snapshot,
                    Replay = (m, p) =>
                    {
                        var mat = op.LocalMatrix(p);
                        for (int i = 0; i < ids.Length; i++)
                        {
                            if (ids[i] >= m.VertexCount || !m.Verts[ids[i]].Alive) continue;
                            var v = m.Verts[ids[i]]; v.Position = Vector3.Transform(v.Position, mat); m.Verts[ids[i]] = v;
                        }
                        return true;
                    },
                };
            }
            shape.History.Add(_entry);
        }
    }

    public void Undo(Document doc)
    {
        Apply(doc, _before);
        if (_entry != null) doc.Get(_node).MeshShape?.History.Remove(_entry);
    }

    private void Apply(Document doc, Vector3[] pos)
    {
        var mesh = doc.Get(_node).Mesh!;
        for (int i = 0; i < _ids.Length; i++)
        {
            var v = mesh.Verts[_ids[i]]; v.Position = pos[i]; mesh.Verts[_ids[i]] = v;
        }
        MeshNormals.Recompute(mesh);
        mesh.BumpGeometry();
        doc.Notify(new DocChange(ChangeKind.MeshGeometry, _node));
    }

    /// <summary>드래그 중 프리뷰 적용(명령 생성 없이).</summary>
    public static void Preview(Document doc, NodeId node, int[] ids, Vector3[] pos)
    {
        var mesh = doc.Get(node).Mesh!;
        for (int i = 0; i < ids.Length; i++)
        {
            var v = mesh.Verts[ids[i]]; v.Position = pos[i]; mesh.Verts[ids[i]] = v;
        }
        MeshNormals.Recompute(mesh);
        mesh.BumpGeometry();
        doc.Notify(new DocChange(ChangeKind.MeshGeometry, node));
    }
}
