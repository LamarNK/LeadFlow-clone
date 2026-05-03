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
    public event EventHandler<CandidateResponse>? ResponseProcessed;

    public MonitoringStatus CurrentStatus { get; private set; } = MonitoringStatus.Waiting;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_loopTask is { IsCompleted: false })
        {
            return Task.CompletedTask;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = GlobalLogger.Instance.LogAsync("Monitoring started.", DeskLinkAuditLogLevel.Info);
        CurrentStatus = MonitoringStatus.Running;
        StatusChanged?.Invoke(this, CurrentStatus);
        _loopTask = RunAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_cts is null)
        {
            return;
        }

        _cts.Cancel();
        if (_loopTask is not null)
        {
            await _loopTask;
        }

        CurrentStatus = MonitoringStatus.Stopped;
        StatusChanged?.Invoke(this, CurrentStatus);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var settings = await settingsService.LoadAsync(cancellationToken);
                var accounts = settings.Avito.Accounts.Where(x => x.IsEnabled).ToList();

                foreach (var account in accounts)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    await ProcessAccountAsync(account, settings, cancellationToken);
                    await Task.Delay(TimeSpan.FromSeconds(settings.MonitoringSafety.DelayBetweenAccountsSeconds), cancellationToken);
                }

                CurrentStatus = MonitoringStatus.Waiting;
                StatusChanged?.Invoke(this, CurrentStatus);
                await Task.Delay(GetRandomCycleDelay(), cancellationToken);
                CurrentStatus = MonitoringStatus.Running;
                StatusChanged?.Invoke(this, CurrentStatus);
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
            CurrentStatus = MonitoringStatus.Error;
            StatusChanged?.Invoke(this, CurrentStatus);
        }
    }

    private async Task ProcessAccountAsync(AvitoAccount account, AppSettings settings, CancellationToken cancellationToken)
    {
        if (account.Status is AvitoAccountStatus.RequiresLogin or AvitoAccountStatus.RequiresManualAction or AvitoAccountStatus.Paused)
        {
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
        _ = GlobalLogger.Instance.LogAsync(
            $"Processing account {account.DisplayName}.",
            DeskLinkAuditLogLevel.Info);
        await repository.SaveAccountAsync(account, cancellationToken);

        var responses = settings.DemoModeEnabled
            ? await avitoDemoResponseSource.GetBatchAsync(account, Math.Max(1, settings.MonitoringSafety.MaxResponsesPerCycle / 2), cancellationToken)
            : await avitoResponseSource.GetNewResponsesAsync(account, settings, cancellationToken);

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
            ResponseProcessed?.Invoke(this, response);
            return;
        }

        var lead = await bitrixClient.CreateLeadAsync(response, settings, cancellationToken);
        response.ProcessedAt = DateTime.UtcNow;
        if (lead.IsSuccess)
        {
            _ = GlobalLogger.Instance.LogAsync(
                $"Bitrix lead created for response {response.Id}: {lead.EntityId}.",
                DeskLinkAuditLogLevel.Info);
            response.Status = ResponseStatus.Sent;
            response.BitrixEntityId = lead.EntityId;
            await repository.AddLogAsync(new ProcessingLogItem
            {
                CandidateResponseId = response.Id,
                AccountId = response.AccountId,
                Level = "Info",
                Message = "Лид создан в Bitrix24",
                Details = lead.EntityId
            }, cancellationToken);
        }
        else
        {
            _ = GlobalLogger.Instance.LogAsync(
                $"Bitrix lead creation failed for response {response.Id}: {lead.Error}.",
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
        }

        await repository.SaveCandidateAsync(response, cancellationToken);
        ResponseProcessed?.Invoke(this, response);
    }

    private static TimeSpan GetRandomCycleDelay()
    {
        var minSeconds = (int)MinCycleDelay.TotalSeconds;
        var maxSeconds = (int)MaxCycleDelay.TotalSeconds;
        return TimeSpan.FromSeconds(Random.Shared.Next(minSeconds, maxSeconds + 1));
    }
}
