using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Contracts;

namespace Orbita.Api.Services;

public sealed class DistributionRouteService(OrbitaDbContext db, PanelAuditService audit)
{
    public async Task<DistributionRouteDto?> GetAsync(
        OfficeScope scope,
        Guid? officeId,
        CancellationToken ct = default)
    {
        var (targetOfficeId, _) = OfficeIdResolver.Resolve(scope, officeId);
        if (targetOfficeId is not Guid resolvedOfficeId)
        {
            return null;
        }

        var route = await db.DistributionRoutes
            .AsNoTracking()
            .Include(x => x.Nodes)
            .ThenInclude(x => x.BitrixInstance)
            .FirstOrDefaultAsync(x => x.OfficeId == resolvedOfficeId, ct);

        if (route is null)
        {
            return new DistributionRouteDto(
                Guid.Empty,
                resolvedOfficeId,
                false,
                [],
                null);
        }

        return MapDto(route);
    }

    public async Task<(DistributionRouteDto? Route, string? Error)> SaveAsync(
        OfficeScope scope,
        Guid? officeId,
        SaveDistributionRouteRequest request,
        string actorUserId,
        CancellationToken ct = default)
    {
        var (targetOfficeId, error) = OfficeIdResolver.Resolve(scope, officeId);
        if (targetOfficeId is not Guid resolvedOfficeId)
        {
            return (null, error);
        }

        var normalizedNodes = NormalizeNodes(request.Nodes);
        var validationError = await ValidateNodesAsync(resolvedOfficeId, normalizedNodes, ct);
        if (validationError is not null)
        {
            return (null, validationError);
        }

        var route = await db.DistributionRoutes
            .Include(x => x.Nodes)
            .FirstOrDefaultAsync(x => x.OfficeId == resolvedOfficeId, ct);

        var now = DateTime.UtcNow;
        if (route is null)
        {
            route = new DistributionRouteEntity
            {
                Id = Guid.NewGuid(),
                OfficeId = resolvedOfficeId,
                IsAutoDistributionEnabled = request.IsAutoDistributionEnabled,
                UpdatedAtUtc = now,
                UpdatedByUserId = actorUserId
            };
            db.DistributionRoutes.Add(route);
        }
        else
        {
            db.DistributionNodes.RemoveRange(route.Nodes);
            var staleStates = await db.DistributionRoundRobinStates
                .Where(x => x.RouteId == route.Id)
                .ToListAsync(ct);
            db.DistributionRoundRobinStates.RemoveRange(staleStates);
            route.IsAutoDistributionEnabled = request.IsAutoDistributionEnabled;
            route.UpdatedAtUtc = now;
            route.UpdatedByUserId = actorUserId;
        }

        var office = await db.Offices.FindAsync([resolvedOfficeId], ct);
        if (office is not null)
        {
            office.BitrixTransmissionEnabled = request.IsAutoDistributionEnabled;
        }

        foreach (var node in normalizedNodes.OrderBy(x => x.SortOrder))
        {
            db.DistributionNodes.Add(new DistributionNodeEntity
            {
                Id = node.Id!.Value,
                RouteId = route.Id,
                ParentNodeId = node.ParentNodeId,
                BitrixInstanceId = node.BitrixInstanceId,
                SortOrder = node.SortOrder,
                EditorPositionX = node.EditorPositionX,
                EditorPositionY = node.EditorPositionY
            });
        }

        if (request.BitrixLeadQuotas is { Count: > 0 })
        {
            var bitrixIds = request.BitrixLeadQuotas.Select(x => x.BitrixInstanceId).Distinct().ToList();
            var instances = await db.BitrixInstances
                .Where(x => x.OfficeId == resolvedOfficeId && bitrixIds.Contains(x.Id))
                .ToListAsync(ct);
            foreach (var quota in request.BitrixLeadQuotas)
            {
                var instance = instances.FirstOrDefault(x => x.Id == quota.BitrixInstanceId);
                if (instance is not null)
                {
                    instance.LeadExportLimit = NormalizeLeadExportLimit(quota.LeadExportLimit);
                }
            }
        }

        await db.SaveChangesAsync(ct);

        await audit.LogAsync(
            actorUserId,
            null,
            PanelAuditOfficeActions.DistributionRouteUpdated,
            "distribution_route",
            route.Id.ToString(),
            $"nodes={normalizedNodes.Count};auto={request.IsAutoDistributionEnabled}",
            null,
            ct);

        var saved = await db.DistributionRoutes
            .AsNoTracking()
            .Include(x => x.Nodes)
            .ThenInclude(x => x.BitrixInstance)
            .FirstAsync(x => x.Id == route.Id, ct);

        return (MapDto(saved), null);
    }

    public async Task<bool> IsAutoDistributionEnabledAsync(Guid officeId, CancellationToken ct = default)
    {
        var route = await db.DistributionRoutes
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.OfficeId == officeId, ct);
        if (route is not null)
        {
            return route.IsAutoDistributionEnabled;
        }

        var office = await db.Offices.AsNoTracking()
            .Where(x => x.Id == officeId)
            .Select(x => new { x.BitrixTransmissionEnabled })
            .FirstOrDefaultAsync(ct);
        return office?.BitrixTransmissionEnabled ?? false;
    }

    private static List<SaveDistributionNodeRequest> NormalizeNodes(IReadOnlyList<SaveDistributionNodeRequest> nodes)
    {
        if (nodes.Count == 0)
        {
            return [];
        }

        return nodes
            .Select(node => new SaveDistributionNodeRequest(
                node.Id ?? Guid.NewGuid(),
                node.ParentNodeId,
                node.BitrixInstanceId,
                node.SortOrder,
                node.EditorPositionX,
                node.EditorPositionY))
            .ToList();
    }

    private async Task<string?> ValidateNodesAsync(
        Guid officeId,
        IReadOnlyList<SaveDistributionNodeRequest> nodes,
        CancellationToken ct)
    {
        if (nodes.Count == 0)
        {
            return null;
        }

        var bitrixIds = nodes.Select(x => x.BitrixInstanceId).Distinct().ToList();
        var validBitrixCount = await db.BitrixInstances
            .CountAsync(x => x.OfficeId == officeId && x.IsEnabled && bitrixIds.Contains(x.Id), ct);
        if (validBitrixCount != bitrixIds.Count)
        {
            return "Схема содержит неизвестный или отключённый Битрикс.";
        }

        var idSet = nodes.Select(x => x.Id!.Value).ToHashSet();
        if (idSet.Count != nodes.Count)
        {
            return "Схема содержит повторяющиеся идентификаторы узлов.";
        }

        foreach (var node in nodes)
        {
            if (node.ParentNodeId is Guid parentId)
            {
                if (parentId == node.Id)
                {
                    return "Узел не может быть родителем самому себе.";
                }

                if (!idSet.Contains(parentId))
                {
                    return "Некорректная схема связей: родительский узел не найден.";
                }
            }
        }

        foreach (var node in nodes)
        {
            if (HasCycle(node.Id!.Value, nodes))
            {
                return "Схема связей содержит цикл.";
            }
        }

        var childLookup = nodes
            .Where(x => x.ParentNodeId is Guid parentId)
            .GroupBy(x => x.ParentNodeId!.Value)
            .ToDictionary(x => x.Key, x => x.Select(n => n.Id!.Value).ToList());

        foreach (var (parentId, children) in childLookup)
        {
            if (children.Count != children.Distinct().Count())
            {
                return "Схема связей содержит дублирующиеся дочерние узлы.";
            }

            if (!idSet.Contains(parentId))
            {
                return "Некорректная схема связей: родительский узел не найден.";
            }
        }

        var roots = nodes.Where(x => x.ParentNodeId is null).Select(x => x.Id!.Value).ToHashSet();
        if (roots.Count == 0)
        {
            return "Схема связей должна содержать хотя бы один узел верхнего уровня.";
        }

        var reachable = new HashSet<Guid>(roots);
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var node in nodes)
            {
                if (node.ParentNodeId is Guid parentId
                    && reachable.Contains(parentId)
                    && reachable.Add(node.Id!.Value))
                {
                    changed = true;
                }
            }
        }

        if (reachable.Count != nodes.Count)
        {
            return "Схема связей содержит узлы, не связанные с верхним уровнем.";
        }

        return null;
    }

    private static bool HasCycle(Guid nodeId, IReadOnlyList<SaveDistributionNodeRequest> nodes)
    {
        var parentById = nodes.ToDictionary(x => x.Id!.Value, x => x.ParentNodeId);
        var visited = new HashSet<Guid>();
        var current = nodeId;
        while (parentById.TryGetValue(current, out var parent) && parent is Guid parentId)
        {
            if (!visited.Add(parentId))
            {
                return true;
            }

            current = parentId;
        }

        return false;
    }

    private static DistributionRouteDto MapDto(DistributionRouteEntity route)
    {
        var childCounts = route.Nodes
            .Where(x => x.ParentNodeId is not null)
            .GroupBy(x => x.ParentNodeId!.Value)
            .ToDictionary(x => x.Key, x => x.Count());

        var nodes = route.Nodes
            .OrderBy(x => x.SortOrder)
            .Select(x => new DistributionNodeDto(
                x.Id,
                x.ParentNodeId,
                x.BitrixInstanceId,
                x.BitrixInstance.Name,
                x.BitrixInstance.Signature,
                x.SortOrder,
                x.EditorPositionX,
                x.EditorPositionY,
                childCounts.ContainsKey(x.Id)))
            .ToList();

        return new DistributionRouteDto(
            route.Id,
            route.OfficeId,
            route.IsAutoDistributionEnabled,
            nodes,
            route.UpdatedAtUtc);
    }

    private static int? NormalizeLeadExportLimit(int? limit) =>
        limit is > 0 ? limit : null;
}