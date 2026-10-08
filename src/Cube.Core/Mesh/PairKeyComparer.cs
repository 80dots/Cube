namespace Cube.Core.Mesh;

/// <summary>
/// 정수 쌍을 64비트로 합친 키((a &lt;&lt; 32) | b)용 비교자. .NET 기본 long 해시는 상위·하위 32비트를 XOR해 a ^ b가 되므로,
/// 인접한 정점 쌍(번호가 비슷함)이 같은 해시로 몰려 사전이 사실상 선형 탐색이 된다(대형 메시 가져오기에서 수 분 정지).
/// SplitMix64 끝단 혼합으로 비트를 고르게 섞는다.
/// <see cref="PolyMesh"/>의 엣지 맵(정점 쌍 → 엣지 ID)처럼 정점 쌍을 키로 쓰는 사전에 넘겨 사용한다.
/// </summary>
public sealed class PairKeyComparer : IEqualityComparer<long>
{
    /// <summary>상태가 없으므로 공유해 쓰는 단일 인스턴스.</summary>
    public static readonly PairKeyComparer Instance = new();

    /// <summary>키 동등성: 64비트 값이 같으면 같은 정점 쌍이다.</summary>
    public bool Equals(long x, long y) => x == y;

    /// <summary>
    /// SplitMix64 최종 혼합(finalizer)으로 64비트 키의 모든 비트를 고르게 섞은 뒤 32비트로 접어 해시를 만든다.
    /// 시프트-XOR와 홀수 상수 곱셈을 두 번 반복해 a, b의 작은 차이도 해시 전체에 퍼지게 한다.
    /// </summary>
    /// <param name="k">정점 쌍을 합친 64비트 키.</param>
    /// <returns>사전 버킷 선택에 쓰는 32비트 해시.</returns>
    public int GetHashCode(long k)
    {
        // 부호 없는 64비트로 보고 혼합한다(산술 시프트로 부호 비트가 번지지 않도록)
        ulong z = (ulong)k;
        // SplitMix64 혼합 1단계: 상위 비트를 하위로 섞고 곱셈으로 확산
        z = (z ^ (z >> 30)) * 0xbf58476d1ce4e5b9UL;
        // 혼합 2단계
        z = (z ^ (z >> 27)) * 0x94d049bb133111ebUL;
        // 마지막 시프트-XOR로 상위 비트의 영향을 하위에도 반영
        z ^= z >> 31;
        // 64비트를 상위·하위 XOR로 32비트에 접는다
        return (int)z ^ (int)(z >> 32);
    }
}
