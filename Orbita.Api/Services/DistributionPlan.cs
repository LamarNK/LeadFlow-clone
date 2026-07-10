using Orbita.Api.Data;

namespace Orbita.Api.Services;

public sealed record DistributionPlan(
    string Topology,
    IReadOnlyList<BitrixInstanceEntity> Targets,
    string? Error);