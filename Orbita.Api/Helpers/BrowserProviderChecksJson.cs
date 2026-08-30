using System.Text.Json;
using LeadFlow.Core.Services.Worker;
using Orbita.Contracts;

namespace Orbita.Api.Helpers;

internal sealed class BrowserProviderCheckSnapshot
{
    public bool Ok { get; set; }
    public DateTime AtUtc { get; set; }
    public string? Message { get; set; }
    public int? Profiles { get; set; }
    public int? Groups { get; set; }
    public string? Path { get; set; }
}

internal sealed class BrowserProviderChecksState
{
    public BrowserProviderCheckSnapshot? AdsPower { get; set; }
    public BrowserProviderCheckSnapshot? Multilogin { get; set; }
    public BrowserProviderCheckSnapshot? Local { get; set; }
}

internal static class BrowserProviderChecksJson
{
    private const int JsonMaxLength = 4000;
    private const int PathMaxLength = 512;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public static BrowserProviderChecksState Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "{}")
        {
            return new BrowserProviderChecksState();
        }

        try
        {
            return JsonSerializer.Deserialize<BrowserProviderChecksState>(json, JsonOptions)
                   ?? new BrowserProviderChecksState();
        }
        catch (JsonException)
        {
            return new BrowserProviderChecksState();
        }
    }

    public static string? Serialize(BrowserProviderChecksState state)
    {
        var json = JsonSerializer.Serialize(state, JsonOptions);
        if (json is "{}" or """{"adsPower":null,"multilogin":null,"local":null}""")
        {
            return null;
        }

        return json.Length <= JsonMaxLength ? json : json[..JsonMaxLength];
    }

    public static BrowserProviderCheckSnapshot? Get(BrowserProviderChecksState state, string provider) =>
        provider switch
        {
            WorkerBrowserProviderKinds.AdsPower => state.AdsPower,
            WorkerBrowserProviderKinds.Multilogin => state.Multilogin,
            WorkerBrowserProviderKinds.Local => state.Local,
            _ => null
        };

    public static void Set(BrowserProviderChecksState state, string provider, BrowserProviderCheckSnapshot? snapshot)
    {
        switch (provider)
        {
            case WorkerBrowserProviderKinds.AdsPower:
                state.AdsPower = snapshot;
                break;
            case WorkerBrowserProviderKinds.Multilogin:
                state.Multilogin = snapshot;
                break;
            case WorkerBrowserProviderKinds.Local:
                state.Local = snapshot;
                break;
        }
    }

    public static void Clear(BrowserProviderChecksState state, string provider) =>
        Set(state, provider, null);

    public static BrowserProviderCheckSnapshot FromReport(
        ReportWorkerBrowserProviderCheckRequest request,
        DateTime atUtc,
        params string?[] secrets)
    {
        var path = string.IsNullOrWhiteSpace(request.ResolvedExecutablePath)
            ? null
            : request.ResolvedExecutablePath.Trim();
        if (path is { Length: > PathMaxLength })
        {
            path = path[..PathMaxLength];
        }

        return new BrowserProviderCheckSnapshot
        {
            Ok = request.Success,
            AtUtc = atUtc,
            Message = BrowserProviderProbeSanitizer.Sanitize(request.Message, secrets),
            Profiles = request.ProfileCount is >= 0 ? request.ProfileCount : null,
            Groups = request.GroupCount is >= 0 ? request.GroupCount : null,
            Path = path
        };
    }

    public static WorkerBrowserProviderCheckDto ToDto(
        string provider,
        bool enabled,
        bool needsSetup,
        string? pendingCheckProvider,
        BrowserProviderCheckSnapshot? last)
    {
        var checking = string.Equals(pendingCheckProvider, provider, StringComparison.OrdinalIgnoreCase);
        var status = WorkerBrowserProviderStatus.Resolve(
            enabled,
            needsSetup,
            checking,
            last is null ? null : last.Ok);
        var canCheck = enabled && !needsSetup && !checking;
        var canSync = enabled
            && !needsSetup
            && !checking
            && status == WorkerBrowserProviderStatus.Connected
            && WorkerBrowserProviderKinds.SupportsCatalogSync(provider);
        var message = status switch
        {
            WorkerBrowserProviderStatus.Disabled => WorkerBrowserProviderMessages.DisabledHint,
            WorkerBrowserProviderStatus.Checking => WorkerBrowserProviderMessages.CheckingMessage,
            WorkerBrowserProviderStatus.NeedsSetup => provider == WorkerBrowserProviderKinds.Multilogin
                ? WorkerBrowserProviderMessages.NeedsToken
                : last?.Message,
            _ => last?.Message
        };

        return new WorkerBrowserProviderCheckDto(
            provider,
            status,
            WorkerBrowserProviderStatus.Label(status),
            message,
            checking ? null : last?.AtUtc,
            last?.Profiles,
            last?.Groups,
            last?.Path,
            canCheck,
            canSync);
    }
}
