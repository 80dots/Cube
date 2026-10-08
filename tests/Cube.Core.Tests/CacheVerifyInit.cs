using System.Runtime.CompilerServices;
using Cube.Core.Mesh;

namespace Cube.Core.Tests;

/// <summary>모든 테스트에서 PolyMesh 엣지 맵/정점 출발 캐시를 매 조회마다 검증한다(캐시가 오래된 채로 쓰이면 바로 예외).</summary>
internal static class CacheVerifyInit
{
    /// <summary>
    /// 모듈 초기화자(<c>[ModuleInitializer]</c>): 테스트 어셈블리가 로드될 때 어떤 테스트보다 먼저 한 번 실행되어
    /// <c>PolyMesh.VerifyCaches</c>를 켠다. 덕분에 개별 테스트가 따로 설정하지 않아도 캐시 무결성이 항상 검사된다.
    /// </summary>
    [ModuleInitializer]
    internal static void Init() => PolyMesh.VerifyCaches = true;
}
