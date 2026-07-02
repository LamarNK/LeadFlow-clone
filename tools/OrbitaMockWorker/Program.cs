using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Orbita.Contracts;

var apiBase = args.ElementAtOrDefault(0) ?? "https://localhost:7291";
var registrationSecret = args.ElementAtOrDefault(1) ?? "change-me-in-production";
var workerCount = int.TryParse(args.ElementAtOrDefault(2), out var count) ? count : 3;

using var http = new HttpClient { BaseAddress = new Uri(apiBase.TrimEnd('/') + "/") };
http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

var workers = new List<MockWorker>();
for (var i = 1; i <= workerCount; i++)
{
    var register = await http.PostAsJsonAsync("api/v1/workers/register", new WorkerRegisterRequest(
        registrationSecret,
        $"VDS #{i}",
        $"mock-vds-{i}",
        "1.0.0-mock"));

    if (!register.IsSuccessStatusCode)
    {
        Console.WriteLine($"Register failed for worker {i}: {(int)register.StatusCode}");
        continue;
    }

    var payload = await register.Content.ReadFromJsonAsync<WorkerRegisterResponse>();
    if (payload is null)
    {
        continue;
    }

    workers.Add(new MockWorker(payload.WorkerId, payload.ApiKey, i));
    Console.WriteLine($"Registered {payload.WorkerId} (VDS #{i})");
}

if (workers.Count == 0)
{
    Console.WriteLine("No workers registered. Is API running?");
    return;
}

var random = new Random(42);
while (true)
{
    foreach (var worker in workers)
    {
        using var request = new HttpRequestMessage();
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", worker.ApiKey);

        var heartbeat = new WorkerHeartbeatRequest(
            worker.Id,
            worker.DisplayName,
            "1.0.0-mock",
            worker.MachineName,
            random.NextDouble() > 0.2 ? "Running" : "Waiting",
            "Mock monitoring cycle",
            true,
            DateTime.UtcNow.AddMinutes(5));

        request.Method = HttpMethod.Post;
        request.RequestUri = new Uri("api/v1/workers/heartbeat", UriKind.Relative);
        request.Content = JsonContent.Create(heartbeat);
        await http.SendAsync(request);

        var stats = BuildStats(worker.Index, random);
        var accounts = BuildAccounts(worker.Index, random);
        var balances = BuildBalances(accounts, random);

        using var snapshotRequest = new HttpRequestMessage(HttpMethod.Post, "api/v1/workers/telemetry/snapshot");
        snapshotRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", worker.ApiKey);
        snapshotRequest.Content = JsonContent.Create(new WorkerSnapshotRequest(
            worker.Id,
            DateTime.UtcNow,
            stats,
            accounts,
            balances));
        await http.SendAsync(snapshotRequest);

        var activityAccount = accounts[random.Next(accounts.Count)];
        var activitySubProfile = activityAccount.SubProfiles?.FirstOrDefault();
        using var activityRequest = new HttpRequestMessage(HttpMethod.Post, "api/v1/workers/activity");
        activityRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", worker.ApiKey);
        activityRequest.Content = JsonContent.Create(new WorkerActivityRequest(
            worker.Id,
            random.NextDouble() > 0.3 ? WorkerActivityPhases.SubProfile : WorkerActivityPhases.Waiting,
            random.NextDouble() > 0.3 ? "сбор откликов" : "ожидание следующего цикла",
            activityAccount.AccountId,
            activityAccount.DisplayName,
            activitySubProfile?.Id,
            activitySubProfile?.Name,
            DateTime.UtcNow.AddMinutes(5),
            DateTime.UtcNow));
        await http.SendAsync(activityRequest);

        using var eventsRequest = new HttpRequestMessage(HttpMethod.Post, "api/v1/workers/telemetry/events");
        eventsRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", worker.ApiKey);
        eventsRequest.Content = JsonContent.Create(new WorkerEventBatchRequest(
            worker.Id,
            [
                new WorkerEventDto(null, "Info", $"Цикл завершён на {worker.DisplayName}", null, DateTime.UtcNow),
                new WorkerEventDto(accounts[0].AccountId, "Warning", "Проверка авторизации", "Mock event", DateTime.UtcNow)
            ]));
        await http.SendAsync(eventsRequest);
    }

    Console.WriteLine($"Sent telemetry for {workers.Count} workers at {DateTime.Now:T}");
    await Task.Delay(TimeSpan.FromSeconds(30));
}

static DashboardStatsDto BuildStats(int index, Random random)
{
    var hourly = Enumerable.Range(0, 24)
        .Select(h => new ActivityPointDto($"{h:00}:00", random.Next(0, 6), random.Next(0, 4), random.Next(0, 2), random.Next(0, 2), h, 1, null))
        .ToList();

    var weekly = Enumerable.Range(0, 7)
        .Select(d => new ActivityPointDto(DateTime.Today.AddDays(-6 + d).ToString("ddd d.MM"), random.Next(5, 40), random.Next(2, 20), random.Next(0, 8), random.Next(0, 4), 0, 1, DateTime.Today.AddDays(-6 + d)))
        .ToList();

    return new DashboardStatsDto(
        random.Next(1, 8),
        random.Next(10, 60) + index * 3,
        random.Next(5, 40),
        random.Next(0, 6),
        random.Next(0, 10),
        random.Next(0, 4),
        random.Next(0, 2),
        10,
        random.Next(0, 2),
        random.Next(0, 3),
        random.Next(20, 80),
        random.Next(0, 5),
        random.Next(0, 3),
        hourly,
        weekly);
}

static List<WorkerAccountDto> BuildAccounts(int workerIndex, Random random)
{
    return Enumerable.Range(1, 10)
        .Select(i =>
        {
            var id = Guid.NewGuid();
            return new WorkerAccountDto(
                id,
                $"Аккаунт {workerIndex}-{i}",
                random.NextDouble() > 0.85 ? "RequiresLogin" : "Monitoring",
                true,
                random.Next(1, 12),
                random.Next(0, 2),
                random.Next(0, 2),
                random.NextDouble() > 0.9 ? "Требуется авторизация" : null,
                DateTime.UtcNow.AddMinutes(-random.Next(1, 120)),
                true,
                $"profile-{workerIndex}-{i}",
                [
                    new WorkerSubProfileDto($"sub-{i}-1", "Основной", "Работа", true, random.Next(500, 5000), null, null, null),
                    new WorkerSubProfileDto($"sub-{i}-2", "Доп.", "Работа", false, random.Next(0, 2000), null, null, null)
                ],
                DateTime.UtcNow.AddHours(-2));
        })
        .ToList();
}

static List<WorkerBalanceDto> BuildBalances(IReadOnlyList<WorkerAccountDto> accounts, Random random) =>
    accounts.Select(a => new WorkerBalanceDto(
        a.AccountId,
        a.DisplayName,
        random.Next(500, 15000),
        [
            new SubProfileBalanceDto("Основной", random.Next(500, 15000)),
            new SubProfileBalanceDto("Доп.", random.Next(0, 5000))
        ])).ToList();

sealed record MockWorker(Guid Id, string ApiKey, int Index)
{
    public string DisplayName => $"VDS #{Index}";
    public string MachineName => $"mock-vds-{Index}";
}