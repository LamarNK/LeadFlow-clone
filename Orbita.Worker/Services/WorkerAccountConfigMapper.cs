using LeadFlow.Core.Models;
using LeadFlow.Core.Services.Worker;
using Orbita.Contracts;

namespace Orbita.Worker.Services;

internal static class WorkerAccountConfigMapper
{
    public static AvitoAccount ToAccount(
        WorkerAccountConfigDto dto,
        WorkerConfigDto config,
        string defaultBaseUrl) =>
        WorkerAccountRuntimeMapper.ToAccount(dto, config, defaultBaseUrl);
}