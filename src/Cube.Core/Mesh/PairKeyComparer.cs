namespace Cube.Core.Mesh;

/// <summary>
/// 정수 쌍을 64비트로 합친 키((a &lt;&lt; 32) | b)용 비교자. .NET 기본 long 해시는 상위·하위 32비트를 XOR해 a ^ b가 되므로,
/// 인접한 정점 쌍(번호가 비슷함)이 같은 해시로 몰려 사전이 사실상 선형 탐색이 된다(대형 메시 가져오기에서 수 분 정지).
/// SplitMix64 끝단 혼합으로 비트를 고르게 섞는다.
/// </summary>
public sealed class PairKeyComparer : IEqualityComparer<long>
{
    public static readonly PairKeyComparer Instance = new();
    public bool Equals(long x, long y) => x == y;
    public int GetHashCode(long k)
    {
        ulong z = (ulong)k;
        z = (z ^ (z >> 30)) * 0xbf58476d1ce4e5b9UL;
        z = (z ^ (z >> 27)) * 0x94d049bb133111ebUL;
        z ^= z >> 31;
        return (int)z ^ (int)(z >> 32);
    }
}
