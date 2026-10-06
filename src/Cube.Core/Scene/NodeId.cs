namespace Cube.Core.Scene;

/// <summary>문서 내 노드의 안정 식별자. 세션 중 재사용되지 않는다.</summary>
public readonly record struct NodeId(int Value)
{
    public static readonly NodeId None = new(0);
    public bool IsNone => Value == 0;
    public override string ToString() => $"#{Value}";
}
