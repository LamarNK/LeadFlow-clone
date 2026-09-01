namespace Orbita.Api.Options;

public sealed class CerioAiOptions
{
    public const string SectionName = "CerioAi";

    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = "https://api.cerio.ru/";
    public string Token { get; set; } = string.Empty;
    public int BatchSize { get; set; } = 3;
    public int MaxParallelism { get; set; } = 3;
    public int MaxAttempts { get; set; } = 8;
    public int PollIntervalSeconds { get; set; } = 30;
    public int RequestTimeoutSeconds { get; set; } = 300;
    public DateTime? ProcessRecordingsFromUtc { get; set; }
    public string PromptVersion { get; set; } = "recruiting-v1";
    public string? AnalysisPrompt { get; set; }
}
