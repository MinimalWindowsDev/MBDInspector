using Xunit;

namespace MBDInspector.Tests;

public sealed class SectionControlHelperTests
{
    [Fact]
    public void TryUpdateStatusText_ReturnsFalse_WhenSectionTextControlMissing()
    {
        bool updated = SectionControlHelper.TryUpdateStatusText(null, 50, out string? text);

        Assert.False(updated);
        Assert.Null(text);
    }

    [Fact]
    public void TryUpdateStatusText_ReturnsFalse_WhenSliderValueMissing()
    {
        bool updated = SectionControlHelper.TryUpdateStatusText(new object(), null, out string? text);

        Assert.False(updated);
        Assert.Null(text);
    }

    [Fact]
    public void TryUpdateStatusText_FormatsPercentage_WhenInputsPresent()
    {
        bool updated = SectionControlHelper.TryUpdateStatusText(new object(), 42.4, out string? text);

        Assert.True(updated);
        Assert.Equal("42%", text);
    }

    [Fact]
    public void FormatStatusText_RoundsToWholePercentage()
    {
        string text = SectionControlHelper.FormatStatusText(49.6);

        Assert.Equal("50%", text);
    }

    [Fact]
    public void AxisFallsBackToX_WhenComboResultIsEmpty()
    {
        char axis = SectionControlHelper.NormalizeAxis(string.Empty);
        bool visible = SectionControlHelper.IsEntityVisible(true, axis, 0.5, 4.0, 0.0, 10.0);

        Assert.Equal('X', axis);
        Assert.True(visible);
    }
}
