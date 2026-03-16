using System.Windows;
using Xunit;

namespace MBDInspector.Tests;

public sealed class WindowPlacementHelperTests
{
    [Fact]
    public void NeedsPrimaryWorkAreaReset_ReturnsTrue_WhenWindowIsOffscreen()
    {
        bool needsReset = WindowPlacementHelper.NeedsPrimaryWorkAreaReset(2500, 50, 1500, 940, new Rect(0, 0, 1920, 1040));

        Assert.True(needsReset);
    }

    [Fact]
    public void NeedsPrimaryWorkAreaReset_ReturnsFalse_WhenWindowFitsWorkArea()
    {
        bool needsReset = WindowPlacementHelper.NeedsPrimaryWorkAreaReset(200, 100, 1200, 800, new Rect(0, 0, 1920, 1040));

        Assert.False(needsReset);
    }

    [Fact]
    public void GetCenteredPosition_CentersWithinWorkArea()
    {
        Point centered = WindowPlacementHelper.GetCenteredPosition(1500, 940, new Rect(0, 0, 1920, 1040));

        Assert.Equal(210, centered.X);
        Assert.Equal(50, centered.Y);
    }
}
