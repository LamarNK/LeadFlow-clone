using System.Text;
using System.Text.Json;

namespace LeadFlow.Core.Services.Avito;

/// <summary>
/// Снимок модалки «Выбор профиля» и правила, когда переключение считается успешным.
/// Успех — только проверяемый эффект: target стал current и модалка закрылась.
/// </summary>
public sealed record AvitoSubProfileSwitchSnapshot(
    bool ModalOpen,
    int CardsCount,
    bool TargetCardFound,
    string? CurrentSubProfileId,
    string? CurrentSubProfileName,
    string? Url)
{
    public static AvitoSubProfileSwitchSnapshot Empty { get; } = new(false, 0, false, null, null, null);
}

public static class AvitoSubProfileSwitchEffect
{
    public static bool IsItemsSwitchTrapUrl(string? url) =>
        !string.IsNullOrEmpty(url)
        && url.Contains("/profile/pro/items", StringComparison.OrdinalIgnoreCase)
        && url.Contains("profile/switch", StringComparison.OrdinalIgnoreCase);

    public static bool IsDashboardSwitchUrl(string? url) =>
        !string.IsNullOrEmpty(url)
        && url.Contains("/profile/dashboard", StringComparison.OrdinalIgnoreCase)
        && url.Contains("profile/switch", StringComparison.OrdinalIgnoreCase);

    public static bool CanReuseOpenModal(bool modalOpen, int cardsCount, string? url) =>
        modalOpen && cardsCount > 0 && !IsItemsSwitchTrapUrl(url);

    public static bool CanReuseOpenModal(AvitoSubProfileSwitchSnapshot snapshot) =>
        CanReuseOpenModal(snapshot.ModalOpen, snapshot.CardsCount, snapshot.Url);

    public static bool IdsEqual(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left)
        && !string.IsNullOrWhiteSpace(right)
        && string.Equals(left.Trim(), right.Trim(), StringComparison.Ordinal);

    public static bool IsAlreadyCurrent(AvitoSubProfileSwitchSnapshot snapshot, string targetId) =>
        snapshot.TargetCardFound && IdsEqual(snapshot.CurrentSubProfileId, targetId);

    public static bool ShouldClickTarget(AvitoSubProfileSwitchSnapshot snapshot, string targetId) =>
        snapshot.ModalOpen
        && snapshot.TargetCardFound
        && !IdsEqual(snapshot.CurrentSubProfileId, targetId);

    public static bool IsSuccessfulSwitch(AvitoSubProfileSwitchSnapshot after, string targetId) =>
        IdsEqual(after.CurrentSubProfileId, targetId) && !after.ModalOpen;

    /// <summary>
    /// После закрытия модалки карточки удаляются из DOM, поэтому current id в новом snapshot
    /// может исчезнуть. Сохраняем подтверждение target, полученное до закрытия, и требуем,
    /// чтобы сам корень модалки действительно исчез.
    /// </summary>
    public static bool IsSuccessfulSwitchAfterModalDismissal(
        AvitoSubProfileSwitchSnapshot after,
        string targetId,
        bool targetWasCurrentBeforeDismissal) =>
        !after.ModalOpen
        && (IdsEqual(after.CurrentSubProfileId, targetId)
            || (targetWasCurrentBeforeDismissal
                && string.IsNullOrWhiteSpace(after.CurrentSubProfileId)));

    public static bool TargetBecameCurrent(AvitoSubProfileSwitchSnapshot after, string targetId) =>
        IdsEqual(after.CurrentSubProfileId, targetId);

    /// <summary>
    /// JS-клик вернул true, но current id не сменился и модалка осталась — это не успех.
    /// </summary>
    public static bool ClickReportedSuccessWithoutDomChange(
        AvitoSubProfileSwitchSnapshot before,
        AvitoSubProfileSwitchSnapshot after,
        string targetId,
        bool clickReportedSuccess) =>
        clickReportedSuccess
        && after.ModalOpen
        && !IdsEqual(after.CurrentSubProfileId, targetId)
        && IdsEqual(before.CurrentSubProfileId, after.CurrentSubProfileId);

    public static bool PointerClickNeedsNativeFallback(
        bool pointerReportedSuccess,
        AvitoSubProfileSwitchSnapshot afterPointer,
        string targetId) =>
        pointerReportedSuccess
        && afterPointer.ModalOpen
        && !IdsEqual(afterPointer.CurrentSubProfileId, targetId);

    /// <summary>
    /// Модалка закрыта только когда нет корня switch. Нельзя OR-ить с <c>[role=dialog]</c>:
    /// карточки Avito — <c>role=button</c>, отдельного dialog может не быть, и старый предикат
    /// сразу считал модалку закрытой.
    /// </summary>
    public static string BuildModalClosedPredicateJs() =>
        "() => !document.querySelector(\"[data-marker='component-profile-switch/root']\")";

    public static string BuildCardSelector(string subProfileId) =>
        $"[data-marker='component-profile-switch/profile-{Escape(subProfileId)}']";

    public static string BuildSnapshotScript(string subProfileId)
    {
        var id = Escape(subProfileId);
        return $$"""
            (() => {
                const targetId = "{{id}}";
                const root = document.querySelector("[data-marker='component-profile-switch/root']");
                const cards = document.querySelectorAll("[data-marker^='component-profile-switch/profile-']");
                const target = document.querySelector('[data-marker="component-profile-switch/profile-' + targetId + '"]');
                let currentId = null;
                let currentName = null;
                const isCurrentCard = (card) => {
                    const cls = String(card.className || "");
                    if (/isCurrent/i.test(cls)) return true;
                    if (card.getAttribute("aria-current") === "true") return true;
                    if (card.getAttribute("aria-checked") === "true") return true;
                    if (card.querySelector("[class*='isCurrent' i]")) return true;
                    const check = card.querySelector("svg, [class*='check' i], [class*='Check' i]");
                    return !!check && /isCurrent/i.test(
                        String(check.className || "") + String(check.getAttribute("class") || ""));
                };
                for (const card of cards) {
                    if (!isCurrentCard(card)) continue;
                    const marker = card.getAttribute("data-marker") || "";
                    const idMatch = marker.match(/profile-(\d+)/);
                    if (!idMatch) continue;
                    currentId = idMatch[1];
                    currentName = (card.querySelector("h5")?.textContent || "").trim() || null;
                    break;
                }
                return JSON.stringify({
                    modalOpen: !!root,
                    cardsCount: cards.length,
                    targetCardFound: !!target,
                    currentSubProfileId: currentId,
                    currentSubProfileName: currentName,
                    url: window.location.href || ""
                });
            })()
            """;
    }

    public static AvitoSubProfileSwitchSnapshot? ParseSnapshot(string? raw)
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
            return new AvitoSubProfileSwitchSnapshot(
                root.TryGetProperty("modalOpen", out var modal) && modal.ValueKind == JsonValueKind.True,
                root.TryGetProperty("cardsCount", out var cards) && cards.TryGetInt32(out var count) ? count : 0,
                root.TryGetProperty("targetCardFound", out var found) && found.ValueKind == JsonValueKind.True,
                root.TryGetProperty("currentSubProfileId", out var id) ? id.GetString() : null,
                root.TryGetProperty("currentSubProfileName", out var name) ? name.GetString() : null,
                root.TryGetProperty("url", out var url) ? url.GetString() : null);
        }
        catch
        {
            return null;
        }
    }

    public static string Escape(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\'': sb.Append("\\'"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(c); break;
            }
        }

        return sb.ToString();
    }

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
