using System;
using System.Windows;

namespace MBDInspector;

public static class WindowPlacementHelper
{
    public static bool NeedsPrimaryWorkAreaReset(double left, double top, double width, double height, Rect workArea)
    {
        if (double.IsNaN(left) || double.IsNaN(top) || width <= 0 || height <= 0)
        {
            return true;
        }

        return left < workArea.Left
            || top < workArea.Top
            || left + width > workArea.Right
            || top + height > workArea.Bottom;
    }

    public static Point GetCenteredPosition(double width, double height, Rect workArea)
    {
        double clampedWidth = Math.Min(width, workArea.Width);
        double clampedHeight = Math.Min(height, workArea.Height);

        double left = workArea.Left + ((workArea.Width - clampedWidth) / 2.0);
        double top = workArea.Top + ((workArea.Height - clampedHeight) / 2.0);
        return new Point(left, top);
    }
}
