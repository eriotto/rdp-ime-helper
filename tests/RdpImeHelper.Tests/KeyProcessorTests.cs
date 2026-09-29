using RdpImeHelper.Logic;

namespace RdpImeHelper.Tests;

public class KeyProcessorTests
{
    [Fact]
    public void CanCreate()
    {
        Assert.NotNull(new KeyProcessor());
    }
}
