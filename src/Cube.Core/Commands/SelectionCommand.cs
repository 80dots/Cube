using Cube.Core.Scene;
using Cube.Core.Selection;

namespace Cube.Core.Commands;

/// <summary>선택 변경을 Undo 가능하게 만든다(Maya와 동일). 변경이 없으면 <see cref="IsNoop"/>.</summary>
/// <remarks>선택 전/후 전체 스냅샷(모드, 오브젝트, 컴포넌트, hilite 대상)을 들고 있다가 Restore로 교체한다.</remarks>
public sealed class SelectionCommand : ICommand
{
    /// <summary>변경 전 선택 상태.</summary>
    private readonly SelectionSnapshot _before;
    /// <summary>변경 후 선택 상태.</summary>
    private readonly SelectionSnapshot _after;

    /// <summary>명령 이름("Select").</summary>
    public string Name => "Select";
    /// <summary>전후 스냅샷이 같으면 true. 호출자는 이 경우 스택에 넣지 않아 빈 Undo 스텝을 만들지 않는다.</summary>
    public bool IsNoop => SelectionState.AreEqual(_before, _after);

    /// <summary>미리 캡처한 전/후 스냅샷으로 명령을 만든다.</summary>
    public SelectionCommand(SelectionSnapshot before, SelectionSnapshot after)
    {
        _before = before; _after = after;
    }

    /// <summary>현재 선택을 before로 캡처하고, 액션을 적용한 뒤 after를 캡처해 명령을 만든다. 이미 적용된 상태이므로 Push(alreadyApplied:true)로 넣는다.</summary>
    /// <param name="doc">선택을 가진 문서.</param>
    /// <param name="change">선택을 실제로 바꾸는 동작.</param>
    /// <returns>전/후 스냅샷을 담은 명령(이미 적용됨).</returns>
    public static SelectionCommand Record(Document doc, Action<SelectionState> change)
    {
        var before = doc.Selection.Capture();
        change(doc.Selection);
        var after = doc.Selection.Capture();
        return new SelectionCommand(before, after);
    }

    /// <summary>변경 후 선택으로 되돌린다(Redo).</summary>
    public void Do(Document doc) => doc.Selection.Restore(_after);
    /// <summary>변경 전 선택으로 되돌린다.</summary>
    public void Undo(Document doc) => doc.Selection.Restore(_before);
}
