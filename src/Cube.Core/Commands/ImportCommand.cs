using Cube.Core.Scene;
using Cube.Core.Selection;

namespace Cube.Core.Commands;

/// <summary>가져온 노드들을 문서에 추가하고 선택한다. Undo 시 제거.</summary>
/// <remarks>glTF/FBX/OBJ 가져오기와 브리지 다시 읽기가 사용한다. 노드 객체 자체를 보관하므로 Redo 때 같은 객체(같은 ID)가 다시 들어간다.</remarks>
public sealed class ImportNodesCommand : ICommand
{
    /// <summary>문서에 넣을 최상위 노드들(자식은 노드 트리에 매달려 함께 추가된다).</summary>
    private readonly List<SceneNode> _nodes;
    /// <summary>처음 Do 직전의 선택 스냅샷. Undo에서 복원하며, Redo 때 다시 캡처하지 않도록 ??=로 한 번만 채운다.</summary>
    private SelectionSnapshot? _selBefore;
    /// <summary>명령 이름("Import").</summary>
    public string Name => "Import";

    /// <summary>가져온 노드 목록을 복사해 보관한다.</summary>
    public ImportNodesCommand(IEnumerable<SceneNode> nodes) { _nodes = nodes.ToList(); }

    /// <summary>노드를 문서에 추가하고 오브젝트 모드로 바꿔 가져온 노드만 선택한다.</summary>
    public void Do(Document doc)
    {
        // 최초 실행 때만 이전 선택을 기억한다(Redo에서 덮어쓰지 않음).
        _selBefore ??= doc.Selection.Capture();
        // 노드 추가 → AddNode가 ID를 배정하고 NodeAdded를 통지한다.
        foreach (var n in _nodes) doc.AddNode(n);
        // 가져온 결과를 바로 볼 수 있게 오브젝트 모드에서 새 노드만 선택.
        doc.Selection.Mode = SelectMode.Object;
        doc.Selection.SelectObjects(_nodes.Select(n => n.Id));
    }

    /// <summary>추가했던 노드를 제거하고 이전 선택을 복원한다.</summary>
    public void Undo(Document doc)
    {
        foreach (var n in _nodes) doc.RemoveNode(n);
        if (_selBefore != null) doc.Selection.Restore(_selBefore);
    }
}
