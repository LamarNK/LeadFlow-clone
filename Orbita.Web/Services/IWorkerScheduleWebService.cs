using Orbita.Contracts;

namespace Orbita.Web.Services;

public interface IWorkerScheduleWebService
{
    Task<WorkerScheduleOfficeDto?> GetAsync(int? dayOff, CancellationToken ct = default);
}
