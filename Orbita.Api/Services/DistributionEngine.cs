using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Contracts;

namespace Orbita.Api.Services;

public sealed class DistributionEngine(OrbitaDbContext db)
{
    public async Task<DistributionPlan> GetPlanAsync(Guid officeId, CancellationToken ct = default)
    {
        var route = await db.DistributionRoutes
            .AsNoTracking()
            .Include(x => x.Nodes)
            .FirstOrDefaultAsync(x => x.OfficeId == officeId, ct);

        if (route is null || route.Nodes.Count == 0)
        {
            return new DistributionPlan(string.Empty, [], "Схема связей не настроена.");
        }

        var nodes = route.Nodes.OrderBy(x => x.SortOrder).ToList();
        if (nodes.All(x => x.ParentNodeId is null))
        {
            var broadcastTargets = await ResolveInstancesAsync(officeId, nodes, ct);
            return broadcastTargets.Error is not null
                ? new DistributionPlan(DistributionTopology.Broadcast, [], broadcastTargets.Error)
                : new DistributionPlan(DistributionTopology.Broadcast, broadcastTargets.Instances, null);
        }

        var chainNodes = TryBuildLinearChain(nodes);
        if (chainNodes is null)
        {
            return new DistributionPlan(
                string.Empty,
                [],
                "Поддерживаются только отдельные узлы (рассылка во все) или одна цепочка B1→B2→B3.");
        }

        var chainTargets = await ResolveInstancesAsync(officeId, chainNodes, ct);
        return chainTargets.Error is not null
            ? new DistributionPlan(DistributionTopology.ChainFallback, [], chainTargets.Error)
            : new DistributionPlan(DistributionTopology.ChainFallback, chainTargets.Instances, null);
    }

    private async Task<(IReadOnlyList<BitrixInstanceEntity> Instances, string? Error)> ResolveInstancesAsync(
        Guid officeId,
        IReadOnlyList<DistributionNodeEntity> nodes,
        CancellationToken ct)
    {
        var targets = new List<BitrixInstanceEntity>();
        foreach (var node in nodes)
        {
            var instance = await db.BitrixInstances
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == node.BitrixInstanceId && x.OfficeId == officeId && x.IsEnabled, ct);
            if (instance is null)
            {
                return ([], "Схема содержит отключённый или неизвестный Битрикс.");
            }

            if (!BitrixValidationStatuses.AllowsWebhookUsage(instance.ValidationStatus))
            {
                return ([], $"Битрикс «{instance.Name}» не прошёл валидацию вебхука.");
            }

            targets.Add(instance);
        }

        return (targets, null);
    }

    internal static List<DistributionNodeEntity>? TryBuildLinearChain(IReadOnlyList<DistributionNodeEntity> nodes)
    {
        var roots = nodes.Where(x => x.ParentNodeId is null).OrderBy(x => x.SortOrder).ToList();
        if (roots.Count != 1)
        {
            return null;
        }

        var chain = new List<DistributionNodeEntity>();
        var current = roots[0];
        var visited = new HashSet<Guid>();
        while (true)
        {
            if (!visited.Add(current.Id))
            {
                return null;
            }

            chain.Add(current);
            var children = nodes
                .Where(x => x.ParentNodeId == current.Id)
                .OrderBy(x => x.SortOrder)
                .ToList();
            if (children.Count == 0)
            {
                break;
            }

            if (children.Count > 1)
            {
                return null;
            }

            current = children[0];
        }

        return chain.Count == nodes.Count ? chain : null;
    }
}