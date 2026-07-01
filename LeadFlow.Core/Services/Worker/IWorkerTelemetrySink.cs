namespace LeadFlow.Core.Services.Worker;

/// <summary>Немедленная отправка телеметрии в панель (субпрофили, статусы аккаунтов).</summary>
public interface IWorkerTelemetrySink
{
    Task PushSnapshotAsync(CancellationToken cancellationToken = default);
}