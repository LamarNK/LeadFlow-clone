using LeadFlow.Core.Services.Avito;
using Xunit;

namespace LeadFlow.Tests;

public sealed class AvitoCandidatesPageScriptsTests
{
    [Fact]
    public void BuildClickCandidateChatByIndexScript_UsesJobApplicationItemIndex()
    {
        var script = AvitoCandidatesPageScripts.BuildClickCandidateChatByIndexScript(2);

        Assert.Contains("[data-marker='job-application/item']", script, StringComparison.Ordinal);
        Assert.Contains("[data-marker='job-application/link/to-chat']", script, StringComparison.Ordinal);
        Assert.Contains("const idx = 2;", script, StringComparison.Ordinal);
        Assert.DoesNotContain("job-application/phone", script, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildDismissCandidateDetailPanelScript_DispatchesEscape()
    {
        var script = AvitoCandidatesPageScripts.BuildDismissCandidateDetailPanelScript();

        Assert.Contains("Escape", script, StringComparison.Ordinal);
        Assert.Contains("styles-module-response", script, StringComparison.Ordinal);
    }
}