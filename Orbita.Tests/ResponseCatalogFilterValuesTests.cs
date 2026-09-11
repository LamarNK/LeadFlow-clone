using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class ResponseCatalogFilterValuesTests
{
    [Fact]
    public void MergeIds_KeepsLegacySingleValueAndDeduplicates()
    {
        var first = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var second = Guid.Parse("22222222-2222-2222-2222-222222222222");

        var merged = ResponseCatalogFilterValues.MergeIds([first, first, second], first);

        Assert.Equal([first, second], merged);
    }

    [Fact]
    public void MergeValues_SplitsCommaSeparatedAndKeepsLegacySingle()
    {
        var merged = ResponseCatalogFilterValues.MergeValues(
            ["not_sent", "crm:aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"],
            "not_sent,bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

        Assert.Equal(
            [
                "not_sent",
                "crm:aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"
            ],
            merged);
    }
}
