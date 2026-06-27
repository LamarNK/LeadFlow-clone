namespace NotifyBot.Application.Options;

public sealed class TelegramOptions
{
    public const string SectionName = "Telegram";

    public string BotToken { get; set; } = string.Empty;

    public List<string> AdminUsernames { get; set; } = ["andreypakin", "LamarrNK"];

    public string WebhookUrl { get; set; } = string.Empty;

    public bool IsAdminUsername(string? username)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return false;
        }

        var normalized = username.TrimStart('@');
        return AdminUsernames.Any(x =>
            string.Equals(x.TrimStart('@'), normalized, StringComparison.OrdinalIgnoreCase));
    }
}