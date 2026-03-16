namespace MBDInspector;
public static class SectionControlHelper
{
    public static string FormatStatusText(double sliderValue) => $"{sliderValue:F0}%";

    public static char NormalizeAxis(string? axisText)
    {
        string normalized = string.IsNullOrWhiteSpace(axisText) ? "X" : axisText.Trim();
        return normalized.Length > 0 ? char.ToUpperInvariant(normalized[0]) : 'X';
    }

    public static bool IsEntityVisible(
        bool sectionEnabled,
        char axis,
        double thresholdPercent,
        double entityAxisValue,
        double minAxisValue,
        double maxAxisValue)
    {
        if (!sectionEnabled)
        {
            return true;
        }

        double threshold = minAxisValue + ((maxAxisValue - minAxisValue) * thresholdPercent);
        _ = axis switch
        {
            'X' or 'Y' or 'Z' => axis,
            _ => 'X'
        };
        return entityAxisValue <= threshold;
    }

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
