using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LeadFlow.Core.Services.Captcha;

public sealed class RuCaptchaClient(HttpClient http) : IRuCaptchaClient
{
    public const string DefaultBaseUrl = "https://api.rucaptcha.com/";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(8);
    public TimeSpan SolveTimeout { get; init; } = TimeSpan.FromSeconds(120);

    public async Task<GeeTestV4Solution> SolveGeeTestV4Async(
        string apiKey,
        string websiteUrl,
        string captchaId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new RuCaptchaException("Не задан API-ключ RuCaptcha.");
        }

        if (string.IsNullOrWhiteSpace(websiteUrl))
        {
            throw new RuCaptchaException("Не задан websiteURL для GeeTest v4.");
        }

        if (string.IsNullOrWhiteSpace(captchaId))
        {
            throw new RuCaptchaException("Не задан captcha_id для GeeTest v4.");
        }

        var taskId = await CreateTaskAsync(apiKey.Trim(), websiteUrl.Trim(), captchaId.Trim(), cancellationToken)
            .ConfigureAwait(false);
        return await WaitForResultAsync(apiKey.Trim(), taskId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<long> CreateTaskAsync(
        string apiKey,
        string websiteUrl,
        string captchaId,
        CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, object?>
        {
            ["clientKey"] = apiKey,
            ["task"] = new Dictionary<string, object?>
            {
                ["type"] = "GeeTestTaskProxyless",
                ["websiteURL"] = websiteUrl,
                ["version"] = 4,
                ["initParameters"] = new Dictionary<string, string>
                {
                    ["captcha_id"] = captchaId
                }
            }
        };

        using var response = await http.PostAsJsonAsync("createTask", body, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new RuCaptchaException($"RuCaptcha createTask HTTP {(int)response.StatusCode}: {Trim(json)}");
        }

        return RuCaptchaResponseParser.ParseCreateTaskId(json);
    }

    private async Task<GeeTestV4Solution> WaitForResultAsync(
        string apiKey,
        long taskId,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + SolveTimeout;
        var firstWait = PollInterval < TimeSpan.FromSeconds(5) ? PollInterval : TimeSpan.FromSeconds(5);
        await Task.Delay(firstWait, cancellationToken).ConfigureAwait(false);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var json = await GetTaskResultJsonAsync(apiKey, taskId, cancellationToken).ConfigureAwait(false);
            var solution = RuCaptchaResponseParser.ParseTaskResult(json, out var pending);
            if (solution is not null)
            {
                return solution;
            }

            if (!pending || DateTime.UtcNow >= deadline)
            {
                throw new RuCaptchaException($"RuCaptcha: решение GeeTest v4 не получено за {SolveTimeout.TotalSeconds:0} с.");
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<string> GetTaskResultJsonAsync(string apiKey, long taskId, CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, object?>
        {
            ["clientKey"] = apiKey,
            ["taskId"] = taskId
        };

        using var response = await http.PostAsJsonAsync("getTaskResult", body, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new RuCaptchaException($"RuCaptcha getTaskResult HTTP {(int)response.StatusCode}: {Trim(json)}");
        }

        return json;
    }

    private static string Trim(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var t = text.Trim();
        return t.Length <= 400 ? t : t[..400];
    }
}
