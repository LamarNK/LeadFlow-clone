using LeadFlow.Data;
using LeadFlow.Logging.Audit;
using LeadFlow.Models;
using LeadFlow.Services.Avito;
using LeadFlow.Services.Bitrix;

namespace LeadFlow.Services;

public sealed class MonitoringService(
    ISettingsService settingsService,
    AppRepository repository,
    IPhoneNormalizer phoneNormalizer,
    ICandidateParser candidateParser,
    IDuplicateService duplicateService,
    IBitrixClient bitrixClient,
    IAvitoResponseSource avitoResponseSource,
    AvitoDemoResponseSource avitoDemoResponseSource) : IMonitoringService
{
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private static readonly TimeSpan MinCycleDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaxCycleDelay = TimeSpan.FromMinutes(10);

    public event EventHandler<MonitoringStatus>? StatusChanged;
    public event EventHandler<string>? StatusMessageChanged;
    public event EventHandler<CandidateResponse>? ResponseProcessed;

    public MonitoringStatus CurrentStatus { get; private set; } = MonitoringStatus.Waiting;
    public string CurrentStatusMessage { get; private set; } = string.Empty;
    public bool IsActive { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_loopTask is { IsCompleted: false })
        {
            return;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        IsActive = true;
        UpdateStatus(MonitoringStatus.Running, "Запуск мониторинга: загружаем настройки и начинаем обработку откликов.");
        try
        {
            var settings = await settingsService.LoadAsync(_cts.Token);
            // Синхронизация при старте отключена: проверка дублей выполняется через API для каждого отклика
            // await SyncBitrixLeadsAsync(settings, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            IsActive = false;
            UpdateStatus(MonitoringStatus.Stopped, string.Empty);
            _cts.Dispose();
            _cts = null;
            return;
        }
        catch (Exception ex)
        {
            _ = GlobalLogger.Instance.LogAsync(
                $"Bitrix pre-sync failed.{Environment.NewLine}{ex}",
                DeskLinkAuditLogLevel.Error);
            IsActive = false;
            _cts.Dispose();
            _cts = null;
            UpdateStatus(MonitoringStatus.Error, $"Ошибка запуска мониторинга: {ex.Message}");
            return;
        }

        _ = GlobalLogger.Instance.LogAsync("Monitoring started.", DeskLinkAuditLogLevel.Info);
        UpdateStatus(MonitoringStatus.Running, "Мониторинг запущен: начинаем обход активных аккаунтов Авито.");
        _loopTask = RunAsync(_cts.Token);
    }

    public async Task StopAsync()
    {
        if (_cts is null)
        {
            return;
        }

        UpdateStatus(MonitoringStatus.Stopped, "Останавливаем мониторинг и завершаем текущие операции.");
        _cts.Cancel();
        if (_loopTask is not null)
        {
            await _loopTask;
        }

        IsActive = false;
        UpdateStatus(MonitoringStatus.Stopped, string.Empty);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var settings = await settingsService.LoadAsync(cancellationToken);
                var accounts = settings.Avito.Accounts.Where(x => x.IsEnabled).ToList();
                UpdateStatus(
                    MonitoringStatus.Running,
                    accounts.Count == 0
                        ? "Активных аккаунтов Авито нет: откройте настройки и включите хотя бы один аккаунт."
                        : $"Начинаем новый цикл мониторинга: активных аккаунтов {accounts.Count}.");

                foreach (var account in accounts)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    await ProcessAccountAsync(account, settings, cancellationToken);
                    await Task.Delay(TimeSpan.FromSeconds(settings.MonitoringSafety.DelayBetweenAccountsSeconds), cancellationToken);
                }

                var delay = GetRandomCycleDelay();
                UpdateStatus(MonitoringStatus.Waiting, $"Цикл завершён. Ждём следующую проверку {FormatDelay(delay)}.");
                await Task.Delay(delay, cancellationToken);
                UpdateStatus(MonitoringStatus.Running, "Пауза завершена: запускаем следующий цикл мониторинга.");
            }
        }
        catch (OperationCanceledException)
        {
            _ = GlobalLogger.Instance.LogAsync("Monitoring stopped by cancellation.", DeskLinkAuditLogLevel.Info);
        }
        catch (Exception ex)
        {
            _ = GlobalLogger.Instance.LogAsync(
                $"Monitoring loop failed.{Environment.NewLine}{ex}",
                DeskLinkAuditLogLevel.Error);
            IsActive = false;
            UpdateStatus(MonitoringStatus.Error, $"Мониторинг остановлен из-за ошибки: {ex.Message}");
        }
    }

    private async Task ProcessAccountAsync(AvitoAccount account, AppSettings settings, CancellationToken cancellationToken)
    {
        if (account.Status is AvitoAccountStatus.RequiresLogin or AvitoAccountStatus.RequiresManualAction or AvitoAccountStatus.Paused)
        {
            UpdateStatus(
                account.Status == AvitoAccountStatus.RequiresLogin ? MonitoringStatus.RequiresAuthorization : MonitoringStatus.RequiresManualAction,
                $"Аккаунт \"{account.DisplayName}\" пропущен. Текущий статус: {account.Status}.");
            _ = GlobalLogger.Instance.LogAsync(
                $"Account {account.DisplayName} skipped because of status {account.Status}.",
                DeskLinkAuditLogLevel.Warning);
            await repository.AddLogAsync(new ProcessingLogItem
            {
                AccountId = account.Id,
                Level = "Warning",
                Message = "Аккаунт пропущен",
                Details = $"Статус: {account.Status}"
            }, cancellationToken);
            return;
        }

        account.Status = AvitoAccountStatus.Monitoring;
        account.LastMonitoringAt = DateTime.UtcNow;
        UpdateStatus(MonitoringStatus.Running, $"Проверяем аккаунт Авито \"{account.DisplayName}\": открываем список откликов.");
        _ = GlobalLogger.Instance.LogAsync(
            $"Processing account {account.DisplayName}.",
            DeskLinkAuditLogLevel.Info);
        await repository.SaveAccountAsync(account, cancellationToken);

        var responses = settings.DemoModeEnabled
            ? await avitoDemoResponseSource.GetBatchAsync(account, Math.Max(1, settings.MonitoringSafety.MaxResponsesPerCycle / 2), cancellationToken)
            : await avitoResponseSource.GetNewResponsesAsync(account, settings, cancellationToken);
        UpdateStatus(MonitoringStatus.Running, $"Аккаунт \"{account.DisplayName}\" проверен: найдено новых откликов {responses.Count}.");

        foreach (var response in responses.Take(settings.MonitoringSafety.MaxResponsesPerCycle))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            await ProcessResponseAsync(response, settings, cancellationToken);
            await Task.Delay(TimeSpan.FromSeconds(settings.MonitoringSafety.DelayBetweenResponsesSeconds), cancellationToken);
        }

        if (account.Status == AvitoAccountStatus.Monitoring)
        {
            account.Status = AvitoAccountStatus.Authorized;
        }

        await repository.SaveAccountAsync(account, cancellationToken);
    }

    private async Task ProcessResponseAsync(CandidateResponse response, AppSettings settings, CancellationToken cancellationToken)
    {
        UpdateStatus(MonitoringStatus.Running, $"Обрабатываем отклик \"{response.FullName}\" ({response.PhoneRaw}): готовим данные кандидата.");
        var names = candidateParser.ParseName(response.FullName);
        response.FirstName = names.FirstName;
        response.LastName = names.LastName;
        response.MiddleName = names.MiddleName;
        response.PhoneNormalized = phoneNormalizer.Normalize(response.PhoneRaw);
        response.Status = ResponseStatus.InProgress;

        _ = GlobalLogger.Instance.LogAsync(
            $"New response detected for account {response.AccountId}: {response.FullName}.",
            DeskLinkAuditLogLevel.Info);
        await repository.SaveCandidateAsync(response, cancellationToken);
        await repository.AddLogAsync(new ProcessingLogItem
        {
            CandidateResponseId = response.Id,
            AccountId = response.AccountId,
            Level = "Info",
            Message = "Найден новый отклик",
            Details = response.FullName
        }, cancellationToken);

        UpdateStatus(MonitoringStatus.Running, $"Отклик \"{response.FullName}\" ({response.PhoneNormalized}): проверяем дубли в локальной базе и Bitrix24.");
        var duplicate = await duplicateService.CheckAsync(response, settings, cancellationToken);
        await repository.AddLogAsync(new ProcessingLogItem
        {
            CandidateResponseId = response.Id,
            AccountId = response.AccountId,
            Level = "Info",
            Message = "Проверка дублей выполнена",
            Details = duplicate.Summary
        }, cancellationToken);

        if (duplicate.IsDuplicate)
        {
            _ = GlobalLogger.Instance.LogAsync(
                $"Duplicate detected for response {response.Id}.",
                DeskLinkAuditLogLevel.Info);
            response.Status = ResponseStatus.Duplicate;
            response.ProcessedAt = DateTime.UtcNow;
            await repository.SaveCandidateAsync(response, cancellationToken);
            UpdateStatus(MonitoringStatus.Running, $"Отклик \"{response.FullName}\" помечен как дубль. Новая сделка в Bitrix24 не создаётся.");
            ResponseProcessed?.Invoke(this, response);
            return;
        }

        UpdateStatus(MonitoringStatus.Running, $"Отклик \"{response.FullName}\" уникален: отправляем сделку в Bitrix24.");
        var lead = await bitrixClient.CreateLeadAsync(response, settings, cancellationToken);
        response.ProcessedAt = DateTime.UtcNow;
        if (lead.IsSuccess)
        {
            _ = GlobalLogger.Instance.LogAsync(
                $"Bitrix deal created for response {response.Id}: {lead.EntityId}.",
                DeskLinkAuditLogLevel.Info);
            response.Status = ResponseStatus.Sent;
            response.BitrixEntityId = lead.EntityId;
            await repository.AddLogAsync(new ProcessingLogItem
            {
                CandidateResponseId = response.Id,
                AccountId = response.AccountId,
                Level = "Info",
                Message = "Сделка создана в Bitrix24",
                Details = lead.EntityId
            }, cancellationToken);
            UpdateStatus(MonitoringStatus.Running, $"Сделка по отклику \"{response.FullName}\" успешно создана в Bitrix24. ID: {lead.EntityId}.");
        }
        else
        {
            _ = GlobalLogger.Instance.LogAsync(
                $"Bitrix deal creation failed for response {response.Id}: {lead.Error}.",
                DeskLinkAuditLogLevel.Error);
            response.Status = ResponseStatus.Error;
            response.ErrorMessage = lead.Error;
            await repository.AddLogAsync(new ProcessingLogItem
            {
                CandidateResponseId = response.Id,
                AccountId = response.AccountId,
                Level = "Error",
                Message = "Ошибка Bitrix24",
                Details = lead.Error
            }, cancellationToken);
            UpdateStatus(MonitoringStatus.Error, $"Ошибка при создании сделки по отклику \"{response.FullName}\": {lead.Error}");
        }

        await repository.SaveCandidateAsync(response, cancellationToken);
        ResponseProcessed?.Invoke(this, response);
    }

    private async Task SyncBitrixLeadsAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        UpdateStatus(MonitoringStatus.Running, "Синхронизация с Bitrix24: загружаем существующие сделки перед стартом мониторинга.");
        var bitrixLeads = await bitrixClient.GetExistingLeadsAsync(settings, cancellationToken);
        if (bitrixLeads.Count == 0)
        {
            UpdateStatus(MonitoringStatus.Running, "Синхронизация с Bitrix24 завершена: новых сделок для импорта не найдено.");
            return;
        }

        var existingIds = await repository.GetExistingBitrixEntityIdsAsync(
            bitrixLeads.Select(x => x.BitrixEntityId),
            cancellationToken);

        var imported = 0;
        foreach (var lead in bitrixLeads)
        {
            if (existingIds.Contains(lead.BitrixEntityId))
            {
                continue;
            }

            lead.PhoneNormalized = phoneNormalizer.Normalize(lead.PhoneRaw);
            await repository.SaveCandidateAsync(lead, cancellationToken);
            imported++;
        }

        await repository.AddLogAsync(new ProcessingLogItem
        {
            Level = "Info",
            Message = "Синхронизация Bitrix24 выполнена",
            Details = $"Импортировано сделок: {imported}"
        }, cancellationToken);
        UpdateStatus(MonitoringStatus.Running, $"Синхронизация с Bitrix24 завершена: импортировано сделок {imported}.");
    }

    private static TimeSpan GetRandomCycleDelay()
    {
        var minSeconds = (int)MinCycleDelay.TotalSeconds;
        var maxSeconds = (int)MaxCycleDelay.TotalSeconds;
        return TimeSpan.FromSeconds(Random.Shared.Next(minSeconds, maxSeconds + 1));
    }

    private void UpdateStatus(MonitoringStatus status, string message)
    {
        CurrentStatus = status;
        CurrentStatusMessage = message;
        StatusChanged?.Invoke(this, status);
        StatusMessageChanged?.Invoke(this, message);
    }

    private static string FormatDelay(TimeSpan delay)
    {
        if (delay.TotalMinutes >= 1)
        {
            return $"{(int)delay.TotalMinutes} мин. {delay.Seconds} сек.";
        }

        return $"{delay.Seconds} сек.";
    }
}
