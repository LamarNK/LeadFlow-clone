using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using TwoCaptchaClient = TwoCaptcha.TwoCaptcha;
using TwoCaptchaApiClient = TwoCaptcha.ApiClient;
using TwoCaptchaCoordinates = TwoCaptcha.Captcha.Coordinates;
using TwoCaptchaGeeTestV4 = TwoCaptcha.Captcha.GeeTestV4;

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

        var captcha = new TwoCaptchaGeeTestV4();
        captcha.SetCaptchaId(captchaId.Trim());
        captcha.SetUrl(websiteUrl.Trim());
        if (!string.IsNullOrWhiteSpace(taskOptions?.Challenge))
        {
            captcha.SetChallenge(taskOptions.Challenge.Trim());
        }

        if (!string.IsNullOrWhiteSpace(taskOptions?.RiskType))
        {
            captcha.SetRiskType(taskOptions.RiskType.Trim());
        }

        if (!string.IsNullOrWhiteSpace(taskOptions?.UserAgent))
        {
            captcha.SetUserAgent(taskOptions.UserAgent.Trim());
        }

        if (taskOptions?.Proxy is { } proxy)
        {
            captcha.SetProxy(
                proxy.Type.ToUpperInvariant(),
                BuildV1ProxyUri(proxy));
        }

        var solver = new TwoCaptchaClient(apiKey.Trim())
        {
            DefaultTimeout = Math.Max(1, (int)Math.Ceiling(SolveTimeout.TotalSeconds)),
            PollingInterval = Math.Max(1, (int)Math.Ceiling(PollInterval.TotalSeconds))
        };
        solver.SetApiClient(new RuCaptchaV1ApiClient(http));

        try
        {
            await solver.Solve(captcha).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new RuCaptchaException($"RuCaptcha GeeTest v4 API v1: {ex.Message}", ex);
        }

        return RuCaptchaResponseParser.ParseGeeTestV4V1Solution(captcha.Code) with
        {
            ProviderTask = new RuCaptchaTask(captcha.Id, RuCaptchaApiVersion.V1)
        };
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
        var solution = await WaitForHCaptchaResultAsync(apiKey.Trim(), taskId, cancellationToken).ConfigureAwait(false);
        return solution with { ProviderTask = new RuCaptchaTask(taskId.ToString(), RuCaptchaApiVersion.V2) };
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
        var solution = await WaitForImageToTextResultAsync(apiKey.Trim(), taskId, cancellationToken).ConfigureAwait(false);
        return solution with { ProviderTask = new RuCaptchaTask(taskId.ToString(), RuCaptchaApiVersion.V2) };
    }

    public async Task<ClickCaptchaSolution> SolveClickCaptchaAsync(
        string apiKey,
        string imageBody,
        string hintImageBody,
        string? hintText,
        int requiredClicks,
        string language,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new RuCaptchaException("Не задан API-ключ RuCaptcha.");
        }

        var body = NormalizeImageBody(imageBody);
        var hint = NormalizeImageBody(hintImageBody);
        if (string.IsNullOrWhiteSpace(body) || string.IsNullOrWhiteSpace(hint))
        {
            throw new RuCaptchaException("Не получено изображение или подсказка ClickCaptcha Avito.");
        }

        var captcha = new TwoCaptchaCoordinates();
        captcha.SetBase64(body);
        captcha.SetHintImg(hint);
        captcha.SetHintText(string.IsNullOrWhiteSpace(hintText)
            ? "Нажмите на элементы на изображении в указанном порядке."
            : hintText.Trim());
        captcha.SetLang(string.IsNullOrWhiteSpace(language) ? "ru" : language.Trim());
        if (requiredClicks is < 1 or > 9)
        {
            throw new RuCaptchaException("Некорректное количество обязательных кликов ClickCaptcha Avito.");
        }

        var solver = new TwoCaptchaClient(apiKey.Trim())
        {
            DefaultTimeout = Math.Max(1, (int)Math.Ceiling(SolveTimeout.TotalSeconds)),
            PollingInterval = Math.Max(1, (int)Math.Ceiling(PollInterval.TotalSeconds))
        };
        solver.SetApiClient(new RuCaptchaV1ApiClient(http, requiredClicks));

        try
        {
            await solver.Solve(captcha).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new RuCaptchaException($"RuCaptcha ClickCaptcha API v1: {ex.Message}", ex);
        }

        return new ClickCaptchaSolution(
            RuCaptchaResponseParser.ParseClickCaptchaCoordinates(captcha.Code),
            new RuCaptchaTask(captcha.Id, RuCaptchaApiVersion.V1));
    }

    public async Task ReportAsync(
        string apiKey,
        RuCaptchaTask task,
        bool isCorrect,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new RuCaptchaException("Не задан API-ключ RuCaptcha.");
        }

        if (task is null || string.IsNullOrWhiteSpace(task.Id))
        {
            throw new RuCaptchaException("Не задан идентификатор задачи RuCaptcha для report.");
        }

        if (task.ApiVersion == RuCaptchaApiVersion.V1)
        {
            var solver = new TwoCaptchaClient(apiKey.Trim());
            solver.SetApiClient(new RuCaptchaV1ApiClient(http));
            try
            {
                await solver.Report(task.Id.Trim(), isCorrect).WaitAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new RuCaptchaException($"RuCaptcha report API v1: {ex.Message}", ex);
            }
        }

        if (!long.TryParse(task.Id, out var taskId) || taskId <= 0)
        {
            throw new RuCaptchaException("Некорректный идентификатор задачи RuCaptcha API v2 для report.");
        }

        var endpoint = isCorrect ? "reportCorrect" : "reportIncorrect";
        using var response = await http.PostAsJsonAsync(endpoint, new Dictionary<string, object?>
        {
            ["clientKey"] = apiKey.Trim(),
            ["taskId"] = taskId
        }, JsonOptions, cancellationToken).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new RuCaptchaException($"RuCaptcha {endpoint} HTTP {(int)response.StatusCode}: {Trim(json)}");
        }

        RuCaptchaResponseParser.EnsureReportAccepted(json);
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

    private static string BuildV1ProxyUri(GeeTestV4Proxy proxy)
    {
        var credentials = string.IsNullOrWhiteSpace(proxy.Login)
            ? string.Empty
            : string.IsNullOrWhiteSpace(proxy.Password)
                ? proxy.Login + "@"
                : proxy.Login + ":" + proxy.Password + "@";
        return credentials + proxy.Address + ":" + proxy.Port;
    }

    private sealed class RuCaptchaV1ApiClient(HttpClient client, int? requiredClicks = null) : TwoCaptchaApiClient
    {
        public override async Task<string> In(
            Dictionary<string, string> parameters,
            Dictionary<string, FileInfo> files)
        {
            if (requiredClicks is > 0 && parameters.ContainsKey("coordinatescaptcha"))
            {
                parameters["min_clicks"] = requiredClicks.Value.ToString();
                parameters["max_clicks"] = requiredClicks.Value.ToString();
            }

            using var content = new FormUrlEncodedContent(parameters);
            using var response = await client.PostAsync("https://rucaptcha.com/in.php", content).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"RuCaptcha in.php HTTP {(int)response.StatusCode}: {Trim(body)}");
            }

            return body;
        }

        public override async Task<string> Res(Dictionary<string, string> parameters)
        {
            var query = string.Join("&", parameters.Select(pair =>
                Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
            using var response = await client.GetAsync("https://rucaptcha.com/res.php?" + query).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"RuCaptcha res.php HTTP {(int)response.StatusCode}: {Trim(body)}");
            }

            return body;
        }
    }
}
