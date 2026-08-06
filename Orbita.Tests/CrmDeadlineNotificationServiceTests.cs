using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orbita.Api.Data;
using Orbita.Api.Options;
using Orbita.Api.Services;
using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class CrmDeadlineNotificationServiceTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 8, 5, 6, 0, 0, TimeSpan.Zero);

    public static TheoryData<TimeSpan, string?> ReminderBoundaries => new()
    {
        { TimeSpan.FromHours(24).Add(TimeSpan.FromSeconds(1)), null },
        { TimeSpan.FromHours(24), CrmTaskNotificationKinds.DueIn24Hours },
        { TimeSpan.FromHours(1).Add(TimeSpan.FromSeconds(1)), CrmTaskNotificationKinds.DueIn24Hours },
        { TimeSpan.FromHours(1), CrmTaskNotificationKinds.DueIn1Hour },
        { TimeSpan.FromSeconds(1), CrmTaskNotificationKinds.DueIn1Hour },
        { TimeSpan.Zero, CrmTaskNotificationKinds.Overdue },
        { TimeSpan.FromSeconds(-1), CrmTaskNotificationKinds.Overdue }
    };

    [Fact]
    public async Task ProcessDueTasks_GlobalSwitchDisabled_DoesNothing()
    {
        await using var harness = Harness.Create(globalEnabled: false);
        var officeId = harness.AddOffice();
        harness.AddProfile("manager-a", officeId);
        harness.AddTask(officeId, "manager-a", harness.Now.AddMinutes(30));
        await harness.Db.SaveChangesAsync();

        var created = await harness.Sut.ProcessDueTasksAsync();

        Assert.Equal(0, created);
        Assert.Empty(harness.Db.CrmTaskNotifications);
        Assert.Empty(harness.Realtime.Sent);
        var summary = await harness.Sut.GetSummaryAsync(officeId, "manager-a");
        Assert.False(summary.Enabled);
        Assert.Equal(0, summary.UnreadCount);
    }

    [Fact]
    public async Task ProcessDueTasks_RequiresAllOfficeFlagsAndMatchingProfile()
    {
        await using var harness = Harness.Create();
        var validOffice = harness.AddOffice(name: "valid");
        var disabledOffice = harness.AddOffice(name: "disabled", isEnabled: false);
        var crmDisabledOffice = harness.AddOffice(name: "crm-disabled", crmEnabled: false);
        var remindersDisabledOffice = harness.AddOffice(name: "reminders-disabled", notificationsEnabled: false);
        var activationMissingOffice = harness.AddOffice(name: "activation-missing", activationPresent: false);

        AddDueTaskWithProfile(harness, validOffice, "valid-manager");
        AddDueTaskWithProfile(harness, disabledOffice, "disabled-manager");
        AddDueTaskWithProfile(harness, crmDisabledOffice, "crm-disabled-manager");
        AddDueTaskWithProfile(harness, remindersDisabledOffice, "reminders-disabled-manager");
        AddDueTaskWithProfile(harness, activationMissingOffice, "activation-missing-manager");
        harness.AddTask(validOffice, "manager-without-office-profile", harness.Now.AddMinutes(30));
        await harness.Db.SaveChangesAsync();

        var created = await harness.Sut.ProcessDueTasksAsync();

        Assert.Equal(1, created);
        var notification = Assert.Single(harness.Db.CrmTaskNotifications);
        Assert.Equal(validOffice, notification.OfficeId);
        Assert.Equal("valid-manager", notification.RecipientUserId);
        Assert.Equal("valid-manager", Assert.Single(harness.Realtime.Sent).RecipientUserId);
    }

    [Theory]
    [MemberData(nameof(ReminderBoundaries))]
    public async Task ProcessDueTasks_UsesExpectedReminderAtBoundary(
        TimeSpan dueAfter,
        string? expectedKind)
    {
        await using var harness = Harness.Create();
        var officeId = harness.AddOffice();
        harness.AddProfile("manager", officeId);
        harness.AddTask(officeId, "manager", harness.Now.Add(dueAfter));
        await harness.Db.SaveChangesAsync();

        var created = await harness.Sut.ProcessDueTasksAsync();

        if (expectedKind is null)
        {
            Assert.Equal(0, created);
            Assert.Empty(harness.Db.CrmTaskNotifications);
            Assert.Empty(harness.Realtime.Sent);
            return;
        }

        Assert.Equal(1, created);
        Assert.Equal(expectedKind, Assert.Single(harness.Db.CrmTaskNotifications).Kind);
        Assert.Equal(expectedKind, Assert.Single(harness.Realtime.Sent).Notification.Kind);
    }

    [Fact]
    public async Task ProcessDueTasks_RepeatedCycle_IsIdempotent()
    {
        await using var harness = Harness.Create();
        var officeId = harness.AddOffice();
        harness.AddProfile("manager", officeId);
        harness.AddTask(officeId, "manager", harness.Now.AddMinutes(30));
        await harness.Db.SaveChangesAsync();

        Assert.Equal(1, await harness.Sut.ProcessDueTasksAsync());
        Assert.Equal(0, await harness.Sut.ProcessDueTasksAsync());

        Assert.Single(harness.Db.CrmTaskNotifications);
        Assert.Single(harness.Realtime.Sent);
    }

    [Theory]
    [InlineData(null, CrmTaskStatuses.Open)]
    [InlineData(30, CrmTaskStatuses.Completed)]
    [InlineData(30, CrmTaskStatuses.Cancelled)]
    public async Task ProcessDueTasks_IgnoresTasksThatCannotNeedAReminder(int? dueAfterMinutes, string status)
    {
        await using var harness = Harness.Create();
        var officeId = harness.AddOffice();
        harness.AddProfile("manager", officeId);
        harness.AddTask(
            officeId,
            "manager",
            dueAfterMinutes is int minutes ? harness.Now.AddMinutes(minutes) : null,
            status);
        await harness.Db.SaveChangesAsync();

        Assert.Equal(0, await harness.Sut.ProcessDueTasksAsync());
        Assert.Empty(harness.Db.CrmTaskNotifications);
        Assert.Empty(harness.Realtime.Sent);
    }

    [Fact]
    public async Task ProcessDueTasks_RealtimeFailureDoesNotLoseDurableNotification()
    {
        await using var harness = Harness.Create();
        var officeId = harness.AddOffice();
        harness.AddProfile("manager", officeId);
        harness.AddTask(officeId, "manager", harness.Now.AddMinutes(30));
        harness.Realtime.ThrowOnNotify = true;
        await harness.Db.SaveChangesAsync();

        Assert.Equal(1, await harness.Sut.ProcessDueTasksAsync());
        Assert.Single(harness.Db.CrmTaskNotifications);
        Assert.Empty(harness.Realtime.Sent);
        Assert.Equal(1, (await harness.Sut.GetSummaryAsync(officeId, "manager")).UnreadCount);
    }

    [Fact]
    public async Task ProcessDueTasks_ActivationCutoffPreventsOldOverdueBurstUntilNewRevision()
    {
        await using var harness = Harness.Create();
        var officeId = harness.AddOffice(enabledAtUtc: harness.Now);
        harness.AddProfile("manager", officeId);
        var oldTask = harness.AddTask(
            officeId,
            "manager",
            harness.Now.AddMinutes(-30),
            reminderChangedAtUtc: harness.Now.AddMinutes(-1));
        await harness.Db.SaveChangesAsync();

        Assert.Equal(0, await harness.Sut.ProcessDueTasksAsync());
        Assert.Empty(harness.Db.CrmTaskNotifications);

        oldTask.ReminderVersion = Guid.NewGuid();
        oldTask.ReminderVersionChangedAtUtc = harness.Now;
        await harness.Db.SaveChangesAsync();

        Assert.Equal(1, await harness.Sut.ProcessDueTasksAsync());
        var notification = Assert.Single(harness.Db.CrmTaskNotifications);
        Assert.Equal(oldTask.ReminderVersion, notification.ReminderVersion);
    }

    [Fact]
    public async Task ProcessDueTasks_ActivationCutoffKeepsExistingFutureTaskEligible()
    {
        await using var harness = Harness.Create();
        var officeId = harness.AddOffice(enabledAtUtc: harness.Now);
        harness.AddProfile("manager", officeId);
        harness.AddTask(
            officeId,
            "manager",
            harness.Now.AddMinutes(30),
            reminderChangedAtUtc: harness.Now.AddMinutes(-1));
        await harness.Db.SaveChangesAsync();

        Assert.Equal(1, await harness.Sut.ProcessDueTasksAsync());
        Assert.Equal(
            CrmTaskNotificationKinds.DueIn1Hour,
            Assert.Single(harness.Db.CrmTaskNotifications).Kind);
    }

    [Fact]
    public async Task ProcessDueTasks_RevisionAndStatusHideStaleNotifications()
    {
        await using var harness = Harness.Create();
        var officeId = harness.AddOffice();
        harness.AddProfile("manager", officeId);
        var task = harness.AddTask(officeId, "manager", harness.Now.AddHours(23));
        await harness.Db.SaveChangesAsync();

        Assert.Equal(1, await harness.Sut.ProcessDueTasksAsync());
        var firstVersion = task.ReminderVersion;

        task.DueAtUtc = harness.Now.AddMinutes(30);
        task.ReminderVersion = Guid.NewGuid();
        task.ReminderVersionChangedAtUtc = harness.Now;
        await harness.Db.SaveChangesAsync();

        Assert.Equal(1, await harness.Sut.ProcessDueTasksAsync());
        Assert.Equal(2, await harness.Db.CrmTaskNotifications.CountAsync());
        Assert.Contains(
            await harness.Db.CrmTaskNotifications.Select(x => x.ReminderVersion).ToListAsync(),
            x => x == firstVersion);

        var current = await harness.Sut.GetAsync(officeId, "manager", unreadOnly: false, limit: 50);
        Assert.True(current.Enabled);
        Assert.Equal(1, current.UnreadCount);
        var currentItem = Assert.Single(current.Items);
        Assert.Equal(CrmTaskNotificationKinds.DueIn1Hour, currentItem.Kind);

        task.Status = CrmTaskStatuses.Completed;
        await harness.Db.SaveChangesAsync();

        Assert.Equal(0, await harness.Sut.ProcessDueTasksAsync());
        var summary = await harness.Sut.GetSummaryAsync(officeId, "manager");
        Assert.True(summary.Enabled);
        Assert.Equal(0, summary.UnreadCount);
        Assert.Empty((await harness.Sut.GetAsync(officeId, "manager", unreadOnly: false, limit: 50)).Items);
    }

    [Fact]
    public async Task ProcessDueTasks_RespectsBatchLimitAndPrioritizesOverdue()
    {
        await using var harness = Harness.Create(batchSize: 2);
        var officeId = harness.AddOffice();
        harness.AddProfile("manager", officeId);
        harness.AddTask(officeId, "manager", harness.Now.AddMinutes(-30), title: "overdue-1");
        harness.AddTask(officeId, "manager", harness.Now.AddMinutes(-20), title: "overdue-2");
        harness.AddTask(officeId, "manager", harness.Now.AddMinutes(-10), title: "overdue-3");
        harness.AddTask(officeId, "manager", harness.Now.AddMinutes(30), title: "soon");
        await harness.Db.SaveChangesAsync();

        var created = await harness.Sut.ProcessDueTasksAsync();

        Assert.Equal(2, created);
        var notifications = await harness.Db.CrmTaskNotifications.ToListAsync();
        Assert.Equal(2, notifications.Count);
        Assert.All(notifications, x => Assert.Equal(CrmTaskNotificationKinds.Overdue, x.Kind));
        Assert.Equal(2, harness.Realtime.Sent.Count);
    }

    [Fact]
    public async Task Notifications_AreIsolatedByOfficeAndRecipient()
    {
        await using var harness = Harness.Create();
        var officeA = harness.AddOffice(name: "office-a");
        var officeB = harness.AddOffice(name: "office-b");
        harness.AddProfile("manager-a", officeA);
        harness.AddProfile("manager-b", officeA);
        harness.AddProfile("manager-c", officeB);
        harness.AddTask(officeA, "manager-a", harness.Now.AddMinutes(30), title: "A");
        harness.AddTask(officeA, "manager-b", harness.Now.AddMinutes(30), title: "B");
        harness.AddTask(officeB, "manager-c", harness.Now.AddMinutes(30), title: "C");
        harness.AddTask(officeB, "manager-a", harness.Now.AddMinutes(30), title: "wrong office");
        await harness.Db.SaveChangesAsync();

        Assert.Equal(3, await harness.Sut.ProcessDueTasksAsync());

        Assert.Single((await harness.Sut.GetAsync(officeA, "manager-a", false, 50)).Items);
        Assert.Single((await harness.Sut.GetAsync(officeA, "manager-b", false, 50)).Items);
        Assert.Single((await harness.Sut.GetAsync(officeB, "manager-c", false, 50)).Items);
        Assert.Empty((await harness.Sut.GetAsync(officeB, "manager-a", false, 50)).Items);
        Assert.Empty((await harness.Sut.GetAsync(officeA, "manager-c", false, 50)).Items);
        Assert.Equal(
            ["manager-a", "manager-b", "manager-c"],
            harness.Realtime.Sent.Select(x => x.RecipientUserId).OrderBy(x => x).ToArray());
    }

    [Fact]
    public async Task Notifications_StopBeingActiveWhenRecipientLeavesOffice()
    {
        await using var harness = Harness.Create();
        var officeId = harness.AddOffice();
        var otherOfficeId = harness.AddOffice(name: "other-office");
        harness.AddProfile("manager", officeId);
        harness.AddTask(officeId, "manager", harness.Now.AddMinutes(30));
        await harness.Db.SaveChangesAsync();

        Assert.Equal(1, await harness.Sut.ProcessDueTasksAsync());
        var profile = await harness.Db.PanelUserProfiles.SingleAsync(x => x.UserId == "manager");
        profile.OfficeId = otherOfficeId;
        await harness.Db.SaveChangesAsync();

        Assert.Equal(0, (await harness.Sut.GetSummaryAsync(officeId, "manager")).UnreadCount);
        Assert.Empty((await harness.Sut.GetAsync(officeId, "manager", false, 50)).Items);
    }

    [Fact]
    public async Task ReadOperations_AreScopedAndIdempotent()
    {
        await using var harness = Harness.Create();
        var officeId = harness.AddOffice();
        var otherOfficeId = harness.AddOffice(name: "other-office");
        harness.AddProfile("manager", officeId);
        harness.AddProfile("other-manager", officeId);
        var dueAt = harness.Now.AddHours(23);
        harness.AddTask(officeId, "manager", dueAt);
        await harness.Db.SaveChangesAsync();

        Assert.Equal(1, await harness.Sut.ProcessDueTasksAsync());
        harness.Time.Advance(TimeSpan.FromHours(22.5));
        Assert.Equal(1, await harness.Sut.ProcessDueTasksAsync());
        harness.Time.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(1, await harness.Sut.ProcessDueTasksAsync());

        var all = await harness.Sut.GetAsync(officeId, "manager", unreadOnly: false, limit: 50);
        Assert.Equal(3, all.UnreadCount);
        Assert.Equal(3, all.Items.Count);
        var notificationId = all.Items[0].Id;

        Assert.False(await harness.Sut.MarkReadAsync(officeId, "other-manager", notificationId));
        Assert.False(await harness.Sut.MarkReadAsync(otherOfficeId, "manager", notificationId));
        Assert.True(await harness.Sut.MarkReadAsync(officeId, "manager", notificationId));
        Assert.True(await harness.Sut.MarkReadAsync(officeId, "manager", notificationId));

        var unread = await harness.Sut.GetAsync(officeId, "manager", unreadOnly: true, limit: 50);
        Assert.Equal(2, unread.UnreadCount);
        Assert.Equal(2, unread.Items.Count);
        Assert.Equal(2, await harness.Sut.MarkAllReadAsync(officeId, "manager"));
        Assert.Equal(0, await harness.Sut.MarkAllReadAsync(officeId, "manager"));
        Assert.Equal(0, (await harness.Sut.GetSummaryAsync(officeId, "manager")).UnreadCount);

        var readBack = await harness.Sut.GetAsync(officeId, "manager", unreadOnly: false, limit: 50);
        Assert.Equal(3, readBack.Items.Count);
        Assert.All(readBack.Items, x => Assert.NotNull(x.ReadAtUtc));
    }

    [Fact]
    public async Task Cleanup_RemovesOnlyOldReadOrDismissedRows()
    {
        await using var harness = Harness.Create();
        var officeId = harness.AddOffice();
        harness.AddProfile("manager", officeId);
        var task = harness.AddTask(officeId, "manager", harness.Now.AddMinutes(30));
        var oldRead = NewNotification(task, harness.Now.AddDays(-91));
        oldRead.ReadAtUtc = harness.Now.AddDays(-90);
        var oldUnread = NewNotification(task, harness.Now.AddDays(-91), CrmTaskNotificationKinds.DueIn1Hour);
        var recentDismissed = NewNotification(task, harness.Now.AddDays(-1), CrmTaskNotificationKinds.Overdue);
        recentDismissed.DismissedAtUtc = harness.Now.AddHours(-1);
        harness.Db.CrmTaskNotifications.AddRange(oldRead, oldUnread, recentDismissed);
        await harness.Db.SaveChangesAsync();

        Assert.Equal(1, await harness.Sut.CleanupAsync());
        Assert.DoesNotContain(harness.Db.CrmTaskNotifications, x => x.Id == oldRead.Id);
        Assert.Contains(harness.Db.CrmTaskNotifications, x => x.Id == oldUnread.Id);
        Assert.Contains(harness.Db.CrmTaskNotifications, x => x.Id == recentDismissed.Id);
    }

    [Fact]
    public void Model_DefinesUniqueReminderIdentity()
    {
        using var harness = Harness.Create();
        var entity = harness.Db.Model.FindEntityType(typeof(CrmTaskNotificationEntity));

        Assert.NotNull(entity);
        var uniqueIndex = Assert.Single(
            entity.GetIndexes(),
            index => index.Properties.Select(x => x.Name).SequenceEqual(
                [nameof(CrmTaskNotificationEntity.TaskId), nameof(CrmTaskNotificationEntity.ReminderVersion), nameof(CrmTaskNotificationEntity.Kind)]));
        Assert.True(uniqueIndex.IsUnique);
    }

    private static void AddDueTaskWithProfile(Harness harness, Guid officeId, string userId)
    {
        harness.AddProfile(userId, officeId);
        harness.AddTask(officeId, userId, harness.Now.AddMinutes(30));
    }

    private static CrmTaskNotificationEntity NewNotification(
        CrmTaskEntity task,
        DateTime createdAtUtc,
        string kind = CrmTaskNotificationKinds.DueIn24Hours) =>
        new()
        {
            Id = Guid.NewGuid(),
            OfficeId = task.OfficeId,
            TaskId = task.Id,
            ReminderVersion = task.ReminderVersion,
            RecipientUserId = task.AssigneeUserId,
            Kind = kind,
            DueAtUtc = task.DueAtUtc!.Value,
            CreatedAtUtc = createdAtUtc
        };

    private sealed class Harness : IAsyncDisposable, IDisposable
    {
        private Harness(
            OrbitaDbContext db,
            MutableTimeProvider time,
            CrmDeadlineNotificationOptions settings,
            CapturingRealtimeNotifier realtime)
        {
            Db = db;
            Time = time;
            Settings = settings;
            Realtime = realtime;
            Sut = new CrmDeadlineNotificationService(
                db,
                time,
                Options.Create(settings),
                realtime,
                NullLogger<CrmDeadlineNotificationService>.Instance);
        }

        public OrbitaDbContext Db { get; }
        public MutableTimeProvider Time { get; }
        public CrmDeadlineNotificationOptions Settings { get; }
        public CapturingRealtimeNotifier Realtime { get; }
        public CrmDeadlineNotificationService Sut { get; }
        public DateTime Now => Time.GetUtcNow().UtcDateTime;

        public static Harness Create(bool globalEnabled = true, int batchSize = 200)
        {
            var options = new DbContextOptionsBuilder<OrbitaDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options;
            var db = new OrbitaDbContext(options);
            var time = new MutableTimeProvider(Start);
            var settings = new CrmDeadlineNotificationOptions
            {
                Enabled = globalEnabled,
                FirstReminderMinutes = 24 * 60,
                FinalReminderMinutes = 60,
                BatchSize = batchSize,
                RetentionDays = 90
            };
            return new Harness(db, time, settings, new CapturingRealtimeNotifier());
        }

        public Guid AddOffice(
            string? name = null,
            bool isEnabled = true,
            bool crmEnabled = true,
            bool notificationsEnabled = true,
            bool activationPresent = true,
            DateTime? enabledAtUtc = null)
        {
            var officeId = Guid.NewGuid();
            Db.Offices.Add(new OfficeEntity
            {
                Id = officeId,
                Name = name ?? $"office-{officeId:N}",
                RegistrationSecretHash = "hash",
                CreatedAtUtc = Now.AddDays(-1),
                IsEnabled = isEnabled,
                CrmEnabled = crmEnabled,
                CrmDeadlineNotificationsEnabled = notificationsEnabled,
                CrmDeadlineNotificationsEnabledAtUtc = activationPresent
                    ? enabledAtUtc ?? Now.AddMinutes(-1)
                    : null
            });
            return officeId;
        }

        public void AddProfile(string userId, Guid officeId) =>
            Db.PanelUserProfiles.Add(new PanelUserProfileEntity
            {
                UserId = userId,
                OfficeId = officeId
            });

        public CrmTaskEntity AddTask(
            Guid officeId,
            string assigneeUserId,
            DateTime? dueAtUtc,
            string status = CrmTaskStatuses.Open,
            DateTime? reminderChangedAtUtc = null,
            string? title = null)
        {
            var task = new CrmTaskEntity
            {
                Id = Guid.NewGuid(),
                OfficeId = officeId,
                Title = title ?? "Call candidate",
                AssigneeUserId = assigneeUserId,
                CreatorUserId = "creator",
                CreatorName = "Creator",
                DueAtUtc = dueAtUtc,
                Status = status,
                ReminderVersion = Guid.NewGuid(),
                ReminderVersionChangedAtUtc = reminderChangedAtUtc ?? Now,
                CreatedAtUtc = Now
            };
            Db.CrmTasks.Add(task);
            return task;
        }

        public void Dispose() => Db.Dispose();

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan value) => _now = _now.Add(value);
    }

    private sealed class CapturingRealtimeNotifier : ICrmNotificationRealtimeNotifier
    {
        public List<SentNotification> Sent { get; } = [];
        public bool ThrowOnNotify { get; set; }

        public Task NotifyAsync(
            string recipientUserId,
            CrmTaskNotificationDto notification,
            CancellationToken ct = default)
        {
            if (ThrowOnNotify)
            {
                throw new InvalidOperationException("Simulated realtime failure.");
            }

            Sent.Add(new SentNotification(recipientUserId, notification));
            return Task.CompletedTask;
        }
    }

    private sealed record SentNotification(
        string RecipientUserId,
        CrmTaskNotificationDto Notification);
}
