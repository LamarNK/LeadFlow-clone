using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Orbita.Api.Options;
using Orbita.Contracts;

namespace Orbita.Api.Services;

public sealed record CerioTranscriptionResult(
    string Text,
    IReadOnlyList<CrmCallTranscriptSegmentDto> Segments);

public sealed record CerioAnalysisResult(
    CrmCallAiAnalysisDto? Analysis,
    string RawAnswer);

public sealed class CerioAiException(
    string code,
    string message,
    bool retryable) : Exception(message)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
}

public interface ICerioAiClient
{
    Task<CerioTranscriptionResult> TranscribeAsync(
        Stream audio,
        string fileName,
        string contentType,
        CancellationToken ct);

    Task<CerioAnalysisResult> AnalyzeAsync(string transcript, CancellationToken ct);
}

public sealed class CerioAiClient(
    IHttpClientFactory httpClientFactory,
    IOptions<CerioAiOptions> options) : ICerioAiClient
{
    public const string HttpClientName = "CerioAi";
    public const string DefaultPromptVersion = "recruiting-v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
    private readonly CerioAiOptions _options = options.Value;

    public async Task<CerioTranscriptionResult> TranscribeAsync(
        Stream audio,
        string fileName,
        string contentType,
        CancellationToken ct)
    {
        EnsureConfigured();
        using var file = new StreamContent(audio);
        if (MediaTypeHeaderValue.TryParse(contentType, out var mediaType))
        {
            file.Headers.ContentType = mediaType;
        }
        using var content = new MultipartFormDataContent();
        content.Add(file, "file", NormalizeFileName(fileName));
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"api/ai/transcribe?token={Uri.EscapeDataString(_options.Token)}&timestamps=true")
        {
            Content = content
        };
        using var response = await SendAsync(request, ct);
        var payload = await response.Content.ReadFromJsonAsync<CerioTranscriptionResponse>(JsonOptions, ct)
                      ?? throw new CerioAiException("invalid_transcription", "Сервис вернул пустой ответ расшифровки.", true);
        var text = payload.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new CerioAiException("empty_transcription", "Сервис не распознал речь в записи.", false);
        }

        var segments = payload.Segments?
            .Where(x => !string.IsNullOrWhiteSpace(x.Text))
            .Select(x => new CrmCallTranscriptSegmentDto(
                Math.Max(0, x.Start),
                Math.Max(x.Start, x.End),
                x.Text!.Trim(),
                string.IsNullOrWhiteSpace(x.Speaker) ? null : x.Speaker.Trim()))
            .ToList() ?? [];
        return new CerioTranscriptionResult(text, segments);
    }

    public async Task<CerioAnalysisResult> AnalyzeAsync(string transcript, CancellationToken ct)
    {
        EnsureConfigured();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"api/ai/ask?token={Uri.EscapeDataString(_options.Token)}")
        {
            Content = JsonContent.Create(new
            {
                prompt = string.IsNullOrWhiteSpace(_options.AnalysisPrompt)
                    ? BuildDefaultPrompt()
                    : _options.AnalysisPrompt.Trim(),
                text = transcript
            })
        };
        using var response = await SendAsync(request, ct);
        var payload = await response.Content.ReadFromJsonAsync<CerioAskResponse>(JsonOptions, ct)
                      ?? throw new CerioAiException("invalid_analysis", "Сервис вернул пустой ответ анализа.", true);
        var raw = payload.Answer?.Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new CerioAiException("empty_analysis", "Сервис не вернул анализ разговора.", true);
        }

        var analysis = TryParseAnalysis(raw);
        return new CerioAnalysisResult(analysis, raw);
    }

    internal static CrmCallAiAnalysisDto? TryParseAnalysis(string raw)
    {
        var json = StripCodeFence(raw);
        try
        {
            var parsed = JsonSerializer.Deserialize<CrmCallAiAnalysisDto>(json, JsonOptions);
            if (parsed is null) return null;
            return parsed with
            {
                Score = Math.Clamp(parsed.Score, 0, 10),
                AttributionConfidence = NormalizeConfidence(parsed.AttributionConfidence),
                Strengths = parsed.Strengths ?? [],
                Weaknesses = parsed.Weaknesses ?? [],
                Risks = parsed.Risks ?? [],
                Recommendations = parsed.Recommendations ?? [],
                SuggestedPhrases = parsed.SuggestedPhrases ?? [],
                Checklist = parsed.Checklist ?? []
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await httpClientFactory.CreateClient(HttpClientName)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new CerioAiException("timeout", "Сервис расшифровки не ответил вовремя.", true);
        }
        catch (HttpRequestException)
        {
            throw new CerioAiException("network", "Не удалось связаться с сервисом расшифровки.", true);
        }

        if (response.IsSuccessStatusCode) return response;

        var status = response.StatusCode;
        response.Dispose();
        var retryable = status is HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout
            || (int)status >= 500;
        var code = status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            ? "authentication"
            : $"http_{(int)status}";
        var message = status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            ? "Сервис отклонил токен доступа. Обратитесь к администратору."
            : "Сервис расшифровки временно недоступен.";
        throw new CerioAiException(code, message, retryable);
    }

    private void EnsureConfigured()
    {
        if (!_options.Enabled || string.IsNullOrWhiteSpace(_options.Token))
        {
            throw new CerioAiException("not_configured", "AI-анализ звонков не настроен.", false);
        }
    }

    private static string NormalizeFileName(string fileName)
    {
        var normalized = Path.GetFileName(fileName);
        return string.IsNullOrWhiteSpace(normalized) ? "call-recording.bin" : normalized;
    }

    private static string StripCodeFence(string value)
    {
        var trimmed = value.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal)) return trimmed;
        var firstLine = trimmed.IndexOf('\n');
        var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        return firstLine >= 0 && lastFence > firstLine
            ? trimmed[(firstLine + 1)..lastFence].Trim()
            : trimmed;
    }

    private static string NormalizeConfidence(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "high" => "high",
        "medium" => "medium",
        _ => "low"
    };

    private static string BuildDefaultPrompt() => """
        Ты — эксперт по контролю качества телефонных разговоров рекрутингового менеджера с кандидатом.
        Проанализируй только предоставленный транскрипт. Не придумывай факты, реплики, договорённости или роли.
        Если роли собеседников нельзя уверенно определить по тексту, укажи attributionConfidence=\"low\" и не приписывай спорные реплики менеджеру.
        Оцени работу менеджера: установление контакта, выявление ситуации и мотивации кандидата, ясность предложения,
        работа с вопросами и возражениями, фиксация следующего шага, корректность и деловой тон.

        Верни ТОЛЬКО валидный JSON без Markdown и пояснений. Строго такая структура:
        {
          "version": 1,
          "score": 0.0,
          "scoreReason": "краткое обоснование",
          "goal": "цель звонка или Не определено",
          "outcome": "фактический итог или Не определено",
          "nextStep": "согласованный следующий шаг или Не согласован",
          "attributionConfidence": "low|medium|high",
          "strengths": [{"code":"short_snake_case","title":"сильная сторона","evidence":"короткая цитата или наблюдение"}],
          "weaknesses": [{"code":"short_snake_case","title":"зона роста","evidence":"короткая цитата или наблюдение","impact":"возможное последствие"}],
          "risks": [{"code":"short_snake_case","title":"риск","evidence":"основание","impact":"последствие"}],
          "recommendations": ["конкретное действие"],
          "suggestedPhrases": ["конкретная фраза для похожего звонка"],
          "checklist": ["пункт подготовки"]
        }
        Оценка score — от 0 до 10. Не более 5 пунктов в каждом массиве. Все тексты — на русском языке.
        """;

    private sealed record CerioTranscriptionResponse(string? Text, IReadOnlyList<CerioSegment>? Segments);
    private sealed record CerioSegment(double Start, double End, string? Text, string? Speaker);
    private sealed record CerioAskResponse(string? Answer);
}
