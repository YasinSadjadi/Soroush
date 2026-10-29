namespace Soroush.Persistence;

internal static class FailureCleanup
{
    // Preserve the primary exception and stack; make secondary observer/resource failures inspectable.
    internal static async Task CaptureAsync(Exception primary, Func<Task> cleanup)
    {
        try { await cleanup().ConfigureAwait(false); }
        catch (Exception secondary)
        {
            const string key = "Soroush.Persistence.SecondaryExceptions";
            if (!(primary.Data[key] is List<Exception> failures))
                primary.Data[key] = failures = new List<Exception>();
            failures.Add(secondary);
        }
    }
}
