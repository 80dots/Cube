using Cube.Core.Scene;
using Cube.Core.Selection;

namespace Cube.Core.Commands;

/// <summary>선택 변경을 Undo 가능하게 만든다(Maya와 동일). 변경이 없으면 <see cref="IsNoop"/>.</summary>
public sealed class SelectionCommand : ICommand
{
    private readonly SelectionSnapshot _before;
    private readonly SelectionSnapshot _after;

    public string Name => "Select";
    public bool IsNoop => SelectionState.AreEqual(_before, _after);

    public SelectionCommand(SelectionSnapshot before, SelectionSnapshot after)
    {
        _before = before; _after = after;
    }

    /// <summary>현재 선택을 before로 캡처하고, 액션을 적용한 뒤 after를 캡처해 명령을 만든다. 이미 적용된 상태이므로 Push(alreadyApplied:true)로 넣는다.</summary>
    public static SelectionCommand Record(Document doc, Action<SelectionState> change)
    {
        var before = doc.Selection.Capture();
        change(doc.Selection);
        var after = doc.Selection.Capture();
        return new SelectionCommand(before, after);
    }

    public void Do(Document doc) => doc.Selection.Restore(_after);
    public void Undo(Document doc) => doc.Selection.Restore(_before);
}
