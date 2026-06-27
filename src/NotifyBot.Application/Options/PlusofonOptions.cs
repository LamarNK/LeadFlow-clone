namespace NotifyBot.Application.Options;

public sealed class PlusofonOptions
{
    public const string SectionName = "Plusofon";

    public string Secret { get; set; } = string.Empty;

    public bool WebhookValidation { get; set; } = true;
}