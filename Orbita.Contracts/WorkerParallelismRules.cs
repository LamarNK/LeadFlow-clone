namespace Orbita.Contracts;

/// <summary>
/// Limits the number of simultaneously running AdsPower browsers using the
/// worker's installed RAM. The estimate reserves 1,500 MB per browser
/// instance.
/// </summary>
public static class WorkerParallelismRules
{
    public const long RamMbPerBrowser = 1_500;

    /// <summary>
    /// Returns the maximum number of browser instances that fit in the
    /// supplied total memory, or <c>null</c> until the worker reports its RAM.
    /// </summary>
    public static int? GetMaximumConcurrentAccounts(long? totalRamMb)
    {
        if (totalRamMb is not > 0)
        {
            return null;
        }

        var capacity = totalRamMb.Value / RamMbPerBrowser;
        return (int)Math.Clamp(capacity, 1, int.MaxValue);
    }
}
