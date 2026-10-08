using Cube.Core.Scene;

namespace Cube.Core.Commands;

/// <summary>되돌릴 수 있는 편집 단위. Do/Undo는 문서 상태만 바꾸고 통지(Notify)까지 책임진다.</summary>
/// <remarks>
/// 모든 문서 편집은 이 인터페이스를 구현한 명령으로 <see cref="UndoStack.Push"/>를 거친다(Godot UndoRedo는 쓰지 않는다).
/// Do는 Redo에서도 다시 호출되므로 여러 번 호출해도 같은 결과가 나와야 하고(멱등), Undo는 Do 직전 상태를 정확히 복원해야 한다.
/// </remarks>
public interface ICommand
{
    /// <summary>Undo/Redo 메뉴와 Action Popup에 표시되는 명령 이름.</summary>
    string Name { get; }
    /// <summary>명령을 문서에 적용한다(처음 실행과 Redo 모두). 변경 통지(<c>Document.Notify</c>)까지 이 안에서 한다.</summary>
    void Do(Document doc);
    /// <summary>Do로 바꾼 상태를 되돌리고 통지한다.</summary>
    void Undo(Document doc);
}

/// <summary>alreadyApplied로 스택에 들어갈 때(드래그 결과 등) Do 대신 호출되는 훅. 히스토리 항목 등록 등에 쓴다.</summary>
/// <remarks>드래그 중 문서를 직접 바꾼 뒤 놓을 때 <c>Push(cmd, alreadyApplied: true)</c>로 넣으면 Do가 호출되지 않으므로, 구성 이력처럼 "스택에 들어가는 순간" 필요한 부수 작업을 여기서 한다.</remarks>
public interface IAppliedHook
{
    /// <summary>이미 적용된 명령이 스택에 들어갈 때 한 번 호출된다(Do를 대신함).</summary>
    void OnPushedApplied(Document doc);
}
