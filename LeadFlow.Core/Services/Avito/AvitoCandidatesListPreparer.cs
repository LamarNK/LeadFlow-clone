using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using LeadFlow.Core.Logging.Audit;
using LeadFlow.Core.Services;
using LeadFlow.Core.Services.Worker;
using Orbita.Contracts;

namespace LeadFlow.Core.Services.Avito;

/// <summary>
/// Перед снятием JSON: прокрутка бесконечного списка откликов и раскрытие телефонов (клик по кнопке с маской «**»).
/// </summary>
public static class AvitoCandidatesListPreparer
{
    private const int SnapshotBatchSize = 5;
    private const int StableRoundsRequired = 3;
    private const int MaxConsecutivePhoneRevealStalls = 3;
    private const int MaxDetailEnrichClicks = 40;

    public static async Task<CandidatesListPrepareResult> PrepareAsync(
        Func<string, CancellationToken, Task<string>> executeScript,
        string logContext,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<string?>>? fetchHtmlSnapshot = null,
        string? pageUrl = null,
        Func<IReadOnlyCollection<string>, CancellationToken, Task<IReadOnlySet<string>>>? resolveExistingSourceResponseIdsAsync = null,
        Func<IReadOnlyCollection<string>, CancellationToken, Task<IReadOnlySet<string>>>? resolveExistingCardFingerprintsAsync = null,
        Func<IReadOnlyCollection<string>, CancellationToken, Task<IReadOnlySet<string>>>? resolveExistingPhonesAsync = null,
        Func<IReadOnlyList<CandidateLookupProfileDto>, CancellationToken, Task<IReadOnlySet<int>>>? resolveExistingMatchedProfileIndicesAsync = null,
        ResponseCollectionFilters? responseCollectionFilters = null,
        Func<string, CancellationToken, Task<bool>>? isOpenPhoneWatchAsync = null,
        bool skipDetailEnrich = false,
        Func<AvitoFirewallProbe.Detection, string?, CancellationToken, Task<bool>>? trySolveCaptchaAsync = null,
        IReadOnlyCollection<WorkerOpenPhoneWatchDto>? openPhoneWatches = null,
        CandidatesPageActors? actors = null,
        AvitoAccountPassBudget? passBudget = null,
        Func<CancellationToken, Task>? onCandidatesSnapshotAsync = null)
    {
        var totalSw = Stopwatch.StartNew();
        var firewallSw = Stopwatch.StartNew();
        await AvitoFirewallProbe.ThrowIfBlockedAsync(
                executeScript,
                fetchHtmlSnapshot,
                pageUrl,
                cancellationToken,
                trySolveCaptchaAsync)
            .ConfigureAwait(false);
        firewallSw.Stop();

        _ = await executeScript(
                AvitoCandidatesPageScripts.BuildResetCandidateCollectionStateScript(),
                cancellationToken)
            .ConfigureAwait(false);

        var lastCount = -1;
        var stableRounds = 0;
        var scrollRounds = 0;
        var stoppedOnKnownHistory = false;
        var scrollStepCalls = 0;
        var scrollProfileProbeCalls = 0;
        var scrollFingerprintProbeCalls = 0;
        var scrollFullRescans = 0;
        var scrollFallbackRescans = 0;
        var jsScrollFallbacks = 0;
        var phoneWatchNameKeys = (openPhoneWatches ?? [])
            .Select(static x => ResponsePhoneWatchEvaluator.BuildFullNameKey(x.FullName))
            .Where(static x => !string.IsNullOrWhiteSpace(x))
            .ToArray();
        var incrementalState = new CandidatesIncrementalScrollState(phoneWatchNameKeys);
        var maxScrollRounds = CandidatesScrollBudget.Resolve(incrementalState.HasRemainingPhoneWatch);

        var scrollSw = Stopwatch.StartNew();
        for (var round = 0; round < maxScrollRounds; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            scrollRounds++;

            if (round > 0 && round % 3 == 0)
            {
                await AvitoFirewallProbe.ThrowIfBlockedAsync(
                        executeScript,
                        fetchHtmlSnapshot,
                        pageUrl,
                        cancellationToken,
                        trySolveCaptchaAsync)
                    .ConfigureAwait(false);
            }

            var previousCount = lastCount < 0 ? 0 : lastCount;
            scrollStepCalls++;
            var wheelStep = await TryWheelScrollStepAsync(executeScript, actors, previousCount, cancellationToken)
                .ConfigureAwait(false);
            AvitoScrollStepProbe step;
            if (wheelStep.WheelUsed && wheelStep.Step is not null)
            {
                step = wheelStep.Step;
            }
            else
            {
                if (actors?.WheelScrollAsync is not null)
                {
                    jsScrollFallbacks++;
                }

                step = await TryParseScrollStepAsync(executeScript, previousCount, cancellationToken).ConfigureAwait(false);
            }

            var count = step.ItemCount;

            // Один incremental-пакет обслуживает и phone-watch, и known-history.
            // При некорректном/неполном пакете консервативно перечитываем весь DOM.
            IReadOnlyList<ListItemProfileKeys> loadedProfiles = step.NewItems
                .Select(static item => new ListItemProfileKeys(
                    item.Index, item.FullName, item.CardFingerprint, item.City, item.Age, item.Gender, item.PhoneDigits))
                .ToArray();
            if (step.RequiresFallbackRescan)
            {
                scrollFallbackRescans++;
                scrollProfileProbeCalls++;
                loadedProfiles = await TryParseListItemProfilesAsync(executeScript, cancellationToken)
                    .ConfigureAwait(false);
                count = loadedProfiles.Count == 0 ? 0 : loadedProfiles.Max(static item => item.Index) + 1;
            }
            else if (step.IsFullRescan)
            {
                scrollFullRescans++;
            }
            incrementalState.ObserveNames(loadedProfiles.Select(static item => item.FullName).ToArray());

            if (count > previousCount
                && resolveExistingCardFingerprintsAsync is not null)
            {
                var knownHistoryProbe = await ProbeLoadedItemsKnownAsync(
                        loadedProfiles,
                        previousCount,
                        count,
                        resolveExistingCardFingerprintsAsync,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (incrementalState.ObserveKnownHistory(
                        knownHistoryProbe.AllKnown,
                        step.AllowEarlyStop))
                {
                    stoppedOnKnownHistory = true;
                    lastCount = count;
                    break;
                }
            }

            if (step.AllowEarlyStop && count == lastCount && (!step.Moved || step.AtEnd))
            {
                stableRounds++;
                if (stableRounds >= StableRoundsRequired)
                {
                    break;
                }
            }
            else
            {
                stableRounds = 0;
                lastCount = count;
            }

            if (round > 0
                && AvitoHumanVariation.RollPermille(MonitoringTiming.ScrollBackChancePermille))
            {
                if (!await TryWheelScrollBackAsync(executeScript, actors, cancellationToken).ConfigureAwait(false))
                {
                    _ = await executeScript(AvitoCandidatesPageScripts.BuildScrollBackScript(), cancellationToken)
                        .ConfigureAwait(false);
                }

                await HumanDelay.AfterListScrollAsync(cancellationToken).ConfigureAwait(false);
            }

            await HumanDelay.AfterListScrollAsync(cancellationToken).ConfigureAwait(false);
        }
        scrollSw.Stop();

        var postScrollSw = Stopwatch.StartNew();
        if (!await TryWheelScrollToTopAsync(executeScript, actors, cancellationToken).ConfigureAwait(false))
        {
            jsScrollFallbacks++;
            _ = await executeScript(AvitoCandidatesPageScripts.BuildScrollToTopScript(), cancellationToken)
                .ConfigureAwait(false);
        }
        await HumanDelay.AfterListScrollAsync(cancellationToken).ConfigureAwait(false);
        await HumanDelay.AfterListReadyAsync(cancellationToken).ConfigureAwait(false);
        postScrollSw.Stop();

        // Один финальный полный снимок после возврата наверх: актуальные DOM-индексы
        // переиспользуются при watch/skip/filter без повторного разбора списка.
        var finalListItems = await TryParseListItemProfilesAsync(executeScript, cancellationToken)
            .ConfigureAwait(false);
        var domItems = finalListItems.Count;
        PhonesReadyProbe? phonesProbe = null;
        var emittedReadyPhoneCount = 0;

        async Task EmitCandidatesSnapshotAsync(bool force)
        {
            if (onCandidatesSnapshotAsync is null)
            {
                return;
            }

            var readyPhoneCount = phonesProbe?.WithPhone ?? 0;
            if (!force && readyPhoneCount - emittedReadyPhoneCount < SnapshotBatchSize)
            {
                return;
            }

            emittedReadyPhoneCount = readyPhoneCount;
            await onCandidatesSnapshotAsync(cancellationToken).ConfigureAwait(false);
        }

        phonesProbe = await TryParsePhonesReadyAsync(executeScript, cancellationToken).ConfigureAwait(false);
        await EmitCandidatesSnapshotAsync(force: true).ConfigureAwait(false);

        var phoneRevealRounds = 0;
        var phoneClicksTotal = 0;
        var phoneRevealSuccesses = 0;
        var phoneRevealFailures = 0;
        var phoneTargetChanges = 0;
        var phonePopupNameMismatches = 0;
        var panelPhoneClicks = 0;
        var panelPhoneSuccesses = 0;
        var panelPhoneFailures = 0;
        var panelFailReasons = new Dictionary<string, int>(StringComparer.Ordinal);
        var prioritySw = Stopwatch.StartNew();
        var openWatchProtected = await ResolveOpenPhoneWatchProtectedIndicesAsync(
                finalListItems,
                isOpenPhoneWatchAsync,
                (openPhoneWatches ?? [])
                    .Select(static x => ResponsePhoneWatchEvaluator.BuildFullNameKey(x.FullName))
                    .Where(static x => !string.IsNullOrWhiteSpace(x))
                    .ToHashSet(StringComparer.Ordinal),
                cancellationToken)
            .ConfigureAwait(false);
        _ = await executeScript(
                AvitoCandidatesPageScripts.BuildApplyPhoneWatchPriorityScript(openWatchProtected),
                cancellationToken)
            .ConfigureAwait(false);

        var cardFingerprintSkipCount = await TryApplyKnownCardFingerprintSkipsAsync(
                executeScript,
                finalListItems,
                resolveExistingCardFingerprintsAsync,
                openWatchProtected,
                cancellationToken)
            .ConfigureAwait(false);
        var phoneSkipCount = await TryApplyKnownPhoneSkipsAsync(
                executeScript,
                finalListItems,
                resolveExistingPhonesAsync,
                openWatchProtected,
                cancellationToken)
            .ConfigureAwait(false);
        var profileSkipCount = await TryApplyKnownProfileSkipsAsync(
                executeScript,
                finalListItems,
                resolveExistingMatchedProfileIndicesAsync,
                openWatchProtected,
                cancellationToken)
            .ConfigureAwait(false);
        var collectionFilterSkipCount = await TryApplyResponseCollectionFilterSkipsAsync(
                executeScript,
                finalListItems,
                responseCollectionFilters,
                cancellationToken)
            .ConfigureAwait(false);
        prioritySw.Stop();
        var detailEnrichClicks = 0;
        var detailEnrichSkipped = 0;
        var detailEnrichHits = 0;
        long phoneRevealMs = 0;
        long detailEnrichMs = 0;
        var pageDetection = await TryDetectJobCrmResponsesPageAsync(executeScript, cancellationToken)
            .ConfigureAwait(false);
        var isJobCrmPage = pageDetection.IsJobCrm;
        var pageVariant = pageDetection.PageVariant;
        async Task RunDetailEnrichmentAsync()
        {
            var detailEnrichSw = Stopwatch.StartNew();
            try
            {
                if (domItems > 0 && !isJobCrmPage && !skipDetailEnrich)
                {
                    var enrichment = await TryCollectDetailEnrichmentAsync(
                            executeScript,
                            Math.Min(domItems, MaxDetailEnrichClicks),
                            resolveExistingSourceResponseIdsAsync,
                            resolveExistingPhonesAsync,
                            actors,
                            cancellationToken)
                        .ConfigureAwait(false);
                    detailEnrichClicks = enrichment.Clicks;
                    detailEnrichSkipped = enrichment.Skipped;
                    detailEnrichHits = enrichment.Hits;
                    if (enrichment.Entries.Count > 0)
                    {
                        var enrichmentJson = JsonSerializer.Serialize(enrichment.Entries);
                        _ = await executeScript(
                                AvitoCandidatesPageScripts.BuildApplyDetailEnrichmentScript(enrichmentJson),
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                detailEnrichSw.Stop();
                detailEnrichMs = detailEnrichSw.ElapsedMilliseconds;
            }
        }

        async Task RunPhoneRevealAsync()
        {
            var phoneRevealSw = Stopwatch.StartNew();
            try
            {
                var lastPendingCount = phonesProbe?.Masked ?? 0;
                var lastWithPhone = phonesProbe?.WithPhone ?? 0;
                var lastFailed = phonesProbe?.Failed ?? 0;
                var consecutiveStalls = 0;

                while (phonesProbe is not { Ready: true } and not { Items: 0 })
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    passBudget?.RecordPhoneRevealAttempts();

                    phoneRevealRounds++;

                    var revealStep = await TryRevealMaskedPhonesAsync(executeScript, actors, cancellationToken).ConfigureAwait(false);
                    if (revealStep is { Attempted: true })
                    {
                        if (revealStep.Clicked > 0)
                        {
                            phoneClicksTotal += revealStep.Clicked;
                            await HumanDelay.AfterPhoneRevealClickAsync(cancellationToken).ConfigureAwait(false);
                        }
                        else
                        {
                            // Attempted=true с Clicked=0 — клик мог уйти до CDP-сбоя
                            // (после-проба не смогла подтвердить). Исход неопределён:
                            // резерв НЕ возвращаем.
                            await HumanDelay.AfterPhoneRevealOutcomeAsync(false, cancellationToken).ConfigureAwait(false);
                        }

                        phonesProbe = await TryParsePhonesReadyAsync(executeScript, cancellationToken).ConfigureAwait(false);
                        if (phonesProbe is not null)
                        {
                            var madeProgress = phonesProbe.WithPhone > lastWithPhone
                                || phonesProbe.Masked < lastPendingCount
                                || phonesProbe.Failed > lastFailed;
                            if (phonesProbe.WithPhone > lastWithPhone)
                            {
                                phoneRevealSuccesses++;
                                // Inline-раскрытие не проходит через popup-проб:
                                // двигаем round-robin курсор явно.
                                await AdvanceRevealCursorAsync(executeScript, cancellationToken)
                                    .ConfigureAwait(false);
                            }
                            else if (revealStep.Clicked > 0)
                            {
                                phoneRevealFailures++;
                                await MarkPhoneRevealFailedAsync(executeScript, revealStep.ClickedIndex, cancellationToken)
                                    .ConfigureAwait(false);
                            }

                            consecutiveStalls = madeProgress ? 0 : consecutiveStalls + 1;
                            lastPendingCount = phonesProbe.Masked;
                            lastWithPhone = phonesProbe.WithPhone;
                            lastFailed = phonesProbe.Failed;
                            await EmitCandidatesSnapshotAsync(force: false).ConfigureAwait(false);
                        }
                        else
                        {
                            consecutiveStalls++;
                        }

                        if (phonesProbe?.Ready == true
                            || phonesProbe?.Items == 0
                            || consecutiveStalls >= MaxConsecutivePhoneRevealStalls)
                        {
                            break;
                        }

                        continue;
                    }

                    var popupStep = await TryRevealNextContactsPopupPhoneAsync(executeScript, actors, cancellationToken)
                        .ConfigureAwait(false);
                    if (popupStep?.Clicked == true)
                    {
                        phoneClicksTotal++;
                        await HumanDelay.AfterPhoneRevealOutcomeAsync(popupStep.Revealed, cancellationToken)
                            .ConfigureAwait(false);
                        if (popupStep.Revealed)
                        {
                            phoneRevealSuccesses++;
                        }
                        else
                        {
                            phoneRevealFailures++;
                            if (popupStep.Reason == "target_changed") phoneTargetChanges++;
                            else
                            {
                                if (popupStep.Reason == "name_mismatch") phonePopupNameMismatches++;
                                await MarkPhoneRevealFailedAsync(executeScript, popupStep.Index, cancellationToken)
                                    .ConfigureAwait(false);
                            }
                        }
                    }
                    else if (popupStep is null || popupStep.Index < 0)
                    {
                        // Клика точно не было: не учитываем эту итерацию в телеметрии.
                        passBudget?.UnrecordPhoneRevealAttempts();
                    }

                    phonesProbe = await TryParsePhonesReadyAsync(executeScript, cancellationToken).ConfigureAwait(false);
                    if (phonesProbe is not null)
                    {
                        var madeProgress = phonesProbe.WithPhone > lastWithPhone
                            || phonesProbe.Masked < lastPendingCount
                            || phonesProbe.Failed > lastFailed;
                        consecutiveStalls = madeProgress ? 0 : consecutiveStalls + 1;
                        lastPendingCount = phonesProbe.Masked;
                        lastWithPhone = phonesProbe.WithPhone;
                        lastFailed = phonesProbe.Failed;
                        await EmitCandidatesSnapshotAsync(force: false).ConfigureAwait(false);
                    }
                    else
                    {
                        consecutiveStalls++;
                    }

                    if (phonesProbe?.Ready == true
                        || phonesProbe?.Items == 0
                        || consecutiveStalls >= MaxConsecutivePhoneRevealStalls)
                    {
                        break;
                    }

                    if (revealStep?.Masked == 0 && popupStep?.Pending == 0)
                    {
                        await HumanDelay.DelayAsync(280, 520, cancellationToken).ConfigureAwait(false);
                    }
                    if (revealStep?.Masked == 0 && popupStep is { Clicked: false, Index: < 0 })
                    {
                        // Есть карточки без номера, но в списке нет доступной
                        // кнопки контактов. Следующий этап проверит их панели.
                        break;
                    }
                }

                phonesProbe ??= await TryParsePhonesReadyAsync(executeScript, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                phoneRevealSw.Stop();
                phoneRevealMs = phoneRevealSw.ElapsedMilliseconds;
            }
        }

        /// <summary>
        /// Добор телефонов через панель «Данные о кандидате» для карточек, у которых
        /// номера нет в списке (новый UX Avito на CRM-страницах). Выполняется после
        /// основного reveal-цикла; каждая карточка: клик по строке → клик «Показать
        /// номер» в панели → чтение номера в кэш (со сверкой имени кандидата, чтобы
        /// не записать чужой номер) → закрытие панели.
        /// </summary>
        async Task RunPanelPhoneEnrichmentAsync()
        {
            var lastTargetChangedIndex = -1;
            var repeatedTargetChanges = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = await TryParsePanelPhoneTargetAsync(executeScript, cancellationToken).ConfigureAwait(false);
                if (target is null || target.Pending == 0 || target.TargetIndex < 0)
                {
                    break;
                }

                passBudget?.RecordPhoneRevealAttempts();
                phoneClicksTotal++;

                PanelPhoneOutcome? outcome = null;
                try
                {
                    panelPhoneClicks++;
                    outcome = await TryRevealPhoneViaPanelAsync(executeScript, actors, target, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // Ошибка одного клика не должна валить весь проход: карточка
                    // получит повторную попытку в следующем проходе.
                }

                if (outcome is { Success: true })
                {
                    panelPhoneSuccesses++;
                    phonesProbe = await TryParsePhonesReadyAsync(executeScript, cancellationToken)
                        .ConfigureAwait(false);
                    await EmitCandidatesSnapshotAsync(force: false).ConfigureAwait(false);
                }
                else
                {
                    panelPhoneFailures++;
                    panelFailReasons.TryGetValue(outcome?.Reason ?? "error", out var reasonCount);
                    panelFailReasons[outcome?.Reason ?? "error"] = reasonCount + 1;
                    if (outcome?.Reason == "target_changed")
                    {
                        repeatedTargetChanges = target.TargetIndex == lastTargetChangedIndex
                            ? repeatedTargetChanges + 1
                            : 1;
                        lastTargetChangedIndex = target.TargetIndex;
                        if (repeatedTargetChanges >= MaxConsecutivePhoneRevealStalls)
                        {
                            await MarkPhoneRevealFailedAsync(executeScript, target.TargetIndex, cancellationToken)
                                .ConfigureAwait(false);
                            repeatedTargetChanges = 0;
                        }
                    }
                    else
                    {
                        await MarkPhoneRevealFailedAsync(executeScript, target.TargetIndex, cancellationToken)
                            .ConfigureAwait(false);
                        repeatedTargetChanges = 0;
                    }
                }

                _ = await executeScript(
                        AvitoCandidatesPageScripts.BuildDismissCandidateDetailPanelScript(),
                        cancellationToken)
                    .ConfigureAwait(false);
                await HumanDelay.DelayAsync(280, 620, cancellationToken).ConfigureAwait(false);
            }

            if (panelPhoneClicks > 0)
            {
                phonesProbe = await TryParsePhonesReadyAsync(executeScript, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>Одна карточка: открыть панель, раскрыть и прочитать номер.</summary>
        async Task<PanelPhoneOutcome> TryRevealPhoneViaPanelAsync(
            Func<string, CancellationToken, Task<string>> executeScriptAction,
            CandidatesPageActors? panelActors,
            PanelPhoneTargetProbe target,
            CancellationToken ct)
        {
            async Task<bool> ClickRowAsync()
            {
                await HumanDelay.BeforeCandidateClickAsync(ct).ConfigureAwait(false);
                var rowClicked = panelActors is not null
                    && await panelActors.TryClickItemChildAsync(target.TargetIndex, null, ct).ConfigureAwait(false);
                if (rowClicked)
                {
                    return true;
                }

                var clickRaw = await executeScriptAction(
                        AvitoCandidatesPageScripts.BuildClickCandidateItemByIndexScript(target.TargetIndex),
                        ct)
                    .ConfigureAwait(false);
                return TryParseClickStep(clickRaw, out var clickOk) && clickOk;
            }

            async Task RunRevealScriptAsync()
            {
                var revealRaw = await executeScriptAction(
                        AvitoCandidatesPageScripts.BuildRevealPanelPhoneScript(),
                        ct)
                    .ConfigureAwait(false);
                if (TryParsePanelPhoneReveal(revealRaw) is { Clicked: true })
                {
                    await HumanDelay.AfterPhoneRevealClickAsync(ct).ConfigureAwait(false);
                }
            }

            var expectedNameJson = System.Text.Json.JsonSerializer.Serialize(target.TargetName);
            // Убираем старый popup до открытия панели. Анкета/enrichment-results
            // не является контактом; открываем только проверенную clickable-карточку.
            _ = await executeScriptAction(AvitoCandidatesPageScripts.BuildCloseContactsPopupScript(), ct).ConfigureAwait(false);
            if (!await ClickRowAsync().ConfigureAwait(false))
            {
                return new PanelPhoneOutcome(false, "row_click_failed");
            }

            var revealAttempted = false;
            var sawOpenPanel = false;
            var sawNameMatch = false;
            var panelWaitSw = Stopwatch.StartNew();
            while (panelWaitSw.ElapsedMilliseconds < MonitoringTiming.PanelPhoneMaxWaitMs)
            {
                ct.ThrowIfCancellationRequested();
                var probeRaw = await executeScriptAction(
                        AvitoCandidatesPageScripts.BuildCandidatePanelPhoneProbeScript(target.TargetIndex, expectedNameJson),
                        ct)
                    .ConfigureAwait(false);
                var probe = TryParsePanelPhoneProbe(probeRaw);
                if (probe is { Revealed: true })
                {
                    return new PanelPhoneOutcome(true, "ok");
                }

                if (probe is { PanelOpen: true })
                {
                    sawOpenPanel = true;
                }

                if (probe is { PanelOpen: true, NameMatch: true })
                {
                    sawNameMatch = true;
                }

                if (probe?.Reason is "target_changed" or "name_mismatch" or "error")
                {
                    return new PanelPhoneOutcome(false, probe.Reason);
                }
                if (probe is { PanelOpen: true, NameMatch: true } && !revealAttempted)
                {
                    revealAttempted = true;
                    await RunRevealScriptAsync().ConfigureAwait(false);
                }

                // Панель может открываться асинхронно после клика: если она ещё
                // не видна — не выходим и не крутимся вплотную, а ждём общей
                // поллинг-задержкой до конца таймаута.
                await HumanDelay.DelayAsync(
                        MonitoringTiming.PanelPhonePollMinMs,
                        MonitoringTiming.PanelPhonePollMaxMs,
                        ct)
                    .ConfigureAwait(false);
            }

            if (!sawOpenPanel)
            {
                return new PanelPhoneOutcome(false, "no_panel");
            }

            return sawNameMatch
                ? new PanelPhoneOutcome(false, "no_phone")
                : new PanelPhoneOutcome(false, "name_mismatch");
        }

        await RunPhoneRevealAsync().ConfigureAwait(false);
        await RunPanelPhoneEnrichmentAsync().ConfigureAwait(false);
        if (!isJobCrmPage && !skipDetailEnrich && domItems > 0)
        {
            await HumanDelay.BetweenResponsesAsync(cancellationToken).ConfigureAwait(false);
            await RunDetailEnrichmentAsync().ConfigureAwait(false);
        }

        var result = new CandidatesListPrepareResult(
            scrollRounds,
            phoneRevealRounds,
            lastCount < 0 ? phonesProbe?.Items ?? 0 : lastCount,
            phonesProbe?.WithPhone ?? 0,
            phonesProbe?.Ready == true,
            phonesProbe?.Masked ?? 0,
            phoneClicksTotal,
            detailEnrichClicks,
            detailEnrichSkipped,
            detailEnrichHits,
            cardFingerprintSkipCount,
            phoneSkipCount,
            profileSkipCount,
            collectionFilterSkipCount,
            PhoneRevealSuccesses: phoneRevealSuccesses,
            PhoneRevealFailures: phoneRevealFailures,
            PhoneRevealFailedCards: phonesProbe?.Failed ?? 0,
            PanelPhoneClicks: panelPhoneClicks,
            PanelPhoneSuccesses: panelPhoneSuccesses,
            PanelPhoneFailures: panelPhoneFailures,
            PhoneRevealExcludedCards: phonesProbe?.Excluded ?? 0);

        var detailEnrichNote = isJobCrmPage
            ? $"detailEnrich=skipped (CRM page, {pageVariant})"
            : skipDetailEnrich
                ? "detailEnrich=deferred (same pass as chat)"
                : $"detailEnrich={result.DetailEnrichHits}/{result.DetailEnrichClicks} (skipped {result.DetailEnrichSkipped})";
        var scrollNote = stoppedOnKnownHistory ? ", scrollStop=known-history" : "";
        var panelPhoneNote = result.PanelPhoneClicks > 0
            ? $", panelPhone={result.PanelPhoneSuccesses}/{result.PanelPhoneClicks} (failed {result.PanelPhoneFailures}"
              + (panelFailReasons.Count > 0
                  ? $", {string.Join(", ", panelFailReasons.OrderByDescending(static kv => kv.Value).Select(static kv => $"{kv.Key}={kv.Value}"))}"
                  : string.Empty)
              + ")"
            : "";
        _ = GlobalLogger.Instance.LogAsync(
            $"Candidates list prepared for {logContext}: scrollRounds={result.ScrollRounds}, domItems={result.DomItemCount}{scrollNote}, phonesReady={result.PhonesReady} ({result.CardsWithPhone}/{result.DomItemCount}), maskedLeft={result.MaskedPhonesLeft}, deferredForPhone={result.DeferredForPhone}, revealOk={result.PhoneRevealSuccesses}, revealFailed={result.PhoneRevealFailures}{panelPhoneNote}, cardFingerprintSkips={result.CardFingerprintSkips}, phoneSkips={result.PhoneSkips}, profileSkips={result.ProfileSkips}, collectionFilterSkips={result.CollectionFilterSkips}, phoneRevealRounds={result.PhoneRevealRounds}, phoneClicks={result.PhoneRevealClicks}, phoneRevealUnlimited=true, {detailEnrichNote}.",
            DeskLinkAuditLogLevel.Info,
            properties: new Dictionary<string, object?>
            {
                ["candidates.context"] = logContext,
                ["candidates.prepare.isJobCrmPage"] = isJobCrmPage,
                ["candidates.prepare.pageVariant"] = pageVariant,
                ["candidates.prepare.skipDetailEnrich"] = skipDetailEnrich,
                ["candidates.prepare.scrollStopKnownHistory"] = stoppedOnKnownHistory,
                ["candidates.prepare.scrollRounds"] = result.ScrollRounds,
                ["candidates.prepare.domItemCount"] = result.DomItemCount,
                ["candidates.prepare.cardsWithPhone"] = result.CardsWithPhone,
                ["candidates.prepare.phonesReady"] = result.PhonesReady,
                ["candidates.prepare.maskedPhonesLeft"] = result.MaskedPhonesLeft,
                ["candidates.prepare.deferredForPhone"] = result.DeferredForPhone,
                ["candidates.prepare.phoneRevealRounds"] = result.PhoneRevealRounds,
                ["candidates.prepare.phoneRevealClicks"] = result.PhoneRevealClicks,
                ["candidates.prepare.phoneRevealUnlimited"] = true,
                ["candidates.prepare.phoneRevealSuccesses"] = result.PhoneRevealSuccesses,
                ["candidates.prepare.phoneRevealFailures"] = result.PhoneRevealFailures,
                ["candidates.prepare.phoneTargetChanged"] = phoneTargetChanges,
                ["candidates.prepare.phonePopupNameMismatch"] = phonePopupNameMismatches,
                ["candidates.prepare.phoneRevealExcludedCards"] = result.PhoneRevealExcludedCards,
                ["candidates.prepare.panelPhoneTargetChanged"] = panelFailReasons.GetValueOrDefault("target_changed"),
                ["candidates.prepare.phoneRevealFailedCards"] = result.PhoneRevealFailedCards,
                ["candidates.prepare.panelPhoneClicks"] = result.PanelPhoneClicks,
                ["candidates.prepare.panelPhoneSuccesses"] = result.PanelPhoneSuccesses,
                ["candidates.prepare.panelPhoneFailures"] = result.PanelPhoneFailures,
                ["candidates.prepare.panelPhoneFailNoPanel"] = panelFailReasons.TryGetValue("no_panel", out var noPanel) ? noPanel : 0,
                ["candidates.prepare.panelPhoneFailNameMismatch"] = panelFailReasons.TryGetValue("name_mismatch", out var nameMismatch) ? nameMismatch : 0,
                ["candidates.prepare.panelPhoneFailNoPhone"] = panelFailReasons.TryGetValue("no_phone", out var noPhone) ? noPhone : 0,
                ["candidates.prepare.panelPhoneFailRowClick"] = panelFailReasons.TryGetValue("row_click_failed", out var rowClick) ? rowClick : 0,
                ["candidates.prepare.detailEnrichClicks"] = result.DetailEnrichClicks,
                ["candidates.prepare.detailEnrichSkipped"] = result.DetailEnrichSkipped,
                ["candidates.prepare.detailEnrichHits"] = result.DetailEnrichHits,
                ["candidates.prepare.cardFingerprintSkips"] = result.CardFingerprintSkips,
                ["candidates.prepare.phoneSkips"] = result.PhoneSkips,
                ["candidates.prepare.profileSkips"] = result.ProfileSkips,
                ["candidates.prepare.totalMs"] = totalSw.ElapsedMilliseconds,
                ["candidates.prepare.firewallMs"] = firewallSw.ElapsedMilliseconds,
                ["candidates.prepare.scrollMs"] = scrollSw.ElapsedMilliseconds,
                ["candidates.prepare.postScrollMs"] = postScrollSw.ElapsedMilliseconds,
                ["candidates.prepare.priorityMs"] = prioritySw.ElapsedMilliseconds,
                ["candidates.prepare.phoneRevealMs"] = phoneRevealMs,
                ["candidates.prepare.detailEnrichMs"] = detailEnrichMs,
                ["candidates.prepare.scrollDomCalls"] = scrollStepCalls + scrollProfileProbeCalls + scrollFingerprintProbeCalls,
                ["candidates.prepare.scrollStepCalls"] = scrollStepCalls,
                ["candidates.prepare.scrollProfileProbeCalls"] = scrollProfileProbeCalls,
                ["candidates.prepare.scrollFingerprintProbeCalls"] = scrollFingerprintProbeCalls,
                ["candidates.prepare.scrollProfileItemsParsed"] = incrementalState.ParsedItems,
                ["candidates.prepare.scrollFingerprintItemsParsed"] = 0,
                ["candidates.prepare.scrollFullRescans"] = scrollFullRescans,
                ["candidates.prepare.scrollFallbackRescans"] = scrollFallbackRescans,
                ["candidates.prepare.jsScrollFallbacks"] = jsScrollFallbacks
            });

        return result;
    }

    private static async Task<AvitoScrollStepProbe> TryParseScrollStepAsync(
        Func<string, CancellationToken, Task<string>> executeScript,
        int previousItemCount,
        CancellationToken cancellationToken)
    {
        var raw = await executeScript(AvitoCandidatesPageScripts.BuildScrollStepScript(previousItemCount), cancellationToken)
            .ConfigureAwait(false);
        return AvitoScrollStepProbeParser.Parse(raw, previousItemCount);
    }

    /// <summary>
    /// Шаг прокрутки CDP-колесом: JS только читает геометрию и снимает DOM, движение — trusted wheel.
    /// Возвращает (null, true), если колесо недоступно/не сработало — вызывающий уходит в JS-fallback.
    /// </summary>
    private static async Task<(AvitoScrollStepProbe? Step, bool WheelUsed)> TryWheelScrollStepAsync(
        Func<string, CancellationToken, Task<string>> executeScript,
        CandidatesPageActors? actors,
        int previousItemCount,
        CancellationToken cancellationToken)
    {
        if (actors?.WheelScrollAsync is null)
        {
            return (null, false);
        }

        var geometryRaw = await executeScript(
                AvitoCandidatesPageScripts.BuildScrollGeometryScript(),
                cancellationToken)
            .ConfigureAwait(false);
        var geometry = AvitoScrollStepProbeParser.TryParseGeometry(geometryRaw);
        if (geometry is null || geometry.ClientHeight <= 0)
        {
            return (null, false);
        }

        var ratio = 0.32 + Random.Shared.NextDouble() * 0.28;
        var delta = Math.Max((int)(geometry.ClientHeight * ratio), 180);
        if (!await actors.TryWheelScrollAsync(delta, cancellationToken).ConfigureAwait(false))
        {
            return (null, false);
        }

        await HumanDelay.DelayAsync(80, 180, cancellationToken).ConfigureAwait(false);
        var probeRaw = await executeScript(
                AvitoCandidatesPageScripts.BuildScrollStepProbeScript(previousItemCount),
                cancellationToken)
            .ConfigureAwait(false);
        var probe = AvitoScrollStepProbeParser.Parse(probeRaw, previousItemCount);
        if (probe.ScrollTop < 0)
        {
            // The wheel was already sent; retrying with JS here would scroll twice.
            return (probe with { RequiresFallbackRescan = true }, true);
        }

        var moved = probe.ScrollTop >= 0 && Math.Abs(probe.ScrollTop - geometry.ScrollTop) > 2;
        if (!moved && !probe.AtEnd)
        {
            return (null, false);
        }

        return (probe with { Moved = moved }, true);
    }

    private static async Task<bool> TryWheelScrollBackAsync(
        Func<string, CancellationToken, Task<string>> executeScript,
        CandidatesPageActors? actors,
        CancellationToken cancellationToken)
    {
        if (actors?.WheelScrollAsync is null)
        {
            return false;
        }

        var geometryRaw = await executeScript(
                AvitoCandidatesPageScripts.BuildScrollGeometryScript(),
                cancellationToken)
            .ConfigureAwait(false);
        var geometry = AvitoScrollStepProbeParser.TryParseGeometry(geometryRaw);
        if (geometry is null || geometry.ClientHeight <= 0)
        {
            return false;
        }

        var ratio = 0.18 + Random.Shared.NextDouble() * 0.22;
        var delta = -Math.Max((int)(geometry.ClientHeight * ratio), 120);
        return await actors.TryWheelScrollAsync(delta, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Возврат наверх CDP-колесом серией жестов; false — использовать JS-fallback.</summary>
    private static async Task<bool> TryWheelScrollToTopAsync(
        Func<string, CancellationToken, Task<string>> executeScript,
        CandidatesPageActors? actors,
        CancellationToken cancellationToken)
    {
        if (actors?.WheelScrollAsync is null)
        {
            return false;
        }

        for (var attempt = 0; attempt < 12; attempt++)
        {
            var geometryRaw = await executeScript(
                    AvitoCandidatesPageScripts.BuildScrollGeometryScript(),
                    cancellationToken)
                .ConfigureAwait(false);
            var geometry = AvitoScrollStepProbeParser.TryParseGeometry(geometryRaw);
            if (geometry is null)
            {
                return false;
            }

            if (geometry.ScrollTop <= 2)
            {
                return true;
            }

            var delta = -(int)Math.Max(geometry.ClientHeight * (0.6 + Random.Shared.NextDouble() * 0.3), 240);
            if (!await actors.TryWheelScrollAsync(delta, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            await HumanDelay.AfterListScrollAsync(cancellationToken).ConfigureAwait(false);
        }

        var finalRaw = await executeScript(AvitoCandidatesPageScripts.BuildScrollGeometryScript(), cancellationToken)
            .ConfigureAwait(false);
        return AvitoScrollStepProbeParser.TryParseGeometry(finalRaw)?.ScrollTop <= 2;
    }

    private static async Task<PhonesReadyProbe?> TryParsePhonesReadyAsync(
        Func<string, CancellationToken, Task<string>> executeScript,
        CancellationToken cancellationToken)
    {
        var raw = await executeScript(AvitoCandidatesPageScripts.BuildPhonesReadyProbeScript(), cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(UnwrapJsonString(raw));
            var root = doc.RootElement;
            return new PhonesReadyProbe(
                root.TryGetProperty("ready", out var r) && r.GetBoolean(),
                root.TryGetProperty("items", out var i) ? i.GetInt32() : 0,
                root.TryGetProperty("withPhone", out var p) ? p.GetInt32() : 0,
                root.TryGetProperty("masked", out var m) ? m.GetInt32() : 0,
                root.TryGetProperty("failed", out var f) ? f.GetInt32() : 0,
                root.TryGetProperty("excluded", out var ex) ? ex.GetInt32() : 0);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<DetailEnrichmentResult> TryCollectDetailEnrichmentAsync(
        Func<string, CancellationToken, Task<string>> executeScript,
        int itemCount,
        Func<IReadOnlyCollection<string>, CancellationToken, Task<IReadOnlySet<string>>>? resolveExistingSourceResponseIdsAsync,
        Func<IReadOnlyCollection<string>, CancellationToken, Task<IReadOnlySet<string>>>? resolveExistingPhonesAsync,
        CandidatesPageActors? actors,
        CancellationToken cancellationToken)
    {
        var entries = new Dictionary<string, object>(StringComparer.Ordinal);
        var clicks = 0;
        var skipped = 0;
        var hits = 0;
        var listItems = await TryParseListItemSkipKeysAsync(executeScript, cancellationToken).ConfigureAwait(false);

        IReadOnlySet<string>? existingOnPage = null;
        if (resolveExistingSourceResponseIdsAsync is not null)
        {
            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < itemCount; index++)
            {
                if (listItems.TryGetValue(index, out var listItem)
                    && !string.IsNullOrWhiteSpace(listItem.SourceResponseId))
                {
                    candidates.Add(listItem.SourceResponseId);
                }
            }

            if (candidates.Count > 0)
            {
                existingOnPage = await resolveExistingSourceResponseIdsAsync(candidates, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        IReadOnlySet<string>? existingPhones = null;
        if (resolveExistingPhonesAsync is not null)
        {
            var phoneCandidates = listItems.Values
                .Select(static x => x.PhoneDigits)
                .Where(static x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (phoneCandidates.Length > 0)
            {
                existingPhones = await resolveExistingPhonesAsync(phoneCandidates, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        for (var index = 0; index < itemCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (listItems.TryGetValue(index, out var listItem)
                && !string.IsNullOrWhiteSpace(listItem.SourceResponseId)
                && existingOnPage is not null
                && existingOnPage.Contains(listItem.SourceResponseId))
            {
                skipped++;
                continue;
            }

            if (listItems.TryGetValue(index, out var phoneListItem)
                && !string.IsNullOrWhiteSpace(phoneListItem.PhoneDigits)
                && existingPhones is not null
                && existingPhones.Contains(phoneListItem.PhoneDigits))
            {
                skipped++;
                continue;
            }

            clicks++;

            _ = await executeScript(AvitoCandidatesPageScripts.BuildRememberCandidateTargetScript(index), cancellationToken)
                .ConfigureAwait(false);
            await HumanDelay.BeforeCandidateClickAsync(cancellationToken).ConfigureAwait(false);

            var pointerClicked = actors is not null
                && await actors.TryClickItemChildAsync(index, null, cancellationToken).ConfigureAwait(false);
            if (!pointerClicked)
            {
                var clickRaw = await executeScript(
                        AvitoCandidatesPageScripts.BuildClickCandidateItemByIndexScript(index),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!TryParseClickStep(clickRaw, out var clickOk) || !clickOk)
                {
                    continue;
                }
            }

            await HumanDelay.AfterCandidateClickAsync(cancellationToken).ConfigureAwait(false);

            var detailRaw = await executeScript(AvitoCandidatesPageScripts.BuildReadDetailPanelScript(), cancellationToken)
                .ConfigureAwait(false);
            var detail = TryParseDetailPanel(detailRaw);
            if (detail is null)
            {
                continue;
            }

            var payload = new Dictionary<string, string?>
            {
                ["candidateIdentity"] = detail.CandidateIdentity,
                ["vacancyUrl"] = detail.VacancyUrl,
                ["vacancy"] = detail.Vacancy,
                ["city"] = detail.City,
                ["age"] = detail.Age
            };

            if (!string.IsNullOrWhiteSpace(detail.VacancyUrl))
            {
                hits++;
            }

            entries[index.ToString()] = payload;
            if (!string.IsNullOrWhiteSpace(detail.PhoneDigits))
            {
                entries[detail.PhoneDigits] = payload;
            }

            await HumanDelay.AfterDetailPanelReadAsync(cancellationToken).ConfigureAwait(false);
        }

        return new DetailEnrichmentResult(entries, clicks, skipped, hits);
    }

    private static async Task<Dictionary<int, ListItemSkipKeys>> TryParseListItemSkipKeysAsync(
        Func<string, CancellationToken, Task<string>> executeScript,
        CancellationToken cancellationToken)
    {
        var raw = await executeScript(AvitoCandidatesPageScripts.BuildCollectListItemSkipKeysScript(), cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(UnwrapJsonString(raw));
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var map = new Dictionary<int, ListItemSkipKeys>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (!item.TryGetProperty("index", out var indexProp))
                {
                    continue;
                }

                var index = indexProp.GetInt32();
                var phoneDigits = item.TryGetProperty("phoneDigits", out var phoneProp)
                    ? phoneProp.GetString() ?? string.Empty
                    : string.Empty;
                var sourceResponseId = item.TryGetProperty("sourceResponseId", out var sourceIdProp)
                    ? sourceIdProp.GetString() ?? string.Empty
                    : string.Empty;
                map[index] = new ListItemSkipKeys(phoneDigits, sourceResponseId);
            }

            return map;
        }
        catch
        {
            return [];
        }
    }

    private sealed record ListItemSkipKeys(string PhoneDigits, string SourceResponseId);

    private sealed record ListItemProfileKeys(
        int Index,
        string FullName,
        string CardFingerprint,
        string City,
        string Age,
        string Gender,
        string PhoneDigits);

    private static async Task<IReadOnlyList<ListItemProfileKeys>> TryParseListItemProfilesAsync(
        Func<string, CancellationToken, Task<string>> executeScript,
        CancellationToken cancellationToken)
    {
        var raw = await executeScript(AvitoCandidatesPageScripts.BuildCollectListItemCardFingerprintsScript(), cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(UnwrapJsonString(raw));
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var items = new List<ListItemProfileKeys>();
            var index = 0;
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                var itemIndex = element.TryGetProperty("index", out var indexProp) ? indexProp.GetInt32() : index;
                items.Add(new ListItemProfileKeys(
                    itemIndex,
                    element.TryGetProperty("fullName", out var nameProp) ? nameProp.GetString() ?? string.Empty : string.Empty,
                    element.TryGetProperty("cardFingerprint", out var fingerprintProp) ? fingerprintProp.GetString() ?? string.Empty : string.Empty,
                    element.TryGetProperty("city", out var cityProp) ? cityProp.GetString() ?? string.Empty : string.Empty,
                    element.TryGetProperty("age", out var ageProp) ? ageProp.GetString() ?? string.Empty : string.Empty,
                    element.TryGetProperty("gender", out var genderProp) ? genderProp.GetString() ?? string.Empty : string.Empty,
                    element.TryGetProperty("phoneDigits", out var phoneProp) ? phoneProp.GetString() ?? string.Empty : string.Empty));
                index++;
            }

            return items;
        }
        catch
        {
            return [];
        }
    }

    private static async Task<int> TryApplyResponseCollectionFilterSkipsAsync(
        Func<string, CancellationToken, Task<string>> executeScript,
        IReadOnlyList<ListItemProfileKeys> listItems,
        ResponseCollectionFilters? filters,
        CancellationToken cancellationToken)
    {
        if (filters is null || !filters.Enabled)
        {
            return 0;
        }

        if (listItems.Count == 0)
        {
            return 0;
        }

        var skipIndices = new List<int>();
        foreach (var item in listItems)
        {
            var result = ResponseCollectionFilter.EvaluateCandidate(
                item.FullName,
                ParseAge(item.Age),
                item.Gender,
                rawText: null,
                filters);
            if (!result.Pass)
            {
                skipIndices.Add(item.Index);
            }
        }

        if (skipIndices.Count == 0)
        {
            return 0;
        }

        _ = await executeScript(
                AvitoCandidatesPageScripts.BuildApplyPhoneRevealSkipScript(skipIndices),
                cancellationToken)
            .ConfigureAwait(false);
        return skipIndices.Count;
    }

    private static int? ParseAge(string? ageText)
    {
        if (string.IsNullOrWhiteSpace(ageText))
        {
            return null;
        }

        var digits = new StringBuilder();
        foreach (var ch in ageText)
        {
            if (char.IsDigit(ch))
            {
                digits.Append(ch);
                if (digits.Length >= 2)
                {
                    break;
                }
            }
        }

        return digits.Length > 0 && int.TryParse(digits.ToString(), out var age) ? age : null;
    }

    private static string NormalizePhoneDigits(string? phoneDigits)
    {
        if (string.IsNullOrWhiteSpace(phoneDigits))
        {
            return string.Empty;
        }

        var value = phoneDigits.Trim();
        if (value.Length == 11 && value.StartsWith("8", StringComparison.Ordinal))
        {
            value = $"7{value[1..]}";
        }
        else if (value.Length == 10)
        {
            value = $"7{value}";
        }

        return value;
    }

    private static bool TryParseClickStep(string? raw, out bool ok)
    {
        ok = false;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(UnwrapJsonString(raw));
            ok = doc.RootElement.TryGetProperty("ok", out var okProp) && okProp.GetBoolean();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static DetailPanelProbe? TryParseDetailPanel(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(UnwrapJsonString(raw));
            var root = doc.RootElement;
            if (!root.TryGetProperty("hasPanel", out var hasPanel) || hasPanel.ValueKind != JsonValueKind.True
                || !root.TryGetProperty("candidateIdentity", out var identity) || string.IsNullOrWhiteSpace(identity.GetString()))
            {
                return null;
            }
            var phoneDigits = root.TryGetProperty("phoneDigits", out var phoneProp)
                ? phoneProp.GetString() ?? string.Empty
                : string.Empty;
            return new DetailPanelProbe(
                identity.GetString()!,
                phoneDigits,
                root.TryGetProperty("vacancyUrl", out var urlProp) ? urlProp.GetString() ?? string.Empty : string.Empty,
                root.TryGetProperty("vacancy", out var vacancyProp) ? vacancyProp.GetString() ?? string.Empty : string.Empty,
                root.TryGetProperty("city", out var cityProp) ? cityProp.GetString() ?? string.Empty : string.Empty,
                root.TryGetProperty("age", out var ageProp) ? ageProp.GetString() ?? string.Empty : string.Empty);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<ResponsesPageDetection> TryDetectJobCrmResponsesPageAsync(
        Func<string, CancellationToken, Task<string>> executeScript,
        CancellationToken cancellationToken)
    {
        var raw = await executeScript(AvitoCandidatesPageScripts.BuildIsJobCrmResponsesPageScript(), cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new ResponsesPageDetection(false, AvitoResponsesPageVariant.Unknown);
        }

        try
        {
            using var doc = JsonDocument.Parse(UnwrapJsonString(raw));
            var isJobCrm = doc.RootElement.TryGetProperty("isJobCrm", out var prop) && prop.GetBoolean();
            var pageVariant = doc.RootElement.TryGetProperty("pageVariant", out var variantProp)
                ? variantProp.GetString() ?? AvitoResponsesPageVariant.Unknown
                : isJobCrm ? AvitoResponsesPageVariant.JobCrm : AvitoResponsesPageVariant.Unknown;
            return new ResponsesPageDetection(isJobCrm, pageVariant);
        }
        catch
        {
            return new ResponsesPageDetection(false, AvitoResponsesPageVariant.Unknown);
        }
    }

    private readonly record struct ResponsesPageDetection(bool IsJobCrm, string PageVariant);

    /// <summary>
    /// Индексы карточек с открытым phone-watch — phone-reveal для них нельзя скипать.
    /// </summary>
    private static async Task<HashSet<int>> ResolveOpenPhoneWatchProtectedIndicesAsync(
        IReadOnlyList<ListItemProfileKeys> listItems,
        Func<string, CancellationToken, Task<bool>>? isOpenPhoneWatchAsync,
        IReadOnlySet<string> openPhoneWatchNameKeys,
        CancellationToken cancellationToken)
    {
        if (isOpenPhoneWatchAsync is null && openPhoneWatchNameKeys.Count == 0)
        {
            return [];
        }

        if (listItems.Count == 0)
        {
            return [];
        }

        var protectedIndices = new HashSet<int>();
        foreach (var item in listItems)
        {
            if (string.IsNullOrWhiteSpace(item.FullName))
            {
                continue;
            }

            var nameKey = ResponsePhoneWatchEvaluator.BuildFullNameKey(item.FullName);
            if (openPhoneWatchNameKeys.Contains(nameKey))
            {
                protectedIndices.Add(item.Index);
                continue;
            }

            if (isOpenPhoneWatchAsync is null)
            {
                continue;
            }

            try
            {
                if (await isOpenPhoneWatchAsync(item.FullName, cancellationToken).ConfigureAwait(false))
                {
                    protectedIndices.Add(item.Index);
                }
            }
            catch
            {
                // best-effort: ошибка store не должна валить сбор
            }
        }

        return protectedIndices;
    }

    private static bool HasFullyRevealedPhoneDigits(string? phoneDigits)
    {
        var digits = NormalizePhoneDigits(phoneDigits);
        return digits.Length >= 10;
    }

    private static async Task<KnownHistoryProbe> ProbeLoadedItemsKnownAsync(
        IReadOnlyList<ListItemProfileKeys> listItems,
        int fromIndex,
        int toIndexExclusive,
        Func<IReadOnlyCollection<string>, CancellationToken, Task<IReadOnlySet<string>>> resolveExistingCardFingerprintsAsync,
        CancellationToken cancellationToken)
    {
        if (toIndexExclusive <= fromIndex)
        {
            return new KnownHistoryProbe(true, 0);
        }

        var batch = new List<string>();
        for (var index = fromIndex; index < toIndexExclusive; index++)
        {
            var item = listItems.FirstOrDefault(x => x.Index == index);
            if (item is null || string.IsNullOrWhiteSpace(item.CardFingerprint))
            {
                return new KnownHistoryProbe(false, listItems.Count);
            }

            batch.Add(item.CardFingerprint);
        }

        if (batch.Count == 0)
        {
            return new KnownHistoryProbe(false, listItems.Count);
        }

        var existing = await resolveExistingCardFingerprintsAsync(
                batch.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                cancellationToken)
            .ConfigureAwait(false);
        foreach (var fingerprint in batch)
        {
            if (!existing.Contains(fingerprint))
            {
                return new KnownHistoryProbe(false, listItems.Count);
            }
        }

        return new KnownHistoryProbe(true, listItems.Count);
    }

    private static async Task<int> TryApplyKnownCardFingerprintSkipsAsync(
        Func<string, CancellationToken, Task<string>> executeScript,
        IReadOnlyList<ListItemProfileKeys> listItems,
        Func<IReadOnlyCollection<string>, CancellationToken, Task<IReadOnlySet<string>>>? resolveExistingCardFingerprintsAsync,
        IReadOnlySet<int> openWatchProtected,
        CancellationToken cancellationToken)
    {
        if (resolveExistingCardFingerprintsAsync is null)
        {
            return 0;
        }

        var fingerprintsByIndex = listItems
            .Where(static item => !string.IsNullOrWhiteSpace(item.CardFingerprint))
            .ToDictionary(static item => item.Index, static item => item.CardFingerprint);
        if (fingerprintsByIndex.Count == 0)
        {
            return 0;
        }

        var existing = await resolveExistingCardFingerprintsAsync(
                fingerprintsByIndex.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                cancellationToken)
            .ConfigureAwait(false);
        if (existing.Count == 0)
        {
            return 0;
        }

        var skipIndices = fingerprintsByIndex
            .Where(kv => existing.Contains(kv.Value))
            .Where(kv => !openWatchProtected.Contains(kv.Key))
            .Where(kv =>
                listItems.FirstOrDefault(item => item.Index == kv.Key) is { } item
                && HasFullyRevealedPhoneDigits(item.PhoneDigits))
            .Select(kv => kv.Key)
            .ToArray();
        if (skipIndices.Length == 0)
        {
            return 0;
        }

        _ = await executeScript(
                AvitoCandidatesPageScripts.BuildApplyPhoneRevealSkipScript(skipIndices),
                cancellationToken)
            .ConfigureAwait(false);
        return skipIndices.Length;
    }

    private static async Task<int> TryApplyKnownPhoneSkipsAsync(
        Func<string, CancellationToken, Task<string>> executeScript,
        IReadOnlyList<ListItemProfileKeys> listItems,
        Func<IReadOnlyCollection<string>, CancellationToken, Task<IReadOnlySet<string>>>? resolveExistingPhonesAsync,
        IReadOnlySet<int> openWatchProtected,
        CancellationToken cancellationToken)
    {
        if (resolveExistingPhonesAsync is null)
        {
            return 0;
        }

        if (listItems.Count == 0)
        {
            return 0;
        }

        var phoneCandidates = listItems
            .Select(static x => x.PhoneDigits)
            .Where(static x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (phoneCandidates.Length == 0)
        {
            return 0;
        }

        var existing = await resolveExistingPhonesAsync(phoneCandidates, cancellationToken).ConfigureAwait(false);
        if (existing.Count == 0)
        {
            return 0;
        }

        // Известный старый номер + open watch — всё равно раскрываем (номер мог смениться под маской).
        var skipIndices = listItems
            .Where(item => !openWatchProtected.Contains(item.Index))
            .Where(item => HasFullyRevealedPhoneDigits(item.PhoneDigits)
                           && existing.Contains(item.PhoneDigits))
            .Select(static item => item.Index)
            .ToArray();
        if (skipIndices.Length == 0)
        {
            return 0;
        }

        _ = await executeScript(
                AvitoCandidatesPageScripts.BuildApplyPhoneRevealSkipScript(skipIndices),
                cancellationToken)
            .ConfigureAwait(false);
        return skipIndices.Length;
    }

    private static async Task<int> TryApplyKnownProfileSkipsAsync(
        Func<string, CancellationToken, Task<string>> executeScript,
        IReadOnlyList<ListItemProfileKeys> listItems,
        Func<IReadOnlyList<CandidateLookupProfileDto>, CancellationToken, Task<IReadOnlySet<int>>>? resolveExistingMatchedProfileIndicesAsync,
        IReadOnlySet<int> openWatchProtected,
        CancellationToken cancellationToken)
    {
        if (resolveExistingMatchedProfileIndicesAsync is null)
        {
            return 0;
        }

        if (listItems.Count == 0)
        {
            return 0;
        }

        var profiles = listItems
            .Select(item => new CandidateLookupProfileDto(
                item.FullName,
                ParseAge(item.Age),
                item.City,
                NormalizePhoneDigits(item.PhoneDigits)))
            .ToList();
        var matched = await resolveExistingMatchedProfileIndicesAsync(profiles, cancellationToken)
            .ConfigureAwait(false);
        if (matched.Count == 0)
        {
            return 0;
        }

        // Уже в Орбите, но open phone-watch или номер не раскрыт — reveal обязателен.
        var skipIndices = matched
            .Where(i => i >= 0 && i < listItems.Count)
            .Select(i => listItems[i])
            .Where(item => !openWatchProtected.Contains(item.Index))
            .Where(item => HasFullyRevealedPhoneDigits(item.PhoneDigits))
            .Select(item => item.Index)
            .ToArray();
        if (skipIndices.Length == 0)
        {
            return 0;
        }

        _ = await executeScript(
                AvitoCandidatesPageScripts.BuildApplyPhoneRevealSkipScript(skipIndices),
                cancellationToken)
            .ConfigureAwait(false);
        return skipIndices.Length;
    }

    private static async Task<Dictionary<int, string>> TryParseCardFingerprintsAsync(
        Func<string, CancellationToken, Task<string>> executeScript,
        CancellationToken cancellationToken)
    {
        var raw = await executeScript(AvitoCandidatesPageScripts.BuildCollectListItemCardFingerprintsScript(), cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(UnwrapJsonString(raw));
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var map = new Dictionary<int, string>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (!item.TryGetProperty("index", out var indexProp))
                {
                    continue;
                }

                var fingerprint = item.TryGetProperty("cardFingerprint", out var fpProp)
                    ? fpProp.GetString() ?? string.Empty
                    : string.Empty;
                if (string.IsNullOrWhiteSpace(fingerprint))
                {
                    continue;
                }

                map[indexProp.GetInt32()] = fingerprint.Trim();
            }

            return map;
        }
        catch
        {
            return [];
        }
    }

    private static async Task<RevealPhonesStepProbe?> TryRevealMaskedPhonesAsync(
        Func<string, CancellationToken, Task<string>> executeScript,
        CandidatesPageActors? actors,
        CancellationToken cancellationToken)
    {
        if (actors?.PointerClickItemChildAsync is not null)
        {
            var locate = await TryParseMaskedPhoneTargetAsync(executeScript, cancellationToken).ConfigureAwait(false);
            if (locate is null)
            {
                return null;
            }

            if (locate.Masked == 0 || locate.TargetIndex < 0)
            {
                return new RevealPhonesStepProbe(locate.Items, locate.Masked, 0, Attempted: false, Failed: locate.Failed);
            }

            if (await actors.TryClickItemChildAsync(
                    locate.TargetIndex,
                    "[data-marker='job-application/phone']",
                    cancellationToken).ConfigureAwait(false))
            {
                return new RevealPhonesStepProbe(
                    locate.Items,
                    locate.Masked,
                    1,
                    Attempted: true,
                    ClickedIndex: locate.TargetIndex,
                    Failed: locate.Failed);
            }

            // A CDP error may arrive after dispatch. Let DOM settle before deciding on a fallback.
            await HumanDelay.AfterPhoneRevealClickAsync(cancellationToken).ConfigureAwait(false);
            var after = await TryParseMaskedPhoneTargetAsync(executeScript, cancellationToken).ConfigureAwait(false);
            if (after is null)
            {
                return new RevealPhonesStepProbe(locate.Items, locate.Masked, 0, Attempted: true, Failed: locate.Failed);
            }

            if (after.Masked < locate.Masked || after.TargetIndex != locate.TargetIndex)
            {
                return new RevealPhonesStepProbe(
                    locate.Items,
                    locate.Masked,
                    1,
                    Attempted: true,
                    ClickedIndex: locate.TargetIndex,
                    Failed: locate.Failed);
            }

            // The mask is still present, so the pointer action did not take effect; use the existing fallback.
        }

        var raw = await executeScript(AvitoCandidatesPageScripts.BuildRevealMaskedPhonesStepScript(), cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(UnwrapJsonString(raw));
            var root = doc.RootElement;
            var clicked = root.TryGetProperty("clicked", out var clickedProperty)
                ? clickedProperty.GetInt32()
                : 0;
            return new RevealPhonesStepProbe(
                root.TryGetProperty("items", out var i) ? i.GetInt32() : 0,
                root.TryGetProperty("masked", out var m) ? m.GetInt32() : 0,
                clicked,
                Attempted: clicked > 0,
                ClickedIndex: root.TryGetProperty("index", out var idx) ? idx.GetInt32() : -1,
                Failed: root.TryGetProperty("failed", out var failed) ? failed.GetInt32() : 0);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<MaskedPhoneTargetProbe?> TryParseMaskedPhoneTargetAsync(
        Func<string, CancellationToken, Task<string>> executeScript,
        CancellationToken cancellationToken)
    {
        var raw = await executeScript(AvitoCandidatesPageScripts.BuildFindMaskedPhoneTargetScript(), cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(UnwrapJsonString(raw));
            var root = doc.RootElement;
            return new MaskedPhoneTargetProbe(
                root.TryGetProperty("items", out var i) ? i.GetInt32() : 0,
                root.TryGetProperty("masked", out var m) ? m.GetInt32() : 0,
                root.TryGetProperty("targetIndex", out var t) ? t.GetInt32() : -1,
                root.TryGetProperty("failed", out var f) ? f.GetInt32() : 0);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<ContactsPopupRevealProbe?> TryRevealNextContactsPopupPhoneAsync(
        Func<string, CancellationToken, Task<string>> executeScript,
        CandidatesPageActors? actors,
        CancellationToken cancellationToken)
    {
        if (actors?.PointerClickItemChildAsync is not null)
        {
            return await RevealNextContactsPopupPhoneWithPointerAsync(executeScript, actors, cancellationToken)
                .ConfigureAwait(false);
        }

        var click = await TryParseContactsPopupClickAsync(executeScript, cancellationToken).ConfigureAwait(false);
        if (click is null)
        {
            return null;
        }

        if (click.ClosedExisting)
        {
            await HumanDelay.DelayAsync(180, 420, cancellationToken).ConfigureAwait(false);
            click = await TryParseContactsPopupClickAsync(executeScript, cancellationToken).ConfigureAwait(false);
            if (click is null)
            {
                return null;
            }
        }

        if (!click.Clicked)
        {
            return new ContactsPopupRevealProbe(click.Items, click.Pending, false, false, click.Index);
        }

        await HumanDelay.AfterPhoneRevealClickAsync(cancellationToken).ConfigureAwait(false);

        var popupWaitSw = Stopwatch.StartNew();
        while (popupWaitSw.ElapsedMilliseconds < MonitoringTiming.ContactsPopupMaxWaitMs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var probe = await TryParseContactsPopupProbeAsync(executeScript, click.Index, true, cancellationToken)
                .ConfigureAwait(false);
            if (probe is { Revealed: true })
            {
                return new ContactsPopupRevealProbe(click.Items, click.Pending, true, true, click.Index);
            }

            if (probe is { State: "error" or "target_changed" or "name_mismatch" })
            {
                _ = await executeScript(AvitoCandidatesPageScripts.BuildCloseContactsPopupScript(), cancellationToken)
                    .ConfigureAwait(false);
                return new ContactsPopupRevealProbe(click.Items, click.Pending, true, false, click.Index, probe.State);
            }

            await HumanDelay.DelayAsync(
                    MonitoringTiming.ContactsPopupPollMinMs,
                    MonitoringTiming.ContactsPopupPollMaxMs,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        _ = await executeScript(AvitoCandidatesPageScripts.BuildCloseContactsPopupScript(), cancellationToken)
            .ConfigureAwait(false);
        return new ContactsPopupRevealProbe(click.Items, click.Pending, true, false, click.Index);
    }

    /// <summary>Popup-раскрытие через trusted CDP-клик: JS только находит цель и читает состояние.</summary>
    private static async Task<ContactsPopupRevealProbe?> RevealNextContactsPopupPhoneWithPointerAsync(
        Func<string, CancellationToken, Task<string>> executeScript,
        CandidatesPageActors actors,
        CancellationToken cancellationToken)
    {
        var target = await TryParseContactsPopupTargetAsync(executeScript, cancellationToken).ConfigureAwait(false);
        if (target is null)
        {
            return null;
        }

        if (target.Pending == 0)
        {
            return new ContactsPopupRevealProbe(target.Items, 0, false, false);
        }

        if (target.ClosedExisting)
        {
            await CloseContactsPopupAsync(executeScript, actors, cancellationToken).ConfigureAwait(false);
            await HumanDelay.DelayAsync(180, 420, cancellationToken).ConfigureAwait(false);
            target = await TryParseContactsPopupTargetAsync(executeScript, cancellationToken).ConfigureAwait(false);
            if (target is null)
            {
                return null;
            }
        }

        if (target.TargetIndex < 0)
        {
            return new ContactsPopupRevealProbe(target.Items, target.Pending, false, false);
        }

        var childSelector = target.Kind == "call-button"
            ? "[data-marker='job-application/call-button']"
            : "[data-marker='job-application/phone']";
        if (!await actors.TryClickItemChildAsync(target.TargetIndex, childSelector, cancellationToken)
                .ConfigureAwait(false))
        {
            // Trusted-клик не сработал: помечаем карточку failed в этом проходе,
            // иначе следующий раунд снова выберет её и съест весь бюджет раундов.
            await MarkPhoneRevealFailedAsync(executeScript, target.TargetIndex, cancellationToken)
                .ConfigureAwait(false);
            return new ContactsPopupRevealProbe(target.Items, target.Pending, false, false, target.TargetIndex);
        }

        await HumanDelay.AfterPhoneRevealClickAsync(cancellationToken).ConfigureAwait(false);

        var popupWaitSw = Stopwatch.StartNew();
        while (popupWaitSw.ElapsedMilliseconds < MonitoringTiming.ContactsPopupMaxWaitMs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var probe = await TryParseContactsPopupProbeAsync(executeScript, target.TargetIndex, false, cancellationToken)
                .ConfigureAwait(false);
            if (probe is { Revealed: true })
            {
                await CloseContactsPopupAsync(executeScript, actors, cancellationToken).ConfigureAwait(false);
                return new ContactsPopupRevealProbe(target.Items, target.Pending, true, true, target.TargetIndex);
            }

            if (probe is { State: "error" or "target_changed" or "name_mismatch" })
            {
                await CloseContactsPopupAsync(executeScript, actors, cancellationToken).ConfigureAwait(false);
                return new ContactsPopupRevealProbe(target.Items, target.Pending, true, false, target.TargetIndex, probe.State);
            }

            await HumanDelay.DelayAsync(
                    MonitoringTiming.ContactsPopupPollMinMs,
                    MonitoringTiming.ContactsPopupPollMaxMs,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await CloseContactsPopupAsync(executeScript, actors, cancellationToken).ConfigureAwait(false);
        return new ContactsPopupRevealProbe(target.Items, target.Pending, true, false, target.TargetIndex);
    }

    private static async Task CloseContactsPopupAsync(
        Func<string, CancellationToken, Task<string>> executeScript,
        CandidatesPageActors actors,
        CancellationToken cancellationToken)
    {
        if (actors.CloseContactsPopupAsync is not null
            && await actors.SafeCloseContactsPopupAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        _ = await executeScript(AvitoCandidatesPageScripts.BuildCloseContactsPopupScript(), cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<ContactsPopupTargetProbe?> TryParseContactsPopupTargetAsync(
        Func<string, CancellationToken, Task<string>> executeScript,
        CancellationToken cancellationToken)
    {
        var raw = await executeScript(
                AvitoCandidatesPageScripts.BuildFindContactsPopupTargetScript(),
                cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(UnwrapJsonString(raw));
            var root = doc.RootElement;
            return new ContactsPopupTargetProbe(
                root.TryGetProperty("items", out var i) ? i.GetInt32() : 0,
                root.TryGetProperty("pending", out var p) ? p.GetInt32() : 0,
                root.TryGetProperty("targetIndex", out var t) ? t.GetInt32() : -1,
                root.TryGetProperty("kind", out var k) ? k.GetString() ?? "none" : "none",
                root.TryGetProperty("closedExisting", out var closed) && closed.ValueKind == JsonValueKind.True);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<PanelPhoneTargetProbe?> TryParsePanelPhoneTargetAsync(
        Func<string, CancellationToken, Task<string>> executeScript,
        CancellationToken cancellationToken)
    {
        var raw = await executeScript(
                AvitoCandidatesPageScripts.BuildFindPanelPhoneTargetScript(),
                cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(UnwrapJsonString(raw));
            var root = doc.RootElement;
            return new PanelPhoneTargetProbe(
                root.TryGetProperty("items", out var i) ? i.GetInt32() : 0,
                root.TryGetProperty("pending", out var p) ? p.GetInt32() : 0,
                root.TryGetProperty("targetIndex", out var t) ? t.GetInt32() : -1,
                root.TryGetProperty("targetName", out var n) ? n.GetString() ?? string.Empty : string.Empty);
        }
        catch
        {
            return null;
        }
    }

    private static PanelPhoneRevealProbe? TryParsePanelPhoneReveal(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(UnwrapJsonString(raw));
            var root = doc.RootElement;
            return new PanelPhoneRevealProbe(
                root.TryGetProperty("clicked", out var c) && c.ValueKind == JsonValueKind.True);
        }
        catch
        {
            return null;
        }
    }

    private static PanelPhoneStateProbe? TryParsePanelPhoneProbe(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(UnwrapJsonString(raw));
            var root = doc.RootElement;
            return new PanelPhoneStateProbe(
                root.TryGetProperty("panelOpen", out var o) && o.ValueKind == JsonValueKind.True,
                root.TryGetProperty("revealed", out var r) && r.ValueKind == JsonValueKind.True,
                root.TryGetProperty("nameMatch", out var nm) && nm.ValueKind == JsonValueKind.True,
                root.TryGetProperty("reason", out var reason) ? reason.GetString() : null);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<ContactsPopupClickProbe?> TryParseContactsPopupClickAsync(
        Func<string, CancellationToken, Task<string>> executeScript,
        CancellationToken cancellationToken)
    {
        var raw = await executeScript(
                AvitoCandidatesPageScripts.BuildRevealNextContactsPopupPhoneScript(),
                cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(UnwrapJsonString(raw));
            var root = doc.RootElement;
            return new ContactsPopupClickProbe(
                root.TryGetProperty("items", out var i) ? i.GetInt32() : 0,
                root.TryGetProperty("pending", out var p) ? p.GetInt32() : 0,
                root.TryGetProperty("clicked", out var c) && c.ValueKind == JsonValueKind.True,
                root.TryGetProperty("closedExisting", out var closed) && closed.ValueKind == JsonValueKind.True,
                root.TryGetProperty("index", out var idx) ? idx.GetInt32() : -1);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<ContactsPopupStateProbe?> TryParseContactsPopupProbeAsync(
        Func<string, CancellationToken, Task<string>> executeScript,
        int targetIndex,
        bool closeOnReady,
        CancellationToken cancellationToken)
    {
        var raw = await executeScript(
                AvitoCandidatesPageScripts.BuildContactsPopupProbeScript(targetIndex, closeOnReady),
                cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(UnwrapJsonString(raw));
            var root = doc.RootElement;
            return new ContactsPopupStateProbe(
                root.TryGetProperty("state", out var stateProp) ? stateProp.GetString() ?? "" : "",
                root.TryGetProperty("revealed", out var revealedProp) && revealedProp.GetBoolean());
        }
        catch
        {
            return null;
        }
    }

    /// <summary>WebView2 иногда возвращает JSON-строку в кавычках.</summary>
    private static string UnwrapJsonString(string raw)
    {
        var t = raw.Trim();
        if (t.Length >= 2 && t.StartsWith('"') && t.EndsWith('"'))
        {
            try
            {
                return JsonSerializer.Deserialize<string>(t) ?? t;
            }
            catch
            {
                return t;
            }
        }

        return t;
    }

        private static async Task MarkPhoneRevealFailedAsync(
            Func<string, CancellationToken, Task<string>> executeScript,
            int index,
            CancellationToken cancellationToken)
        {
            if (index < 0)
            {
                return;
            }

            try
            {
                _ = await executeScript(
                        AvitoCandidatesPageScripts.BuildMarkPhoneRevealFailedScript(index),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                // best effort: пометка нужна только внутри текущего прохода
            }
        }

        private static async Task AdvanceRevealCursorAsync(
            Func<string, CancellationToken, Task<string>> executeScript,
            CancellationToken cancellationToken)
        {
            try
            {
                _ = await executeScript(
                        AvitoCandidatesPageScripts.BuildAdvanceRevealCursorScript(),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                // best effort: курсор — эвристика ротации, не критичен
            }
        }

        private sealed record KnownHistoryProbe(bool AllKnown, int ParsedItems);

    private sealed record PhonesReadyProbe(bool Ready, int Items, int WithPhone, int Masked, int Failed = 0, int Excluded = 0);

    private sealed record RevealPhonesStepProbe(int Items, int Masked, int Clicked, bool Attempted, int ClickedIndex = -1, int Failed = 0);

    private sealed record MaskedPhoneTargetProbe(int Items, int Masked, int TargetIndex, int Failed = 0);

    private sealed record ContactsPopupTargetProbe(int Items, int Pending, int TargetIndex, string Kind, bool ClosedExisting);

    private sealed record ContactsPopupRevealProbe(int Items, int Pending, bool Clicked, bool Revealed, int Index = -1, string? Reason = null);

    private sealed record PanelPhoneTargetProbe(int Items, int Pending, int TargetIndex, string TargetName = "");

    private sealed record PanelPhoneRevealProbe(bool Clicked);

    private sealed record PanelPhoneStateProbe(bool PanelOpen, bool Revealed, bool NameMatch = false, string? Reason = null);

    /// <summary>Итог добора телефона через панель: успех + причина для диагностики.</summary>
    private sealed record PanelPhoneOutcome(bool Success, string Reason);

    private sealed record ContactsPopupClickProbe(int Items, int Pending, bool Clicked, bool ClosedExisting, int Index);

    private sealed record ContactsPopupStateProbe(string State, bool Revealed);

    private sealed record DetailPanelProbe(string CandidateIdentity, string PhoneDigits, string VacancyUrl, string Vacancy, string City, string Age);

    private sealed record DetailEnrichmentResult(Dictionary<string, object> Entries, int Clicks, int Skipped, int Hits);
}

public sealed record CandidatesListPrepareResult(
    int ScrollRounds,
    int PhoneRevealRounds,
    int DomItemCount,
    int CardsWithPhone,
    bool PhonesReady,
    int MaskedPhonesLeft = 0,
    int PhoneRevealClicks = 0,
    int DetailEnrichClicks = 0,
    int DetailEnrichSkipped = 0,
    int DetailEnrichHits = 0,
    int CardFingerprintSkips = 0,
    int PhoneSkips = 0,
    int ProfileSkips = 0,
    int CollectionFilterSkips = 0,
    /// <summary>Клики, реально давшие номер в этом проходе.</summary>
    int PhoneRevealSuccesses = 0,
    /// <summary>Клики, не давшие номера (карточка помечена failed до конца прохода).</summary>
    int PhoneRevealFailures = 0,
    /// <summary>Карточки, чей клик не дал номера (включая отметки из popup-пути).</summary>
    int PhoneRevealFailedCards = 0,
    /// <summary>Карточки, для которых телефон добирали через боковую панель отклика.</summary>
    int PanelPhoneClicks = 0,
    int PanelPhoneSuccesses = 0,
    int PanelPhoneFailures = 0,
    int PhoneRevealExcludedCards = 0)
{
    /// <summary>Карточки, оставшиеся без номера после неудачных кликов или отсутствия кнопки.</summary>
    public int DeferredForPhone => Math.Max(0, MaskedPhonesLeft + PhoneRevealFailedCards);
}
