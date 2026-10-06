namespace Cube.Core.Tests;

public class SmokeTests
{
    [Fact]
    public void CoreAssemblyLoads()
    {
        Assert.Equal("Cube.Core", typeof(CoreInfo).Assembly.GetName().Name);
    }
}
