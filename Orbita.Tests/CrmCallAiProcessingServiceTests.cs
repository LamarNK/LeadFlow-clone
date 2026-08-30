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

    [Fact]
    public async Task ZeroDurationRecordings_AreNeverSentAndExistingJobsBecomeSkipped()
    {
        await using var db = CreateDb();
        var dataPath = Path.Combine(Path.GetTempPath(), $"orbita-ai-zero-tests-{Guid.NewGuid():N}");
        try
        {
            var recordingOptions = Options.Create(new CrmCallRecordingOptions
            {
                DataPath = dataPath,
                MaxUploadBytes = 1024 * 1024
            });
            var storage = new CrmCallRecordingStorageService(recordingOptions);
            var now = DateTime.UtcNow;
            var existingZeroCall = NewCall(Guid.NewGuid(), now, durationSeconds: 0, "existing-zero.bin");
            var newZeroCall = NewCall(Guid.NewGuid(), now.AddSeconds(-1), durationSeconds: 0, "new-zero.bin");
            var validCallId = Guid.NewGuid();
            await using var audio = new MemoryStream([1, 2, 3, 4]);
            var validPath = await storage.SaveAsync(validCallId, audio, CancellationToken.None);
            var validCall = NewCall(validCallId, now.AddSeconds(-2), durationSeconds: 30, validPath);
            db.CrmCalls.AddRange(existingZeroCall, newZeroCall, validCall);
            db.CrmCallAiInsights.Add(new CrmCallAiInsightEntity
            {
                CallId = existingZeroCall.Id,
                Call = existingZeroCall,
                Status = CrmCallAiStatuses.Transcribing,
                PromptVersion = "test",
                Attempts = 1,
                NextAttemptAtUtc = now.AddMinutes(-1),
                CreatedAtUtc = now.AddMinutes(-2),
                UpdatedAtUtc = now.AddMinutes(-1)
            });
            await db.SaveChangesAsync();

            var fake = new FakeCerioClient();
            var sut = new CrmCallAiProcessingService(
                db,
                storage,
                fake,
                Options.Create(new CerioAiOptions
                {
                    Enabled = true,
                    Token = "test-token",
                    BatchSize = 10
                }),
                TimeProvider.System,
                NullLogger<CrmCallAiProcessingService>.Instance);

            Assert.Equal(1, await sut.ProcessDueAsync());
            Assert.Equal(1, fake.Transcriptions);
            Assert.Equal(1, fake.Analyses);

            var skipped = await db.CrmCallAiInsights.SingleAsync(x => x.CallId == existingZeroCall.Id);
            Assert.Equal(CrmCallAiStatuses.Skipped, skipped.Status);
            Assert.Equal("zero_duration", skipped.LastErrorCode);
            Assert.Null(skipped.NextAttemptAtUtc);
            Assert.False(await db.CrmCallAiInsights.AnyAsync(x => x.CallId == newZeroCall.Id));
            Assert.Equal(
                CrmCallAiStatuses.Completed,
                (await db.CrmCallAiInsights.SingleAsync(x => x.CallId == validCall.Id)).Status);
        }
        finally
        {
            if (Directory.Exists(dataPath)) Directory.Delete(dataPath, recursive: true);
        }

        static CrmCallEntity NewCall(Guid id, DateTime startedAtUtc, int durationSeconds, string path) => new()
        {
            Id = id,
            OfficeId = Guid.NewGuid(),
            CardId = Guid.NewGuid(),
            Provider = CrmTelephonyProviders.Asterisk,
            ExternalCallId = Guid.NewGuid().ToString("N"),
            Direction = CrmCallDirections.Outgoing,
            CallerPhone = "79000000001",
            CalledPhone = "79000000002",
            ClientPhoneNormalized = "79000000002",
            StartedAtUtc = startedAtUtc,
            DurationSeconds = durationSeconds,
            RecordingStoragePath = path,
            RecordingContentType = "audio/wav",
            RecordingFileName = "call.wav",
            ReceivedAtUtc = startedAtUtc,
            UpdatedAtUtc = startedAtUtc
        };
    }

    [Fact]
    public async Task ClaimedRecordings_CanBeProcessedThreeAtATimeWithSeparateDbContexts()
    {
        var dbOptions = new DbContextOptionsBuilder<OrbitaDbContext>()
            .UseInMemoryDatabase($"crm-call-ai-parallel-{Guid.NewGuid():N}")
            .Options;
        var dataPath = Path.Combine(Path.GetTempPath(), $"orbita-ai-parallel-tests-{Guid.NewGuid():N}");
        try
        {
            var storage = new CrmCallRecordingStorageService(Options.Create(new CrmCallRecordingOptions
            {
                DataPath = dataPath,
                MaxUploadBytes = 1024 * 1024
            }));
            var now = DateTime.UtcNow;
            await using (var setupDb = new OrbitaDbContext(dbOptions))
            {
                for (var index = 0; index < 3; index++)
                {
                    var id = Guid.NewGuid();
                    await using var audio = new MemoryStream([1, 2, 3, 4]);
                    var path = await storage.SaveAsync(id, audio, CancellationToken.None);
                    setupDb.CrmCalls.Add(new CrmCallEntity
                    {
                        Id = id,
                        OfficeId = Guid.NewGuid(),
                        CardId = Guid.NewGuid(),
                        Provider = CrmTelephonyProviders.Asterisk,
                        ExternalCallId = Guid.NewGuid().ToString("N"),
                        Direction = CrmCallDirections.Outgoing,
                        CallerPhone = "79000000001",
                        CalledPhone = "79000000002",
                        ClientPhoneNormalized = "79000000002",
                        StartedAtUtc = now.AddSeconds(-index),
                        DurationSeconds = 30,
                        RecordingStoragePath = path,
                        RecordingContentType = "audio/wav",
                        RecordingFileName = "call.wav",
                        ReceivedAtUtc = now,
                        UpdatedAtUtc = now
                    });
                }
                await setupDb.SaveChangesAsync();
            }

            var fake = new ConcurrentCerioClient();
            var options = Options.Create(new CerioAiOptions
            {
                Enabled = true,
                Token = "test-token",
                BatchSize = 3,
                MaxParallelism = 3
            });
            IReadOnlyList<Guid> callIds;
            await using (var claimDb = new OrbitaDbContext(dbOptions))
            {
                var coordinator = NewService(claimDb);
                callIds = await coordinator.ClaimDueAsync(3);
            }
            Assert.Equal(3, callIds.Count);

            await Task.WhenAll(callIds.Select(async callId =>
            {
                await using var workerDb = new OrbitaDbContext(dbOptions);
                await NewService(workerDb).ProcessClaimedAsync(callId);
            }));

            Assert.Equal(3, fake.MaxConcurrentTranscriptions);
            await using var verifyDb = new OrbitaDbContext(dbOptions);
            Assert.All(await verifyDb.CrmCallAiInsights.ToListAsync(), insight =>
                Assert.Equal(CrmCallAiStatuses.Completed, insight.Status));

            CrmCallAiProcessingService NewService(OrbitaDbContext context) => new(
                context,
                storage,
                fake,
                options,
                TimeProvider.System,
                NullLogger<CrmCallAiProcessingService>.Instance);
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

    private sealed class ConcurrentCerioClient : ICerioAiClient
    {
        private int activeTranscriptions;
        private int maxConcurrentTranscriptions;

        public int MaxConcurrentTranscriptions => Volatile.Read(ref maxConcurrentTranscriptions);

        public async Task<CerioTranscriptionResult> TranscribeAsync(
            Stream audio,
            string fileName,
            string contentType,
            CancellationToken ct)
        {
            var active = Interlocked.Increment(ref activeTranscriptions);
            UpdateMaximum(active);
            try
            {
                await Task.Delay(75, ct);
                return new CerioTranscriptionResult("Тестовый разговор", []);
            }
            finally
            {
                Interlocked.Decrement(ref activeTranscriptions);
            }
        }

        public Task<CerioAnalysisResult> AnalyzeAsync(string transcript, CancellationToken ct) =>
            Task.FromResult(new CerioAnalysisResult(
                new CrmCallAiAnalysisDto(
                    1,
                    8,
                    "Проверка параллельной обработки.",
                    "Связаться",
                    "Контакт состоялся",
                    "Перезвонить",
                    "medium",
                    [],
                    [],
                    [],
                    [],
                    [],
                    []),
                "{}"));

        private void UpdateMaximum(int candidate)
        {
            while (true)
            {
                var current = Volatile.Read(ref maxConcurrentTranscriptions);
                if (candidate <= current
                    || Interlocked.CompareExchange(ref maxConcurrentTranscriptions, candidate, current) == current)
                {
                    return;
                }
            }
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
