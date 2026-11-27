using Xunit;

namespace DittoHopper.Core.Tests;

public class SmokeTests
{
    [Fact]
    public void CoreAssemblyLoads() => Assert.Equal("DittoHopper.Core", typeof(ModelDiff).Assembly.GetName().Name);
}
