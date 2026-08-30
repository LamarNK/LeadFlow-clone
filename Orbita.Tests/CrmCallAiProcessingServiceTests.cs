using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Net;
using System.Text;
using Orbita.Api.Data;
using Orbita.Api.Options;
using Orbita.Api.Services;
using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class CrmCallAiProcessingServiceTests
{
    [Fact]
    public async Task CerioClient_UsesDocumentedTranscribeAndAskContracts()
    {
        var handler = new CerioContractHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.cerio.test/") };
        var client = new CerioAiClient(
            new SingleClientFactory(http),
            Options.Create(new CerioAiOptions { Enabled = true, Token = "test-token" }));

        await using var audio = new MemoryStream([1, 2, 3]);
        var transcript = await client.TranscribeAsync(audio, "call.mp3", "audio/mpeg", CancellationToken.None);
        var analysis = await client.AnalyzeAsync(transcript.Text, CancellationToken.None);

        Assert.Equal("Тестовый разговор", transcript.Text);
        Assert.Single(transcript.Segments);
        Assert.NotNull(analysis.Analysis);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("/api/ai/transcribe?token=test-token&timestamps=true", handler.Requests[0].PathAndQuery);
        Assert.Contains("name=file", handler.Requests[0].Body.Replace('"', ' '), StringComparison.Ordinal);
        Assert.Equal("/api/ai/ask?token=test-token", handler.Requests[1].PathAndQuery);
        using var askBody = System.Text.Json.JsonDocument.Parse(handler.Requests[1].Body);
        Assert.Equal("Тестовый разговор", askBody.RootElement.GetProperty("text").GetString());
        Assert.Contains(
            "attributionConfidence",
            askBody.RootElement.GetProperty("prompt").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void StructuredAnalysis_ParsesFenceAndNormalizesValues()
    {
        const string json = """
            ```json
            {
              "version": 1,
              "score": 12,
              "scoreReason": "Проверка",
              "goal": "Связаться",
              "outcome": "Договорились",
              "nextStep": "Перезвонить",
              "attributionConfidence": "unexpected",
              "strengths": [{"code":"contact","title":"Контакт"}],
              "weaknesses": [],
              "risks": [],
              "recommendations": [],
              "suggestedPhrases": [],
              "checklist": []
            }
            ```
            """;

        var result = CerioAiClient.TryParseAnalysis(json);

        Assert.NotNull(result);
        Assert.Equal(10, result.Score);
        Assert.Equal("low", result.AttributionConfidence);
        Assert.Single(result.Strengths);
    }

    [Fact]
    public async Task StoredRecordings_FromEveryProvider_AreProcessedExactlyOnce()
    {
        await using var db = CreateDb();
        var dataPath = Path.Combine(Path.GetTempPath(), $"orbita-ai-tests-{Guid.NewGuid():N}");
        try
        {
            var recordingOptions = Options.Create(new CrmCallRecordingOptions
            {
                DataPath = dataPath,
                MaxUploadBytes = 1024 * 1024
            });
            var storage = new CrmCallRecordingStorageService(recordingOptions);
            var now = DateTime.UtcNow;
            foreach (var provider in new[]
                     {
                         CrmTelephonyProviders.Asterisk,
                         CrmTelephonyProviders.Sipout,
                         CrmTelephonyProviders.Plusofon
                     })
            {
                var id = Guid.NewGuid();
                await using var audio = new MemoryStream([1, 2, 3, 4]);
                var path = await storage.SaveAsync(id, audio, CancellationToken.None);
                db.CrmCalls.Add(new CrmCallEntity
                {
                    Id = id,
                    OfficeId = Guid.NewGuid(),
                    CardId = Guid.NewGuid(),
                    Provider = provider,
                    ExternalCallId = Guid.NewGuid().ToString("N"),
                    Direction = CrmCallDirections.Outgoing,
                    CallerPhone = "79000000001",
                    CalledPhone = "79000000002",
                    ClientPhoneNormalized = "79000000002",
                    StartedAtUtc = now,
                    DurationSeconds = 60,
                    RecordingStoragePath = path,
                    RecordingContentType = "audio/mpeg",
                    RecordingFileName = "call.mp3",
                    ReceivedAtUtc = now,
                    UpdatedAtUtc = now
                });
            }
            await db.SaveChangesAsync();

            var fake = new FakeCerioClient();
            var options = Options.Create(new CerioAiOptions
            {
                Enabled = true,
                Token = "test-token",
                BatchSize = 10
            });
            var sut = new CrmCallAiProcessingService(
                db,
                storage,
                fake,
                options,
                TimeProvider.System,
                NullLogger<CrmCallAiProcessingService>.Instance);

            Assert.Equal(3, await sut.ProcessDueAsync());
            Assert.Equal(3, fake.Transcriptions);
            Assert.Equal(3, fake.Analyses);
            Assert.All(await db.CrmCallAiInsights.ToListAsync(), insight =>
                Assert.Equal(CrmCallAiStatuses.Completed, insight.Status));

            Assert.Equal(0, await sut.ProcessDueAsync());
            Assert.Equal(3, fake.Transcriptions);
            Assert.Equal(3, fake.Analyses);
        }
        finally
        {
            if (Directory.Exists(dataPath)) Directory.Delete(dataPath, recursive: true);
        }
    }

    private static OrbitaDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<OrbitaDbContext>()
            .UseInMemoryDatabase($"crm-call-ai-{Guid.NewGuid():N}")
            .Options;
        return new OrbitaDbContext(options);
    }

    private sealed class FakeCerioClient : ICerioAiClient
    {
        public int Transcriptions { get; private set; }
        public int Analyses { get; private set; }

        public Task<CerioTranscriptionResult> TranscribeAsync(
            Stream audio,
            string fileName,
            string contentType,
            CancellationToken ct)
        {
            Transcriptions++;
            return Task.FromResult(new CerioTranscriptionResult(
                "Менеджер договорился с кандидатом о следующем звонке.",
                [new CrmCallTranscriptSegmentDto(0, 3, "Добрый день")])) ;
        }

        public Task<CerioAnalysisResult> AnalyzeAsync(string transcript, CancellationToken ct)
        {
            Analyses++;
            return Task.FromResult(new CerioAnalysisResult(
                new CrmCallAiAnalysisDto(
                    1,
                    8,
                    "Следующий шаг зафиксирован.",
                    "Связаться",
                    "Контакт состоялся",
                    "Перезвонить",
                    "medium",
                    [new CrmCallAiAnalysisPointDto("next_step", "Зафиксирован следующий шаг")],
                    [],
                    [],
                    ["Уточнить мотивацию"],
                    ["Что для вас важно?"],
                    ["Проверить карточку кандидата"]),
                "{}"));
        }
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class CerioContractHandler : HttpMessageHandler
    {
        public List<(string PathAndQuery, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.RequestUri!.PathAndQuery, body));
            if (request.RequestUri.AbsolutePath.EndsWith("/transcribe", StringComparison.Ordinal))
            {
                return Json("""
                    {"text":"Тестовый разговор","segments":[{"start":0,"end":1.5,"text":"Тестовый разговор"}]}
                    """);
            }

            var answer = """
                {"version":1,"score":8,"scoreReason":"Хорошо","goal":"Связаться","outcome":"Договорились","nextStep":"Перезвонить","attributionConfidence":"medium","strengths":[],"weaknesses":[],"risks":[],"recommendations":[],"suggestedPhrases":[],"checklist":[]}
                """;
            return Json(System.Text.Json.JsonSerializer.Serialize(new { answer }));
        }

        private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(value, Encoding.UTF8, "application/json")
        };
    }
}
