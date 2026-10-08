using System.Numerics;
using Cube.Core.Mesh;
using Cube.Core.Scene;
using Cube.Core.Selection;

namespace Cube.Core.Commands;

/// <summary>
/// 위상을 바꾸는 메시 편집의 공통 기반. 실행 전/후 메시 전체 스냅샷과 선택 스냅샷을 보관한다.
/// 파생 클래스는 <see cref="Execute"/>에서 메시를 수정하고 결과 선택을 설정한다.
/// </summary>
/// <remarks>
/// 첫 Do에서만 Execute를 실제로 호출하고 결과를 _after로 찍어 둔다. Redo는 _after를 복사해 넣기만 하므로 연산이 결정적이지 않아도 결과가 같다.
/// 노멀 재계산과 위상 버전 증가는 이 기반 클래스가 대신 한다. <see cref="MakeHistoryEntry"/>가 항목을 주면 구성 이력에 추가/제거한다.
/// </remarks>
public abstract class MeshEditCommand : ICommand
{
    /// <summary>편집 대상 노드.</summary>
    protected readonly NodeId NodeId;
    /// <summary>대상 노드를 외부(Action Popup 등)에서 읽기 위한 공개 접근자.</summary>
    public NodeId NodeIdPublic => NodeId;
    /// <summary>실행 전/후 메시 전체 복사본. _after != null이면 이미 한 번 실행된 명령(이후 Do는 Redo).</summary>
    private PolyMesh? _before, _after;
    /// <summary>실행 전/후 선택 스냅샷.</summary>
    private SelectionSnapshot? _selBefore, _selAfter;

    /// <summary>명령 이름(파생 클래스가 정함). 히스토리 항목 이름으로도 쓰인다.</summary>
    public abstract string Name { get; }

    /// <summary>대상 노드를 지정한다.</summary>
    protected MeshEditCommand(NodeId node) { NodeId = node; }

    /// <summary>메시를 제자리에서 수정한다. 반환값 false면 변경 없음(스택에 넣지 않아도 됨).</summary>
    protected abstract bool Execute(Document doc, SceneNode node, PolyMesh mesh);

    /// <summary>구성 이력 항목(없으면 null). 파라미터가 있으면 Properties의 History에서 편집할 수 있다.</summary>
    protected virtual HistoryEntry? MakeHistoryEntry() => null;
    /// <summary>첫 실행 때 만든 히스토리 항목. Redo/Undo에서 같은 객체를 다시 넣고 뺀다.</summary>
    private HistoryEntry? _entry;

    /// <summary>첫 실행이면 Execute → 노멀 재계산 → 스냅샷/히스토리 등록, Redo면 저장된 결과를 복원한다.</summary>
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
        // 첫 실행: 전 상태를 찍고 연산 실행.
        _before = mesh.Clone();
        _selBefore = doc.Selection.Capture();
        bool changed = Execute(doc, node, mesh);
        // 변경이 없으면 스냅샷을 버려 DidChange == false가 되게 한다(호출자가 스택에 넣지 않음).
        if (!changed) { _before = null; _selBefore = null; return; }
        // 결과 정리: 노멀 재계산, 위상 버전 증가, 후 상태 저장.
        MeshNormals.Recompute(mesh);
        mesh.BumpTopology();
        _after = mesh.Clone();
        _selAfter = doc.Selection.Capture();
        // 구성 이력 항목 등록(Before = 실행 직전 메시).
        _entry = MakeHistoryEntry();
        if (_entry != null) { _entry.Name = Name; _entry.Before = _before; node.MeshShape!.History.Add(_entry); }
        // 통지 중에는 선택을 비워 두었다가(옛/새 ID 혼동 방지) 뷰가 재빌드된 뒤 복원
        doc.Selection.GetComponents(NodeId).ClearAll();
        doc.Notify(new DocChange(ChangeKind.MeshTopology, NodeId));
        doc.Selection.Restore(_selAfter);
    }

    /// <summary>첫 실행에서 실제 변경이 있었는지. false면 호출자는 Undo 스택에 넣지 않는다.</summary>
    public bool DidChange => _after != null;

    /// <summary>실행 전 메시와 선택을 복원하고 히스토리 항목을 뺀다.</summary>
    public void Undo(Document doc)
    {
        if (_before == null) return;
        var node = doc.Get(NodeId); var mesh = node.Mesh!;
        // Do와 같은 이유로 통지 전 컴포넌트 선택을 비운다.
        doc.Selection.GetComponents(NodeId).ClearAll();
        mesh.CopyFrom(_before);
        if (_entry != null) node.MeshShape!.History.Remove(_entry);
        doc.Notify(new DocChange(ChangeKind.MeshTopology, NodeId));
        if (_selBefore != null) doc.Selection.Restore(_selBefore);
    }
}

/// <summary>정점 위치만 바꾸는 편집(컴포넌트 이동/회전/스케일). 드래그 후 alreadyApplied로 넣는다.</summary>
/// <remarks>
/// 대상 정점 ID와 전/후 위치 배열(같은 인덱스로 대응)만 보관하므로 가볍다. op 또는 replay와 파라미터가 있으면
/// 스택에 들어가는 순간(<see cref="OnPushedApplied"/>) 구성 이력 항목을 만들어 Action Popup/History에서 값을 고칠 수 있게 한다.
/// </remarks>
public sealed class MoveVerticesCommand : ICommand, IAppliedHook
{
    /// <summary>대상 노드, 정점 ID 목록, 그 정점들의 이동 전/후 위치(메시 로컬).</summary>
    private readonly NodeId _node; private readonly int[] _ids; private readonly Vector3[] _before, _after;
    /// <summary>조작기 변형 정보와 히스토리 파라미터 초기값(둘 다 선택 사항).</summary>
    private readonly ComponentTransformOp? _op; private readonly HistoryParams? _params;
    /// <summary>op 대신 쓰는 사용자 정의 재실행(Extrude 두께 등).</summary>
    private readonly Func<PolyMesh, HistoryParams, bool>? _replay;
    /// <summary>등록된 히스토리 항목(처음 한 번 만들고 Redo 때 재사용).</summary>
    private HistoryEntry? _entry;
    /// <summary>명령 이름(예: "Move", "Extrude Thickness").</summary>
    public string Name { get; }

    /// <summary>정점 이동 명령을 만든다. before/after는 ids와 같은 길이·순서여야 한다.</summary>
    /// <param name="replay">op 대신 쓰는 히스토리 재실행(예: Extrude 두께 = 정점별 방향 오프셋). Before 스냅샷에서 시작한 메시에 파라미터를 적용한다.</param>
    public MoveVerticesCommand(string name, NodeId node, int[] ids, Vector3[] before, Vector3[] after, ComponentTransformOp? op = null, HistoryParams? parameters = null, Func<PolyMesh, HistoryParams, bool>? replay = null)
    {
        Name = name; _node = node; _ids = ids; _before = before; _after = after; _op = op; _params = parameters; _replay = replay;
    }

    /// <summary>모든 정점의 전/후 위치가 같으면 true(빈 드래그).</summary>
    public bool IsNoop { get { for (int i = 0; i < _ids.Length; i++) if (_before[i] != _after[i]) return false; return true; } }
    /// <summary>대상 노드(Action Popup이 후속 드래그를 다시 만들 때 사용).</summary>
    public NodeId Node => _node;
    /// <summary>조작기 변형 정보(이동/회전/스케일). 사용자 정의 replay(Extrude 두께 등)면 null.</summary>
    public ComponentTransformOp? Op => _op;
    /// <summary>현재 파라미터(히스토리에서 편집되었으면 그 값).</summary>
    public HistoryParams? Params => _entry?.Params ?? _params;

    /// <summary>후 위치를 적용하고(Redo) 히스토리 항목을 다시 등록한다.</summary>
    public void Do(Document doc)
    {
        Apply(doc, _after);
        OnPushedApplied(doc);
    }

    /// <summary>드래그로 이미 적용된 상태로 들어올 때 히스토리 항목만 등록한다.</summary>
    public void OnPushedApplied(Document doc)
    {
        // 재실행 수단(op/replay)과 파라미터가 모두 있고 메시 셰이프일 때만 이력에 남긴다.
        if ((_op != null || _replay != null) && _params != null && doc.Get(_node).MeshShape is { } shape)
        {
            if (_entry == null)
            {
                // 람다가 this를 잡지 않도록 지역 변수로 복사.
                var ids = _ids; var before = _before; var op = _op;
                // Before 스냅샷 = 현재 메시에서 대상 정점만 이동 전 위치로 되돌린 복사본.
                var snapshot = shape.Mesh.Clone();
                for (int i = 0; i < ids.Length; i++) { var v = snapshot.Verts[ids[i]]; v.Position = before[i]; snapshot.Verts[ids[i]] = v; }
                _entry = new HistoryEntry
                {
                    Name = Name, Params = _params.Clone(), Before = snapshot,
                    // 기본 재실행: 파라미터로 로컬 델타 행렬을 만들어 살아 있는 대상 정점에 곱한다.
                    Replay = _replay ?? ((m, p) =>
                    {
                        var mat = op!.LocalMatrix(p);
                        for (int i = 0; i < ids.Length; i++)
                        {
                            if (ids[i] >= m.VertexCount || !m.Verts[ids[i]].Alive) continue;
                            var v = m.Verts[ids[i]]; v.Position = Vector3.Transform(v.Position, mat); m.Verts[ids[i]] = v;
                        }
                        return true;
                    }),
                };
            }
            shape.History.Add(_entry);
            doc.Notify(new DocChange(ChangeKind.HistoryChanged, _node));
        }
    }

    /// <summary>이전 위치로 되돌리고 히스토리 항목을 뺀다.</summary>
    public void Undo(Document doc)
    {
        Apply(doc, _before);
        if (_entry != null && doc.Get(_node).MeshShape?.History.Remove(_entry) == true) doc.Notify(new DocChange(ChangeKind.HistoryChanged, _node));
    }

    /// <summary>대상 정점에 위치 배열을 써 넣고 노멀 재계산 후 MeshGeometry(위상 불변)를 통지한다.</summary>
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
    /// <remarks>Apply와 같은 절차를 정적 메서드로 제공한다. 드래그가 끝나면 호출자가 명령을 만들어 alreadyApplied로 넣는다.</remarks>
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
