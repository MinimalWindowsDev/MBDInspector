namespace MBDInspector;

public static class UiInitializationGuard
{
    public static bool AreReady(params object?[] controls)
    {
        foreach (object? control in controls)
        {
            if (control is null)
            {
                return false;
            }
        }

        return true;
    }
}
