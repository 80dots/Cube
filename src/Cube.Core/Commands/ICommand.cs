using Cube.Core.Scene;

namespace Cube.Core.Commands;

/// <summary>되돌릴 수 있는 편집 단위. Do/Undo는 문서 상태만 바꾸고 통지(Notify)까지 책임진다.</summary>
public interface ICommand
{
    string Name { get; }
    void Do(Document doc);
    void Undo(Document doc);
}

/// <summary>alreadyApplied로 스택에 들어갈 때(드래그 결과 등) Do 대신 호출되는 훅. 히스토리 항목 등록 등에 쓴다.</summary>
public interface IAppliedHook
{
    void OnPushedApplied(Document doc);
}
