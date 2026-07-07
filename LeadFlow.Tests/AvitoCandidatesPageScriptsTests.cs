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
        Assert.Contains("download-report-button", script, StringComparison.Ordinal);
        Assert.DoesNotContain("[class*='overlay']", script, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSendMiniMessengerReplyScript_TargetsReplyInput()
    {
        var script = AvitoCandidatesPageScripts.BuildSendMiniMessengerReplyScript("Hello");

        Assert.Contains("[data-marker='reply/input']", script, StringComparison.Ordinal);
        Assert.Contains("\"Hello\"", script, StringComparison.Ordinal);
        Assert.Contains("reply/send", script, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildIsJobCrmResponsesPageScript_DetectsCrmMarkers()
    {
        var script = AvitoCandidatesPageScripts.BuildIsJobCrmResponsesPageScript();

        Assert.Contains("filters/status-list-content", script, StringComparison.Ordinal);
        Assert.Contains("download-report-button/download", script, StringComparison.Ordinal);
        Assert.Contains("job-crm/response/cv-button", script, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildExtractionScriptForPuppeteer_IncludesDomIndex()
    {
        var script = AvitoCandidatesPageScripts.BuildExtractionScriptForPuppeteer();

        Assert.Contains("domIndex: rootIndex", script, StringComparison.Ordinal);
        Assert.Contains("listItems.indexOf(root)", script, StringComparison.Ordinal);
    }
}