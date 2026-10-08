namespace Cube.Core.Tests;

/// <summary>
/// 테스트 프로젝트가 Cube.Core 어셈블리를 제대로 참조하고 로드하는지 확인하는 최소 스모크 테스트.
/// 다른 테스트가 모두 실패할 때 원인이 빌드/참조 문제인지 먼저 가려내는 용도다.
/// </summary>
public class SmokeTests
{
    /// <summary>
    /// <see cref="CoreInfo"/> 타입이 속한 어셈블리 이름이 "Cube.Core"인지 확인한다.
    /// 프로젝트 참조가 끊기거나 어셈블리 이름이 바뀌면 여기서 바로 드러난다.
    /// </summary>
    [Fact]
    public void CoreAssemblyLoads()
    {
        Assert.Equal("Cube.Core", typeof(CoreInfo).Assembly.GetName().Name);
    }
}
