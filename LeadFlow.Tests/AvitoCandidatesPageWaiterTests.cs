using LeadFlow.Core.Services.Avito;
using Xunit;

namespace LeadFlow.Tests;

public sealed class AvitoCandidatesPageWaiterTests
{
    [Fact]
    public async Task WaitsUntilListSignatureChangesFromBaseline()
    {
        var poll = 0;
        var probes = new[]
        {
            ProbeJson("2|Иван@Новый", loading: false),
            ProbeJson("3|Пётр@Новый", loading: false),
            ProbeJson("3|Пётр@Новый", loading: false),
            ProbeJson("3|Пётр@Новый", loading: false),
        };

        await AvitoCandidatesPageWaiter.WaitForCandidatesOrThrowFirewallAsync(
            (_, _) =>
            {
                var idx = Math.Min(poll, probes.Length - 1);
                poll++;
                return Task.FromResult(probes[idx]);
            },
            null,
            "https://www.avito.ru/profile/candidates",
            CancellationToken.None,
            baselineListSignature: "2|Иван@Новый");

        Assert.True(poll >= 4);
    }

    [Fact]
    public async Task WaitsForStableEmptyListWithoutBaseline()
    {
        var poll = 0;
        var probes = new[]
        {
            ProbeJson("0|", loading: true, contentReady: false),
            ProbeJson("0|", loading: false, contentReady: true, emptyConfirmed: true),
            ProbeJson("0|", loading: false, contentReady: true, emptyConfirmed: true),
            ProbeJson("0|", loading: false, contentReady: true, emptyConfirmed: true),
        };

        await AvitoCandidatesPageWaiter.WaitForCandidatesOrThrowFirewallAsync(
            (_, _) =>
            {
                var idx = Math.Min(poll, probes.Length - 1);
                poll++;
                return Task.FromResult(probes[idx]);
            },
            null,
            "https://www.avito.ru/profile/candidates",
            CancellationToken.None);

        Assert.True(poll >= 4);
    }

    [Fact]
    public async Task AcceptsStableEmptyListWhenBaselineSignatureMatches()
    {
        var poll = 0;
        var probes = new[]
        {
            ProbeJson("0|", loading: false, contentReady: true),
            ProbeJson("0|", loading: false, contentReady: true),
            ProbeJson("0|", loading: false, contentReady: true),
        };

        await AvitoCandidatesPageWaiter.WaitForCandidatesOrThrowFirewallAsync(
            (_, _) =>
            {
                var idx = Math.Min(poll, probes.Length - 1);
                poll++;
                return Task.FromResult(probes[idx]);
            },
            null,
            "https://www.avito.ru/profile/candidates",
            CancellationToken.None,
            baselineListSignature: "0|");

        Assert.True(poll >= 3);
    }

    [Fact]
    public async Task KeepsPollingWhileBaselineSignatureIsUnchanged()
    {
        var same = ProbeJson("1|Старый@", loading: false);
        var calls = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AvitoCandidatesPageWaiter.WaitForCandidatesOrThrowFirewallAsync(
                (_, _) =>
                {
                    calls++;
                    return Task.FromResult(same);
                },
                null,
                "https://www.avito.ru/profile/candidates",
                cts.Token,
                baselineListSignature: "1|Старый@"));

        Assert.True(calls >= 3);
    }

    private static string ProbeJson(
        string listSignature,
        bool loading,
        bool contentReady = true,
        bool emptyConfirmed = false)
    {
        var itemCount = listSignature.StartsWith("0|", StringComparison.Ordinal) ? 0 : 1;
        return $$"""
                   {"readyState":"complete","itemCount":{{itemCount}},"statusCount":{{itemCount}},"loading":{{loading.ToString().ToLowerInvariant()}},"listSignature":"{{listSignature}}","emptyConfirmed":{{emptyConfirmed.ToString().ToLowerInvariant()}},"blocked":false,"contentReady":{{contentReady.ToString().ToLowerInvariant()}}}
                   """;
    }
}