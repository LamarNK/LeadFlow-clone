using LeadFlow.Core.Services.Avito;
using Xunit;

namespace LeadFlow.Tests;

public sealed class CandidatesListPrepareResultTests
{
    [Fact]
    public void DeferredForPhone_SumsMaskedAndFailedCards()
    {
        var result = new CandidatesListPrepareResult(
            ScrollRounds: 5,
            PhoneRevealRounds: 12,
            DomItemCount: 180,
            CardsWithPhone: 50,
            PhonesReady: false,
            MaskedPhonesLeft: 130,
            PhoneRevealClicks: 25,
            PhoneRevealBudget: 25,
            PhoneRevealSuccesses: 20,
            PhoneRevealFailures: 2,
            PhoneRevealFailedCards: 5);

        Assert.Equal(135, result.DeferredForPhone);
    }

    [Fact]
    public void DeferredForPhone_ZeroWhenEverythingRevealed()
    {
        var result = new CandidatesListPrepareResult(
            ScrollRounds: 5,
            PhoneRevealRounds: 3,
            DomItemCount: 10,
            CardsWithPhone: 10,
            PhonesReady: true);

        Assert.Equal(0, result.DeferredForPhone);
    }

    [Fact]
    public void MarkPhoneRevealFailedScript_PassesIndexAndUsesStore()
    {
        var script = AvitoCandidatesPageScripts.BuildMarkPhoneRevealFailedScript(7);

        Assert.Contains("markPhoneRevealFailed(7)", script);
    }

    [Fact]
    public void ResetCandidateCollectionStateScript_ClearsFailedPhoneReveal()
    {
        var script = AvitoCandidatesPageScripts.BuildResetCandidateCollectionStateScript();

        Assert.Contains("state.failedPhoneReveal = {}", script);
    }

    [Fact]
    public void PhonesReadyProbeScript_CountsFailedSeparately()
    {
        var script = AvitoCandidatesPageScripts.BuildPhonesReadyProbeScript();

        Assert.Contains("hasFailedPhoneReveal(index)", script);
        Assert.Contains("failed++", script);
    }

    [Fact]
    public void RevealPickerScripts_UseRoundRobinOrdering()
    {
        Assert.Contains("orderedRevealIndexes(items)", AvitoCandidatesPageScripts.BuildFindMaskedPhoneTargetScript());
        Assert.Contains("orderedRevealIndexes(items)", AvitoCandidatesPageScripts.BuildRevealMaskedPhonesStepScript());
        Assert.Contains("orderedRevealIndexes(items)", AvitoCandidatesPageScripts.BuildFindContactsPopupTargetScript());
        Assert.Contains("orderedRevealIndexes(items)", AvitoCandidatesPageScripts.BuildRevealNextContactsPopupPhoneScript());
    }

    [Fact]
    public void RevealHelpers_RotateCursorOnFailureAndSuccess()
    {
        var helpersProbe = AvitoCandidatesPageScripts.BuildFindMaskedPhoneTargetScript();
        Assert.Contains("advanceRevealCursor()", helpersProbe);
        Assert.Contains("const orderedRevealIndexes = (items) =>", helpersProbe);
        Assert.Contains("getRevealCursor() % rest.length", helpersProbe);

        // Успешное popup-раскрытие тоже сдвигает курсор.
        var popupProbe = AvitoCandidatesPageScripts.BuildContactsPopupProbeScript(3, closeOnReady: true);
        Assert.Contains("advanceRevealCursor()", popupProbe);
    }

    [Fact]
    public void ResetStateScript_KeepsRevealCursorAcrossPasses()
    {
        var script = AvitoCandidatesPageScripts.BuildResetCandidateCollectionStateScript();

        Assert.DoesNotContain("revealCursor", script);
    }

    [Fact]
    public void ExtractionScript_DoesNotCountSkippedCardsAsMissingPhone()
    {
        var script = AvitoCandidatesPageScripts.BuildExtractionScript();

        Assert.Contains("item.domIndex >= 0 && !shouldSkipPhoneReveal(item.domIndex)", script);
        Assert.Contains("missingPhoneCount", script);
    }

    [Fact]
    public void ExtractionScriptForPuppeteer_KeepsMissingPhoneLogic()
    {
        var script = AvitoCandidatesPageScripts.BuildExtractionScriptForPuppeteer();

        Assert.Contains("item.domIndex >= 0 && !shouldSkipPhoneReveal(item.domIndex)", script);
        Assert.Contains("missingPhoneCount", script);
    }

    [Fact]
    public void RevealCursorHelpers_PersistAcrossPageReload()
    {
        var script = AvitoCandidatesPageScripts.BuildFindMaskedPhoneTargetScript();

        Assert.Contains("sessionStorage", script);
        Assert.Contains("lf.revealCursor.v1", script);
    }

    [Fact]
    public void AdvanceRevealCursorScript_AdvancesAndReportsCursor()
    {
        var script = AvitoCandidatesPageScripts.BuildAdvanceRevealCursorScript();

        Assert.Contains("advanceRevealCursor()", script);
        Assert.Contains("getRevealCursor()", script);
    }

    [Fact]
    public void PanelPhoneTargetScript_ReturnsTargetNameForVerification()
    {
        var script = AvitoCandidatesPageScripts.BuildFindPanelPhoneTargetScript();

        Assert.Contains("orderedRevealIndexes(items)", script);
        Assert.Contains("hasFailedPhoneReveal(index)", script);
        Assert.Contains("needsPhoneReveal(items[index], index)", script);
        Assert.Contains("targetName", script);
    }

    [Fact]
    public void PanelPhoneRevealScript_ClicksShowPhoneInPanel()
    {
        var script = AvitoCandidatesPageScripts.BuildRevealPanelPhoneScript();

        Assert.Contains("job-application/call-button", script);
        Assert.Contains("показа(ть|ние)\\s+(номер|телефон)", script);
        Assert.Contains("humanClick", script);
    }

    [Fact]
    public void PanelPhoneProbeScript_VerifiesNameBeforeCaching()
    {
        var script = AvitoCandidatesPageScripts.BuildCandidatePanelPhoneProbeScript(
            5,
            System.Text.Json.JsonSerializer.Serialize("Швагерус Владимир Александрович"));

        Assert.Contains("nameMatch", script);
        // Имя передаётся JSON-литералом (кириллица экранируется как \uXXXX).
        Assert.Contains("const expectedName", script);
        Assert.Contains("\\u0428\\u0432", script);
        // Кэш и курсор — только после сверки имени.
        Assert.Contains("if (!nameMatch)", script);
        Assert.Contains("advanceRevealCursor()", script);
        Assert.Contains("readContactsPopupPhone()", script);
    }

    [Fact]
    public void PanelPhoneScripts_ExcludeListCardsFromPanelRoot()
    {
        Assert.Contains("!root.closest(\"[data-marker='job-application/item']\")", AvitoCandidatesPageScripts.BuildRevealPanelPhoneScript());
        Assert.Contains("!root.querySelector(\"[data-marker='job-application/item']\")", AvitoCandidatesPageScripts.BuildCandidatePanelPhoneProbeScript(1, "\"X\""));
    }

    [Fact]
    public void ClickItemButtonByMarkerScript_TargetsEnrichmentResultsButton()
    {
        var script = AvitoCandidatesPageScripts.BuildClickItemButtonByMarkerScript(
            3,
            System.Text.Json.JsonSerializer.Serialize("job-crm/response/enrichment-results-button"));

        Assert.Contains("job-crm/response/enrichment-results-button", script);
        Assert.Contains("querySelector(`[data-marker='${marker}']`)", script);
        Assert.Contains("no_button", script);
        // Кликовая логика — общий humanClick из хелперов, без дублирования.
        Assert.Contains("humanClick(target)", script);
    }

    [Fact]
    public void PrepareResult_CarriesPanelPhoneCounters()
    {
        var result = new CandidatesListPrepareResult(
            ScrollRounds: 3,
            PhoneRevealRounds: 4,
            DomItemCount: 40,
            CardsWithPhone: 30,
            PhonesReady: false,
            PanelPhoneClicks: 12,
            PanelPhoneSuccesses: 10,
            PanelPhoneFailures: 2);

        Assert.Equal(12, result.PanelPhoneClicks);
        Assert.Equal(10, result.PanelPhoneSuccesses);
        Assert.Equal(2, result.PanelPhoneFailures);
    }
}
