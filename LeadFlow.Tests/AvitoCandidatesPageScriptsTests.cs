using LeadFlow.Core.Services.Avito;
using Xunit;

namespace LeadFlow.Tests;

public sealed class AvitoCandidatesPageScriptsTests
{
    [Fact]
    public void BuildFirewallProbeScript_DetectsStaticIpBlockPage()
    {
        var script = AvitoCandidatesPageScripts.BuildFirewallProbeScript();

        Assert.Contains("location.hash === \"#block\"", script, StringComparison.Ordinal);
        Assert.Contains("support.avito.ru/request/720", script, StringComparison.Ordinal);
        Assert.Contains("Отключить\\s+VPN", script, StringComparison.Ordinal);
        Assert.Contains("hasFirewallText && hasCaptchaWidget", script, StringComparison.Ordinal);
        Assert.Contains("const hasCaptchaChallenge", script, StringComparison.Ordinal);
        Assert.Contains("const hasIpBlock", script, StringComparison.Ordinal);
        Assert.Contains("!hasCaptchaChallenge && (hasIpText || hasStaticIpBlock)", script, StringComparison.Ordinal);
    }

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
    public void BuildScrollAndCollectMiniMessengerMessagesScript_PrefersOverlayRootOverFirstList()
    {
        var script = AvitoCandidatesPageScripts.BuildScrollAndCollectMiniMessengerMessagesScript();

        Assert.Contains("mini-messenger/messenger-page-link", script, StringComparison.Ordinal);
        Assert.Contains("channel-module-root", script, StringComparison.Ordinal);
        Assert.Contains("no_active_candidate_messenger", script, StringComparison.Ordinal);
        Assert.Contains("__leadflowMessengerBeforeMarker", script, StringComparison.Ordinal);
        Assert.Contains("[data-marker='platformMessage/text']", script, StringComparison.Ordinal);
        Assert.Contains("[data-marker='messageChunk']", script, StringComparison.Ordinal);
        Assert.Contains("const root = miniRoot", script, StringComparison.Ordinal);
        Assert.DoesNotContain("(!marker ? miniRoot : null)", script, StringComparison.Ordinal);
        Assert.Contains("const diagnostics", script, StringComparison.Ordinal);
        Assert.Contains("hasMiniLink", script, StringComparison.Ordinal);
        Assert.Contains("historyCount", script, StringComparison.Ordinal);
        Assert.Contains("rootMessageNodeCount", script, StringComparison.Ordinal);
        Assert.Contains("no_message_text", script, StringComparison.Ordinal);
        Assert.Contains("textContent", script, StringComparison.Ordinal);
        Assert.Contains("collectFrom", script, StringComparison.Ordinal);
        Assert.DoesNotContain("collectFrom(document)", script, StringComparison.Ordinal);
        Assert.Contains("behavior: \"auto\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("behavior: \"smooth\"", script, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildMessengerUiVisibleExpression_WaitsForCandidateOverlayInsteadOfGlobalHistory()
    {
        var script = AvitoCandidatesPageScripts.BuildMessengerUiVisibleExpression();

        Assert.Contains("__leadflowMessengerBeforeMarker", script, StringComparison.Ordinal);
        Assert.Contains("data-leadflow-messenger-before", script, StringComparison.Ordinal);
        Assert.Contains("mini-messenger/messenger-page-link", script, StringComparison.Ordinal);
        Assert.Contains("channel-module-root", script, StringComparison.Ordinal);
        Assert.Contains("[data-marker='messagesHistory']", script, StringComparison.Ordinal);
        Assert.Contains("isVisible", script, StringComparison.Ordinal);
        Assert.DoesNotContain("!!document.querySelector(\"[data-marker='messagesHistory/list']\")", script, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildMarkMessengerRootsBeforeOpenScript_MarksOnlyExistingVisibleHistories()
    {
        var script = AvitoCandidatesPageScripts.BuildMarkMessengerRootsBeforeOpenScript();

        Assert.Contains("data-leadflow-messenger-before", script, StringComparison.Ordinal);
        Assert.Contains("__leadflowMessengerBeforeMarker", script, StringComparison.Ordinal);
        Assert.Contains("[data-marker='messagesHistory']", script, StringComparison.Ordinal);
        Assert.Contains("isHistoryVisible", script, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildMessengerMessagesPresentExpression_LooksInsideMiniRoot()
    {
        var script = AvitoCandidatesPageScripts.BuildMessengerMessagesPresentExpression();

        Assert.Contains("[data-marker='message']", script, StringComparison.Ordinal);
        Assert.Contains("__leadflowMessengerBeforeMarker", script, StringComparison.Ordinal);
        Assert.Contains("data-leadflow-messenger-before", script, StringComparison.Ordinal);
        Assert.Contains("mini-messenger/messenger-page-link", script, StringComparison.Ordinal);
        Assert.Contains("channel-module-root", script, StringComparison.Ordinal);
        Assert.Contains("[data-marker='messagesHistory']", script, StringComparison.Ordinal);
        Assert.DoesNotContain("return hasMessage(document);", script, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildResolveMessengerChannelUrlExpression_PrefersVisibleMiniLink()
    {
        var script = AvitoCandidatesPageScripts.BuildResolveMessengerChannelUrlExpression();

        Assert.Contains("querySelectorAll(\"a[data-marker='mini-messenger/messenger-page-link']\")", script, StringComparison.Ordinal);
        Assert.Contains("channel-module-root", script, StringComparison.Ordinal);
        Assert.Contains("[data-marker='messagesHistory']", script, StringComparison.Ordinal);
        Assert.DoesNotContain("openedHistory.contains(link)", script, StringComparison.Ordinal);
        Assert.Contains("/\\/profile\\/messenger\\/channel\\/", script, StringComparison.Ordinal);
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

    [Fact]
    public void BuildRevealMaskedPhonesStepScript_ClicksMaskedEvenIfKnown()
    {
        var script = AvitoCandidatesPageScripts.BuildRevealMaskedPhonesStepScript();

        // Под маской всегда кликаем (phone-watch / смена временного номера).
        Assert.Contains("clearCachedPhone", script, StringComparison.Ordinal);
        Assert.Contains("/\\*/.test(raw)", script, StringComparison.Ordinal);
        // Цикл кликов не гейтится shouldSkip — только наличие «*» в тексте кнопки.
        Assert.Contains("masked++", script, StringComparison.Ordinal);
        Assert.Contains("humanClick", script, StringComparison.Ordinal);
        Assert.Contains("if (clicked > 0)", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ContactsPhoneHelpers_MaskedInvalidatesCacheAndNeedsReveal()
    {
        // Helpers вшиты в probe/extraction scripts.
        var script = AvitoCandidatesPageScripts.BuildPhonesReadyProbeScript();

        Assert.Contains("isMaskedPhoneText", script, StringComparison.Ordinal);
        Assert.Contains("clearCachedPhone", script, StringComparison.Ordinal);
        Assert.Contains("needsPhoneReveal", script, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildRevealNextContactsPopupPhoneScript_TargetsCallButtonAndRetriesLoadError()
    {
        var script = AvitoCandidatesPageScripts.BuildRevealNextContactsPopupPhoneScript();

        Assert.Contains("[data-marker='job-application/call-button']", script, StringComparison.Ordinal);
        Assert.Contains("job-application/response/contacts-popup/popup", script, StringComparison.Ordinal);
        Assert.Contains("временн", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("не\\s+удалось\\s+загрузить\\s+контактные\\s+данные", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("clickPhoneRevealTarget", script, StringComparison.Ordinal);
        Assert.DoesNotContain("waitForContactsPopup", script, StringComparison.Ordinal);
        Assert.DoesNotContain("while (Date.now() < sliceEnd)", script, StringComparison.Ordinal);
        Assert.DoesNotContain("maxRetries = 3", script, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildContactsPopupProbeScript_StoresRevealedPhoneAndCloses()
    {
        var script = AvitoCandidatesPageScripts.BuildContactsPopupProbeScript(3);

        Assert.Contains("__leadflowRevealedPhones", script, StringComparison.Ordinal);
        Assert.Contains("snapshotContactsPopupState", script, StringComparison.Ordinal);
        Assert.Contains("const index = 3;", script, StringComparison.Ordinal);
        Assert.Contains("временн", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("не\\s+удалось\\s+загрузить\\s+контактные\\s+данные", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("while (Date.now() < sliceEnd)", script, StringComparison.Ordinal);
    }

    [Fact]
    public void PhoneHelpers_UsePointerMouseGestureInsteadOfBusyWait()
    {
        var script = AvitoCandidatesPageScripts.BuildPhonesReadyProbeScript();

        Assert.Contains("humanClick", script, StringComparison.Ordinal);
        Assert.Contains("pointerdown", script, StringComparison.Ordinal);
        Assert.DoesNotContain("waitForContactsPopup", script, StringComparison.Ordinal);
        Assert.DoesNotContain("while (Date.now() < sliceEnd)", script, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildScrollStepScript_UsesPartialViewportAndSmoothBehavior()
    {
        var script = AvitoCandidatesPageScripts.BuildScrollStepScript();

        Assert.Contains("scrollBy", script, StringComparison.Ordinal);
        Assert.Contains("behavior: \"smooth\"", script, StringComparison.Ordinal);
        Assert.Contains("Math.random()", script, StringComparison.Ordinal);
        Assert.DoesNotContain("clientHeight * 0.9", script, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildScrollBackScript_ScrollsUp()
    {
        var script = AvitoCandidatesPageScripts.BuildScrollBackScript();

        Assert.Contains("scrollBy", script, StringComparison.Ordinal);
        Assert.Contains("behavior: \"smooth\"", script, StringComparison.Ordinal);
        Assert.Contains("delta = -Math.max", script, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildClickCandidateItemByIndexScript_DispatchesPointerGesture()
    {
        var script = AvitoCandidatesPageScripts.BuildClickCandidateItemByIndexScript(1);

        Assert.Contains("pointerdown", script, StringComparison.Ordinal);
        Assert.Contains("mousedown", script, StringComparison.Ordinal);
        Assert.Contains("mouseup", script, StringComparison.Ordinal);
        Assert.DoesNotContain("item.click()", script, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPhonesReadyProbeScript_UsesRevealedPhoneCache()
    {
        var script = AvitoCandidatesPageScripts.BuildPhonesReadyProbeScript();

        Assert.Contains("__leadflowRevealedPhones", script, StringComparison.Ordinal);
        Assert.Contains("needsPhoneReveal", script, StringComparison.Ordinal);
        Assert.Contains("readItemPhone", script, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildExtractionScript_ReadsPhoneFromRevealedCache()
    {
        var script = AvitoCandidatesPageScripts.BuildExtractionScript();

        Assert.Contains("readItemPhone(root, rootIndex)", script, StringComparison.Ordinal);
        Assert.Contains("__leadflowRevealedPhones", script, StringComparison.Ordinal);
    }

    [Fact]
    public void VacancyAndCityScripts_SupportQuotedAndLinkedVacancyLines()
    {
        var scripts = new[]
        {
            AvitoCandidatesPageScripts.BuildCollectListItemSkipKeysScript(),
            AvitoCandidatesPageScripts.BuildCollectListItemCardFingerprintsScript(),
            AvitoCandidatesPageScripts.BuildExtractionScript(),
            AvitoCandidatesPageScripts.BuildReadDetailPanelScript()
        };

        Assert.All(scripts, script =>
        {
            Assert.Contains("parseVacancyLineFromText", script, StringComparison.Ordinal);
            Assert.Contains("parseVacancyAndCityFromRoot", script, StringComparison.Ordinal);
            Assert.Contains("на\\s+вакансию", script, StringComparison.Ordinal);
            Assert.Contains("stripVacancyQuotes", script, StringComparison.Ordinal);
        });
    }
}
