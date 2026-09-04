namespace Orbita.Tests;

public sealed class WorkerDetailsLiveMarkupTests
{
    [Fact]
    public void WorkerLiveRenderer_PlacesTopUpTriggerOnTheSelectedSubProfile()
    {
        var workerJs = ReadRepoFile("Orbita.Web/wwwroot/js/orbita-worker.js");
        var sharedJs = ReadRepoFile("Orbita.Web/wwwroot/js/orbita-live-shared.js");

        Assert.Contains("data-topup-trigger", workerJs);
        Assert.Contains("data-subprofile-id", workerJs);
        Assert.Contains("startSession(workerId, accountId, subProfileId, accountName);", workerJs);
        Assert.DoesNotContain("account.canTopUp", workerJs);
        Assert.Contains("layout === 'worker' && sub.canTopUp", sharedJs);
        Assert.Contains("canTopUp: !!(sub.canTopUp || sub.CanTopUp)", sharedJs);
        Assert.Contains("data-subprofile-id=\"' + escapeHtml(sub.id)", sharedJs);
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
