namespace Cube.Core.Scene;

/// <summary>문서 내 노드의 안정 식별자. 세션 중 재사용되지 않는다.</summary>
/// <remarks>값 0은 "없음"(<see cref="None"/>)이며 실제 노드는 1부터 배정된다(<c>Document.AddNode/AssignIds</c>). 선택·명령·애니메이션 트랙이 노드를 이 값으로 참조한다.</remarks>
public readonly record struct NodeId(int Value)
{
    /// <summary>"노드 없음"(루트 부모 표시 등).</summary>
    public static readonly NodeId None = new(0);
    /// <summary>None인지(값 0).</summary>
    public bool IsNone => Value == 0;
    /// <summary>디버그 표시용 "#번호".</summary>
    public override string ToString() => $"#{Value}";
}
