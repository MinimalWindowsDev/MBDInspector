using Xunit;

namespace MBDInspector.Tests;

public sealed class UiInitializationGuardTests
{
    [Fact]
    public void AreReady_ReturnsFalse_WhenAnyControlIsMissing()
    {
        bool ready = UiInitializationGuard.AreReady(new object(), null, new object());

        Assert.False(ready);
    }

    [Fact]
    public void AreReady_ReturnsTrue_WhenAllControlsExist()
    {
        bool ready = UiInitializationGuard.AreReady(new object(), new object(), new object());

        Assert.True(ready);
    }
}
