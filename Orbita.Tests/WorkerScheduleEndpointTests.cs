using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Orbita.Api.Endpoints;
using Orbita.Api.Services;
using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class WorkerScheduleEndpointTests
{
    [Fact]
    public void ScheduleApiEndpoints_RequireSchedulePermission()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddScoped<WorkerScheduleManager>();
        builder.Services.AddScoped<OfficeScopeService>();
        using var app = builder.Build();
        WorkerScheduleEndpoints.Map(app);

        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(x => x.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(x => x.RoutePattern.RawText?.StartsWith("/api/v1/schedule", StringComparison.Ordinal) == true)
            .ToList();

        Assert.Equal(7, endpoints.Count);
        Assert.All(endpoints, endpoint =>
            Assert.Contains(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(),
                authorization => authorization.Policy == PanelPermissions.Schedule));
    }
}
