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
        => await SolveGeeTestV4Async(apiKey, websiteUrl, captchaId, taskOptions: null, cancellationToken)
            .ConfigureAwait(false);

    public async Task<GeeTestV4Solution> SolveGeeTestV4Async(
        string apiKey,
        string websiteUrl,
        string captchaId,
        GeeTestV4TaskOptions? taskOptions,
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

        var taskId = await CreateTaskAsync(
                apiKey.Trim(),
                websiteUrl.Trim(),
                captchaId.Trim(),
                taskOptions,
                cancellationToken)
            .ConfigureAwait(false);
        return await WaitForResultAsync(apiKey.Trim(), taskId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<HCaptchaSolution> SolveHCaptchaAsync(
        string apiKey,
        string websiteUrl,
        string websiteKey,
        GeeTestV4TaskOptions? taskOptions,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new RuCaptchaException("Не задан API-ключ RuCaptcha.");
        }

        if (string.IsNullOrWhiteSpace(websiteUrl))
        {
            throw new RuCaptchaException("Не задан websiteURL для hCaptcha.");
        }

        if (string.IsNullOrWhiteSpace(websiteKey))
        {
            throw new RuCaptchaException("Не задан websiteKey для hCaptcha.");
        }

        var taskId = await CreateHCaptchaTaskAsync(
                apiKey.Trim(), websiteUrl.Trim(), websiteKey.Trim(), taskOptions, cancellationToken)
            .ConfigureAwait(false);
        return await WaitForHCaptchaResultAsync(apiKey.Trim(), taskId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ImageCaptchaSolution> SolveImageToTextAsync(
        string apiKey,
        string imageBody,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new RuCaptchaException("Не задан API-ключ RuCaptcha.");
        }

        var body = NormalizeImageBody(imageBody);
        if (string.IsNullOrWhiteSpace(body))
        {
            throw new RuCaptchaException("Не получено изображение внутренней капчи Avito.");
        }

        var taskId = await CreateImageToTextTaskAsync(apiKey.Trim(), body, cancellationToken).ConfigureAwait(false);
        return await WaitForImageToTextResultAsync(apiKey.Trim(), taskId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<long> CreateTaskAsync(
        string apiKey,
        string websiteUrl,
        string captchaId,
        GeeTestV4TaskOptions? taskOptions,
        CancellationToken cancellationToken)
    {
        var proxy = taskOptions?.Proxy;
        var body = new Dictionary<string, object?>
        {
            ["clientKey"] = apiKey,
            ["task"] = new Dictionary<string, object?>
            {
                ["type"] = proxy is null ? "GeeTestTaskProxyless" : "GeeTestTask",
                ["websiteURL"] = websiteUrl,
                ["version"] = 4,
                ["initParameters"] = new Dictionary<string, string>
                {
                    ["captcha_id"] = captchaId,
                    // Как initGeetest4 на firewall-странице Avito: product bind + rus.
                    ["product"] = "bind",
                    ["language"] = "rus"
                }
            }
        };
        var task = (Dictionary<string, object?>)body["task"]!;
        if (!string.IsNullOrWhiteSpace(taskOptions?.UserAgent))
        {
            task["userAgent"] = taskOptions.UserAgent.Trim();
        }

        if (proxy is not null)
        {
            task["proxyType"] = proxy.Type;
            task["proxyAddress"] = proxy.Address;
            task["proxyPort"] = proxy.Port;
            task["proxyLogin"] = proxy.Login;
            task["proxyPassword"] = proxy.Password;
        }

        using var response = await http.PostAsJsonAsync("createTask", body, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new RuCaptchaException($"RuCaptcha createTask HTTP {(int)response.StatusCode}: {Trim(json)}");
        }

        return RuCaptchaResponseParser.ParseCreateTaskId(json);
    }

    private async Task<long> CreateHCaptchaTaskAsync(
        string apiKey,
        string websiteUrl,
        string websiteKey,
        GeeTestV4TaskOptions? taskOptions,
        CancellationToken cancellationToken)
    {
        var proxy = taskOptions?.Proxy;
        var body = new Dictionary<string, object?>
        {
            ["clientKey"] = apiKey,
            ["task"] = new Dictionary<string, object?>
            {
                ["type"] = proxy is null ? "HCaptchaTaskProxyless" : "HCaptchaTask",
                ["websiteURL"] = websiteUrl,
                ["websiteKey"] = websiteKey
            }
        };
        var task = (Dictionary<string, object?>)body["task"]!;
        if (!string.IsNullOrWhiteSpace(taskOptions?.UserAgent))
        {
            task["userAgent"] = taskOptions.UserAgent.Trim();
        }

        if (proxy is not null)
        {
            task["proxyType"] = proxy.Type;
            task["proxyAddress"] = proxy.Address;
            task["proxyPort"] = proxy.Port;
            task["proxyLogin"] = proxy.Login;
            task["proxyPassword"] = proxy.Password;
        }

        using var response = await http.PostAsJsonAsync("createTask", body, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new RuCaptchaException($"RuCaptcha createTask hCaptcha HTTP {(int)response.StatusCode}: {Trim(json)}");
        }

        return RuCaptchaResponseParser.ParseCreateTaskId(json);
    }

    private async Task<long> CreateImageToTextTaskAsync(
        string apiKey,
        string imageBody,
        CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, object?>
        {
            ["clientKey"] = apiKey,
            ["task"] = new Dictionary<string, object?>
            {
                ["type"] = "ImageToTextTask",
                ["body"] = imageBody,
                ["case"] = true,
                ["numeric"] = 0,
                ["math"] = false,
                ["minLength"] = 1,
                ["maxLength"] = 16
            }
        };

        using var response = await http.PostAsJsonAsync("createTask", body, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new RuCaptchaException($"RuCaptcha createTask картинки HTTP {(int)response.StatusCode}: {Trim(json)}");
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

    private async Task<HCaptchaSolution> WaitForHCaptchaResultAsync(
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
            var solution = RuCaptchaResponseParser.ParseHCaptchaTaskResult(json, out var pending);
            if (solution is not null)
            {
                return solution;
            }

            if (!pending || DateTime.UtcNow >= deadline)
            {
                throw new RuCaptchaException($"RuCaptcha: решение hCaptcha не получено за {SolveTimeout.TotalSeconds:0} с.");
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<ImageCaptchaSolution> WaitForImageToTextResultAsync(
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
            var solution = RuCaptchaResponseParser.ParseImageToTextTaskResult(json, out var pending);
            if (solution is not null)
            {
                return solution;
            }

            if (!pending || DateTime.UtcNow >= deadline)
            {
                throw new RuCaptchaException($"RuCaptcha: решение картинки не получено за {SolveTimeout.TotalSeconds:0} с.");
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

    private static string? NormalizeImageBody(string? imageBody)
    {
        if (string.IsNullOrWhiteSpace(imageBody))
        {
            return null;
        }

        var value = imageBody.Trim();
        if (value.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
        {
            var separator = value.IndexOf(',');
            return separator >= 0 && separator < value.Length - 1 ? value[(separator + 1)..] : null;
        }

        return value.Contains("://", StringComparison.Ordinal) ? null : value;
    }
}
