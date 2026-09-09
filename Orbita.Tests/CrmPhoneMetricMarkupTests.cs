namespace Orbita.Tests;

public sealed class CrmPhoneMetricMarkupTests
{
    [Theory]
    [InlineData("Orbita.Web/Views/Crm/_CrmListTable.cshtml")]
    [InlineData("Orbita.Web/Views/Crm/_CrmTile.cshtml")]
    public void CrmCandidatePresentation_ShowsPhoneMetric(string relativePath)
    {
        var markup = File.ReadAllText(FindRepoFile(relativePath));

        Assert.Contains("card.PhoneMetricKind", markup, StringComparison.Ordinal);
        Assert.Contains("card.PreviousPhoneRaw", markup, StringComparison.Ordinal);
        Assert.Contains("crm-phone-metric--changed", markup, StringComparison.Ordinal);
    }

    private static string FindRepoFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Repository file not found: {relativePath}");
    }
}
