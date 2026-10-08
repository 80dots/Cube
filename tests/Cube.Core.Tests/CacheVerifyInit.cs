using System.Runtime.CompilerServices;
using Cube.Core.Mesh;

namespace Cube.Core.Tests;

/// <summary>모든 테스트에서 PolyMesh 엣지 맵/정점 출발 캐시를 매 조회마다 검증한다(캐시가 오래된 채로 쓰이면 바로 예외).</summary>
internal static class CacheVerifyInit
{
    [ModuleInitializer]
    internal static void Init() => PolyMesh.VerifyCaches = true;
}
