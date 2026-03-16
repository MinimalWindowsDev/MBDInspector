namespace MBDInspector;

public static class SectionControlHelper
{
    public static string FormatStatusText(double sliderValue) => $"{sliderValue:F0}%";

    public static bool TryUpdateStatusText(object? sectionTextControl, double? sliderValue, out string? text)
    {
        text = null;
        if (sectionTextControl is null || sliderValue is null)
        {
            return false;
        }

        text = FormatStatusText(sliderValue.Value);
        return true;
    }
}
