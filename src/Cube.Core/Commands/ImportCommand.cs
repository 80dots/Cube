using Cube.Core.Scene;
using Cube.Core.Selection;

namespace Cube.Core.Commands;

/// <summary>가져온 노드들을 문서에 추가하고 선택한다. Undo 시 제거.</summary>
public sealed class ImportNodesCommand : ICommand
{
    private readonly List<SceneNode> _nodes;
    private SelectionSnapshot? _selBefore;
    public string Name => "Import";

    public ImportNodesCommand(IEnumerable<SceneNode> nodes) { _nodes = nodes.ToList(); }

    public void Do(Document doc)
    {
        _selBefore ??= doc.Selection.Capture();
        foreach (var n in _nodes) doc.AddNode(n);
        doc.Selection.Mode = SelectMode.Object;
        doc.Selection.SelectObjects(_nodes.Select(n => n.Id));
    }

    public void Undo(Document doc)
    {
        foreach (var n in _nodes) doc.RemoveNode(n);
        if (_selBefore != null) doc.Selection.Restore(_selBefore);
    }
}
