using System.Text.Json;

namespace LeadFlow.Core.Services.Avito;

/// <summary>Парсинг JS-probe состояния страницы Avito.</summary>
public static class AvitoPageStateProbe
{
    public static async Task<AvitoPageState?> TryProbeAsync(
        Func<string, CancellationToken, Task<string>> executeScript,
        CancellationToken cancellationToken)
    {
        var raw = await executeScript(AvitoPageStateScripts.BuildProbeScript(), cancellationToken)
            .ConfigureAwait(false);
        return TryParse(raw);
    }

    public static AvitoPageState? TryParse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            var text = UnwrapJsonString(raw);
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;

            var kind = ParsePageKind(root.TryGetProperty("pageKind", out var k) ? k.GetString() : null);
            return new AvitoPageState(
                kind,
                root.TryGetProperty("url", out var url) ? url.GetString() : null,
                root.TryGetProperty("title", out var title) ? title.GetString() : null,
                root.TryGetProperty("profileSwitchModalOpen", out var modal) && modal.ValueKind == JsonValueKind.True,
                root.TryGetProperty("profileCardsCount", out var cards) ? cards.GetInt32() : 0,
                root.TryGetProperty("currentSubProfileId", out var spId) ? spId.GetString() : null,
                root.TryGetProperty("currentSubProfileName", out var spName) ? spName.GetString() : null,
                root.TryGetProperty("candidatesItemCount", out var items) ? items.GetInt32() : 0,
                root.TryGetProperty("hasLoginForm", out var login) && login.ValueKind == JsonValueKind.True,
                root.TryGetProperty("hasCaptcha", out var captcha) && captcha.ValueKind == JsonValueKind.True,
                root.TryGetProperty("hasFirewallIp", out var firewall) && firewall.ValueKind == JsonValueKind.True);
        }
        catch
        {
            return null;
        }
    }

    private static AvitoPageKind ParsePageKind(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "candidates" => AvitoPageKind.Candidates,
        "dashboard" => AvitoPageKind.Dashboard,
        "profileitems" => AvitoPageKind.ProfileItems,
        "profileswitchmodal" => AvitoPageKind.ProfileSwitchModal,
        "login" => AvitoPageKind.Login,
        "captcha" => AvitoPageKind.Captcha,
        _ => AvitoPageKind.Unknown
    };

    private static string UnwrapJsonString(string raw)
    {
        var t = raw.Trim();
        if (t.Length >= 2 && t.StartsWith('"') && t.EndsWith('"'))
        {
            try
            {
                return JsonSerializer.Deserialize<string>(t) ?? t;
            }
            catch
            {
                return t;
            }
        }

        return t;
    }
}