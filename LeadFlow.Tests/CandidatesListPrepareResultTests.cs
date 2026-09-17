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
}
