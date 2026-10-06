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
    private PolyMesh? _before, _after;
    private SelectionSnapshot? _selBefore, _selAfter;

    public abstract string Name { get; }

    protected MeshEditCommand(NodeId node) { NodeId = node; }

    /// <summary>메시를 제자리에서 수정한다. 반환값 false면 변경 없음(스택에 넣지 않아도 됨).</summary>
    protected abstract bool Execute(Document doc, SceneNode node, PolyMesh mesh);

    public void Do(Document doc)
    {
        var node = doc.Get(NodeId);
        var mesh = node.Mesh ?? throw new InvalidOperationException("node has no mesh");
        if (_after != null)
        {
            mesh.CopyFrom(_after);
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
        doc.Notify(new DocChange(ChangeKind.MeshTopology, NodeId));
        _selAfter = doc.Selection.Capture();
    }

    public bool DidChange => _after != null;

    public void Undo(Document doc)
    {
        if (_before == null) return;
        var mesh = doc.Get(NodeId).Mesh!;
        mesh.CopyFrom(_before);
        doc.Notify(new DocChange(ChangeKind.MeshTopology, NodeId));
        if (_selBefore != null) doc.Selection.Restore(_selBefore);
    }
}

/// <summary>정점 위치만 바꾸는 편집(컴포넌트 이동/회전/스케일). 드래그 후 alreadyApplied로 넣는다.</summary>
public sealed class MoveVerticesCommand : ICommand
{
    private readonly NodeId _node; private readonly int[] _ids; private readonly Vector3[] _before, _after;
    public string Name { get; }

    public MoveVerticesCommand(string name, NodeId node, int[] ids, Vector3[] before, Vector3[] after)
    {
        Name = name; _node = node; _ids = ids; _before = before; _after = after;
    }

    public bool IsNoop { get { for (int i = 0; i < _ids.Length; i++) if (_before[i] != _after[i]) return false; return true; } }

    public void Do(Document doc) => Apply(doc, _after);
    public void Undo(Document doc) => Apply(doc, _before);

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
