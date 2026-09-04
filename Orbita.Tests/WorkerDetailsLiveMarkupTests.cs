namespace Orbita.Tests;

public sealed class WorkerDetailsLiveMarkupTests
{
    [Fact]
    public void WorkerLiveRenderer_PreservesTopUpTriggerAndInitializesIt()
    {
        var js = ReadRepoFile("Orbita.Web/wwwroot/js/orbita-worker.js");
        var renderer = js[
            js.IndexOf("function renderWorkerAccounts", StringComparison.Ordinal)..
            js.IndexOf("function activityPillExtras", StringComparison.Ordinal)];

        Assert.Contains("data-topup-trigger", renderer);
        Assert.Contains("account.canTopUp", renderer);
        Assert.Contains("initTopUpModal();", renderer);
    }

    private static string ReadRepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException(relativePath);
    }
}
