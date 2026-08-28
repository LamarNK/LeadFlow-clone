using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.IO.Compression;
using Orbita.Api.Data;
using Orbita.Api.Options;
using Orbita.Api.Services;
using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class CrmWorkspaceServiceTests
{
    private static readonly Guid OfficeId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid WorkerId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact]
    public async Task DeleteCard_NonAdministrator_IsRejected()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var response = await SeedResponseAsync(harness.Db, "delete-denied");
        var card = NewCard(response.Id);
        harness.Db.CrmCandidateCards.Add(card);
        await harness.Db.SaveChangesAsync();

        var (ok, error, cardName) = await harness.Sut.DeleteCardAsync(card.Id, isAdministrator: false);

        Assert.False(ok);
        Assert.Contains("только администратор", error, StringComparison.OrdinalIgnoreCase);
        Assert.Null(cardName);
        Assert.True(await harness.Db.CrmCandidateCards.AnyAsync(x => x.Id == card.Id));
        Assert.True(await harness.Db.CandidateResponses.AnyAsync(x => x.Id == response.Id));
    }

    [Fact]
    public async Task DeleteCard_Administrator_RemovesCardContentAndPreservesSourceEvidence()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var response = await SeedResponseAsync(harness.Db, "delete-admin");
        var card = NewCard(response.Id);
        var task = new CrmTaskEntity
        {
            Id = Guid.NewGuid(),
            OfficeId = OfficeId,
            CardId = card.Id,
            Title = "Связанная задача",
            AssigneeUserId = "manager-id",
            CreatorUserId = "admin-id",
            CreatorName = "Администратор",
            CreatedAtUtc = DateTime.UtcNow,
            ReminderVersionChangedAtUtc = DateTime.UtcNow
        };
        var attachmentId = Guid.NewGuid();
        var attachmentRelativePath = $"{task.Id:N}/{attachmentId:N}.bin";
        var attachmentFullPath = Path.Combine(harness.AttachmentRoot, attachmentRelativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(attachmentFullPath)!);
        await File.WriteAllTextAsync(attachmentFullPath, "test");

        harness.Db.CrmCandidateCards.Add(card);
        harness.Db.CrmCandidateNotes.Add(new CrmCandidateNoteEntity
        {
            Id = Guid.NewGuid(),
            CardId = card.Id,
            AuthorUserId = "admin-id",
            AuthorName = "Администратор",
            Text = "Заметка",
            CreatedAtUtc = DateTime.UtcNow
        });
        harness.Db.CrmCandidateHistory.Add(new CrmCandidateHistoryEntity
        {
            Id = Guid.NewGuid(),
            CardId = card.Id,
            Action = "test",
            ActorUserId = "admin-id",
            ActorName = "Администратор",
            CreatedAtUtc = DateTime.UtcNow
        });
        harness.Db.CrmTasks.Add(task);
        harness.Db.CrmTaskNotifications.Add(new CrmTaskNotificationEntity
        {
            Id = Guid.NewGuid(),
            OfficeId = OfficeId,
            TaskId = task.Id,
            ReminderVersion = task.ReminderVersion,
            RecipientUserId = "manager-id",
            Kind = "test",
            DueAtUtc = DateTime.UtcNow,
            CreatedAtUtc = DateTime.UtcNow
        });
        harness.Db.CrmTaskComments.Add(new CrmTaskCommentEntity
        {
            Id = Guid.NewGuid(),
            TaskId = task.Id,
            AuthorUserId = "admin-id",
            AuthorName = "Администратор",
            Text = "Комментарий",
            CreatedAtUtc = DateTime.UtcNow
        });
        harness.Db.CrmTaskAttachments.Add(new CrmTaskAttachmentEntity
        {
            Id = attachmentId,
            TaskId = task.Id,
            FileName = "test.txt",
            ContentType = "text/plain",
            SizeBytes = 4,
            UploadedByUserId = "admin-id",
            UploadedByName = "Администратор",
            RelativePath = attachmentRelativePath,
            CreatedAtUtc = DateTime.UtcNow
        });
        harness.Db.CrmOutboundChatMessages.Add(new CrmOutboundChatMessageEntity
        {
            Id = Guid.NewGuid(),
            CardId = card.Id,
            ResponseId = response.Id,
            AuthorUserId = "manager-id",
            AuthorName = "Менеджер",
            Text = "Сообщение",
            CreatedAtUtc = DateTime.UtcNow
        });
        harness.Db.CrmCardChatReads.Add(new CrmCardChatReadEntity
        {
            CardId = card.Id,
            UserId = "manager-id",
            LastReadAtUtc = DateTime.UtcNow,
            ContentHash = "hash"
        });
        var call = new CrmCallEntity
        {
            Id = Guid.NewGuid(),
            OfficeId = OfficeId,
            CardId = card.Id,
            Provider = "test",
            ExternalCallId = Guid.NewGuid().ToString("N"),
            Direction = "outbound",
            CallerPhone = "201",
            CalledPhone = "79990001122",
            ClientPhoneNormalized = "79990001122",
            StartedAtUtc = DateTime.UtcNow,
            ReceivedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };
        var delivery = new ResponseCrmDeliveryEntity
        {
            Id = Guid.NewGuid(),
            ResponseId = response.Id,
            OfficeId = OfficeId,
            CardId = card.Id,
            Outcome = "created",
            Source = "test",
            CreatedAtUtc = DateTime.UtcNow
        };
        var alert = new CrmDeskAlertEntity
        {
            Id = Guid.NewGuid(),
            OfficeId = OfficeId,
            RecipientUserId = "manager-id",
            Kind = "test",
            CardId = card.Id,
            Title = "Оповещение",
            Message = "Текст",
            CreatedAtUtc = DateTime.UtcNow
        };
        harness.Db.CrmCalls.Add(call);
        harness.Db.ResponseCrmDeliveries.Add(delivery);
        harness.Db.CrmDeskAlerts.Add(alert);
        await harness.Db.SaveChangesAsync();

        var (ok, error, cardName) = await harness.Sut.DeleteCardAsync(card.Id, isAdministrator: true);

        Assert.True(ok, error);
        Assert.Equal(response.FullName, cardName);
        harness.Db.ChangeTracker.Clear();
        Assert.False(await harness.Db.CrmCandidateCards.AnyAsync(x => x.Id == card.Id));
        Assert.False(await harness.Db.CrmCandidateNotes.AnyAsync(x => x.CardId == card.Id));
        Assert.False(await harness.Db.CrmCandidateHistory.AnyAsync(x => x.CardId == card.Id));
        Assert.False(await harness.Db.CrmTasks.AnyAsync(x => x.CardId == card.Id));
        Assert.False(await harness.Db.CrmTaskNotifications.AnyAsync(x => x.TaskId == task.Id));
        Assert.False(await harness.Db.CrmTaskComments.AnyAsync(x => x.TaskId == task.Id));
        Assert.False(await harness.Db.CrmTaskAttachments.AnyAsync(x => x.TaskId == task.Id));
        Assert.False(await harness.Db.CrmOutboundChatMessages.AnyAsync(x => x.CardId == card.Id));
        Assert.False(await harness.Db.CrmCardChatReads.AnyAsync(x => x.CardId == card.Id));
        Assert.True(await harness.Db.CandidateResponses.AnyAsync(x => x.Id == response.Id));
        Assert.Null((await harness.Db.CrmCalls.SingleAsync(x => x.Id == call.Id)).CardId);
        Assert.Null((await harness.Db.ResponseCrmDeliveries.SingleAsync(x => x.Id == delivery.Id)).CardId);
        Assert.Null((await harness.Db.CrmDeskAlerts.SingleAsync(x => x.Id == alert.Id)).CardId);
        Assert.False(File.Exists(attachmentFullPath));
    }

    [Fact]
    public async Task CreateCard_WhenCrmDisabled_DoesNothing()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: false);
        var response = await SeedResponseAsync(harness.Db);

        await harness.Sut.CreateCardForResponseAsync(response);

        Assert.Empty(harness.Db.CrmCandidateCards);
    }

    [Fact]
    public async Task CreateCard_WhenEnabled_CreatesCardWithoutTouchingBitrixFields()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var response = await SeedResponseAsync(harness.Db);
        response.Status = ResponseStatuses.ActionRequired;
        response.BitrixEntityId = "keep-me";
        await harness.Db.SaveChangesAsync();

        await harness.Sut.CreateCardForResponseAsync(response);

        var card = Assert.Single(harness.Db.CrmCandidateCards);
        Assert.Equal(response.Id, card.ResponseId);
        Assert.Equal(CrmStages.Lead, card.Stage);
        Assert.Null(card.ManagerUserId);
        Assert.Equal(ResponseStatuses.ActionRequired, response.Status);
        Assert.Equal("keep-me", response.BitrixEntityId);
    }

    [Fact]
    public async Task CreateCard_IsAssignedAfterFiveMinuteShiftCollectionWindow()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var manager = await harness.CreateManagerAsync("mgr@test.local", capacity: 5, onShift: false);
        Assert.True(await harness.Sut.StartShiftAsync(OfficeId, manager.Id));
        var response = await SeedResponseAsync(harness.Db);

        await harness.Sut.CreateCardForResponseAsync(response);

        var card = Assert.Single(harness.Db.CrmCandidateCards);
        Assert.Null(card.ManagerUserId);

        harness.Clock.Advance(TimeSpan.FromMinutes(6));
        await harness.Sut.ProcessDueDailyDistributionsAsync();

        Assert.Equal(manager.Id, card.ManagerUserId);
        Assert.True(card.IsInActiveLoad);
    }

    [Fact]
    public async Task StartShift_OfficeLead_CanStartShiftButDoesNotReceiveQueueCards()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var lead = await harness.CreateDeskUserAsync(
            "office-lead@test.local",
            capacity: 2,
            onShift: false,
            PanelRoles.OfficeLead);
        var r1 = await SeedResponseAsync(harness.Db, "lead-src-1");
        var r2 = await SeedResponseAsync(harness.Db, "lead-src-2");
        harness.Db.CrmCandidateCards.AddRange(NewCard(r1.Id), NewCard(r2.Id));
        await harness.Db.SaveChangesAsync();

        var ok = await harness.Sut.StartShiftAsync(OfficeId, lead.Id);
        Assert.True(ok);

        harness.Clock.Advance(TimeSpan.FromMinutes(6));
        await harness.Sut.ProcessDueDailyDistributionsAsync();

        Assert.Equal(0, await harness.Db.CrmCandidateCards.CountAsync(x => x.ManagerUserId == lead.Id));
        Assert.Equal(2, await harness.Db.CrmCandidateCards.CountAsync(x => x.ManagerUserId == null));
        Assert.Empty(harness.Db.CrmDailyDistributionSessions);
        var profile = await harness.Db.PanelUserProfiles.SingleAsync(x => x.UserId == lead.Id);
        Assert.True(profile.CrmShiftActive);
        Assert.Single(await harness.Db.CrmManagerShifts.Where(x => x.ManagerUserId == lead.Id).ToListAsync());
    }

    [Fact]
    public async Task ActiveFlagWithoutTodayStartEvent_DoesNotCreateDistributionSession()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var manager = await harness.CreateManagerAsync("stale-flag@test.local", capacity: 5, onShift: true);
        var profile = await harness.Db.PanelUserProfiles.SingleAsync(x => x.UserId == manager.Id);
        profile.CrmShiftStartedAtUtc = harness.Clock.GetUtcNow().AddDays(-1).UtcDateTime;
        var response = await SeedResponseAsync(harness.Db, "stale-flag-card");
        harness.Db.CrmCandidateCards.Add(NewCard(response.Id, manager.Id));
        await harness.Db.SaveChangesAsync();

        harness.Clock.Advance(TimeSpan.FromMinutes(6));
        await harness.Sut.ProcessDueDailyDistributionsAsync();

        Assert.Empty(harness.Db.CrmDailyDistributionSessions);
    }

    [Fact]
    public async Task TodayDistribution_ExcludesManagerWhoseShiftStartedOnPreviousBusinessDay()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var stale = await harness.CreateManagerAsync("stale-recipient@test.local", capacity: 300, onShift: true);
        var current = await harness.CreateManagerAsync("current-recipient@test.local", capacity: 300, onShift: false);
        var staleProfile = await harness.Db.PanelUserProfiles.SingleAsync(x => x.UserId == stale.Id);
        staleProfile.CrmShiftStartedAtUtc = harness.Clock.GetUtcNow().AddDays(-1).UtcDateTime;

        for (var index = 0; index < 4; index++)
        {
            var response = await SeedResponseAsync(harness.Db, $"stale-recipient-{index}");
            var card = NewCard(response.Id);
            card.Stage = index < 2 ? CrmStages.Lead : CrmStages.Ndz73;
            card.ManagerUserId = null;
            card.IsInActiveLoad = false;
            harness.Db.CrmCandidateCards.Add(card);
        }

        await harness.Db.SaveChangesAsync();
        Assert.True(await harness.Sut.StartShiftAsync(OfficeId, current.Id));

        harness.Clock.Advance(TimeSpan.FromMinutes(6));
        await harness.Sut.ProcessDueDailyDistributionsAsync();

        Assert.Equal(0, await harness.Db.CrmCandidateCards.CountAsync(x => x.ManagerUserId == stale.Id));
        Assert.Equal(4, await harness.Db.CrmCandidateCards.CountAsync(x => x.ManagerUserId == current.Id));
    }

    [Fact]
    public async Task StartShift_DistributesWholeLeadPoolIgnoringCapacityAfterFiveMinutes()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var manager = await harness.CreateManagerAsync("shift@test.local", capacity: 1, onShift: false);
        var secondManager = await harness.CreateManagerAsync("shift-2@test.local", capacity: 1, onShift: false);
        var r1 = await SeedResponseAsync(harness.Db, "src-1");
        var r2 = await SeedResponseAsync(harness.Db, "src-2");
        var r3 = await SeedResponseAsync(harness.Db, "src-3");
        harness.Db.CrmCandidateCards.AddRange(NewCard(r1.Id), NewCard(r2.Id), NewCard(r3.Id));
        await harness.Db.SaveChangesAsync();

        var ok = await harness.Sut.StartShiftAsync(OfficeId, manager.Id);
        Assert.True(ok);
        Assert.True(await harness.Sut.StartShiftAsync(OfficeId, secondManager.Id));

        Assert.Equal(3, await harness.Db.CrmCandidateCards.CountAsync(x => x.ManagerUserId == null));
        harness.Clock.Advance(TimeSpan.FromMinutes(6));
        await harness.Sut.ProcessDueDailyDistributionsAsync();

        var counts = await harness.Db.CrmCandidateCards
            .GroupBy(x => x.ManagerUserId)
            .Select(x => x.Count())
            .OrderBy(x => x)
            .ToListAsync();
        Assert.Equal([1, 2], counts);
        Assert.Equal(0, await harness.Db.CrmCandidateCards.CountAsync(x => x.ManagerUserId == null));
        var profile = await harness.Db.PanelUserProfiles.SingleAsync(x => x.UserId == manager.Id);
        Assert.True(profile.CrmShiftActive);
        Assert.NotNull(profile.CrmShiftStartedAtUtc);

        var history = Assert.Single(await harness.Db.CrmManagerShifts.Where(x => x.ManagerUserId == manager.Id).ToListAsync());
        Assert.Equal(OfficeId, history.OfficeId);
        Assert.Null(history.EndedAtUtc);
        Assert.Null(history.EndReason);
        Assert.Equal(profile.CrmShiftStartedAtUtc, history.StartedAtUtc);
    }

    [Fact]
    public async Task DailyDistribution_BalancesLeadAndNdzSeparately_AndNormalizesNdz2()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var first = await harness.CreateManagerAsync("pool-1@test.local", capacity: 1, onShift: false);
        var second = await harness.CreateDeskUserAsync(
            "pool-2@test.local",
            capacity: 1,
            onShift: false,
            PanelRoles.SeniorManager);
        Assert.True(await harness.Sut.StartShiftAsync(OfficeId, first.Id));
        Assert.True(await harness.Sut.StartShiftAsync(OfficeId, second.Id));

        for (var index = 0; index < 7; index++)
        {
            var response = await SeedResponseAsync(harness.Db, $"pool-{index}");
            var card = NewCard(response.Id);
            card.Stage = index < 4
                ? CrmStages.Lead
                : index % 2 == 0 ? CrmStages.Ndz73 : CrmStages.Ndz26;
            if (CrmDailyDistribution.IsNdz(card.Stage))
            {
                card.IsInActiveLoad = false;
            }
            harness.Db.CrmCandidateCards.Add(card);
        }

        await harness.Db.SaveChangesAsync();
        harness.Clock.Advance(TimeSpan.FromMinutes(6));
        await harness.Sut.ProcessDueDailyDistributionsAsync();

        var recipientIds = new[] { first.Id, second.Id };
        Assert.All(
            await harness.Db.CrmCandidateCards.ToListAsync(),
            card => Assert.Contains(card.ManagerUserId, recipientIds));
        Assert.DoesNotContain(
            await harness.Db.CrmCandidateCards.ToListAsync(),
            card => card.Stage == CrmStages.Ndz26);
        Assert.All(
            await harness.Db.CrmCandidateCards.Where(x => x.Stage == CrmStages.Ndz73).ToListAsync(),
            card => Assert.True(card.IsInActiveLoad));

        var leadCounters = await harness.Db.CrmDailyDistributionCounters
            .Where(x => x.Pool == CrmDailyDistribution.LeadPool)
            .Select(x => x.AssignedCount)
            .OrderBy(x => x)
            .ToListAsync();
        var ndzCounters = await harness.Db.CrmDailyDistributionCounters
            .Where(x => x.Pool == CrmDailyDistribution.NdzPool)
            .Select(x => x.AssignedCount)
            .OrderBy(x => x)
            .ToListAsync();
        Assert.Equal([2, 2], leadCounters);
        Assert.Equal([1, 2], ndzCounters);
    }

    [Fact]
    public async Task DailyDistribution_UsesProductionNdzStageNamesFromOfficeFunnel()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(
            harness.Db,
            crmEnabled: true,
            stages: ["Лид", "НДЗ", "НДЗ 2", "Переговоры", "Анкета"]);
        var first = await harness.CreateManagerAsync("prod-ndz-1@test.local", capacity: 1, onShift: false);
        var second = await harness.CreateManagerAsync("prod-ndz-2@test.local", capacity: 1, onShift: false);
        Assert.True(await harness.Sut.StartShiftAsync(OfficeId, first.Id));
        Assert.True(await harness.Sut.StartShiftAsync(OfficeId, second.Id));

        for (var index = 0; index < 9; index++)
        {
            var response = await SeedResponseAsync(harness.Db, $"prod-ndz-{index}");
            var card = NewCard(response.Id);
            card.Stage = index < 3 ? CrmStages.Lead : index % 2 == 0 ? "НДЗ" : "НДЗ 2";
            harness.Db.CrmCandidateCards.Add(card);
        }

        await harness.Db.SaveChangesAsync();
        harness.Clock.Advance(TimeSpan.FromMinutes(6));
        await harness.Sut.ProcessDueDailyDistributionsAsync();

        var cards = await harness.Db.CrmCandidateCards.ToListAsync();
        Assert.Equal(9, cards.Count);
        Assert.DoesNotContain(cards, card => card.Stage == "НДЗ 2");
        Assert.Equal(6, cards.Count(card => card.Stage == "НДЗ"));
        Assert.DoesNotContain(cards, card => card.Stage == CrmStages.Ndz73 || card.Stage == CrmStages.Ndz26);
        Assert.All(cards, card => Assert.Contains(card.ManagerUserId, new[] { first.Id, second.Id }));

        var ndzCounts = cards
            .Where(card => card.Stage == "НДЗ")
            .GroupBy(card => card.ManagerUserId)
            .Select(group => group.Count())
            .OrderBy(count => count)
            .ToList();
        Assert.Equal([3, 3], ndzCounts);
    }

    [Fact]
    public async Task DailyDistribution_DoesNotTakeEmptyStageIntoNdzPool()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(
            harness.Db,
            crmEnabled: true,
            stages: ["Лид", "НДЗ", "НДЗ 2", "Пустые", "Переговоры"]);
        var first = await harness.CreateManagerAsync("empty-pool-1@test.local", capacity: 1, onShift: false);
        var second = await harness.CreateManagerAsync("empty-pool-2@test.local", capacity: 1, onShift: false);
        Assert.True(await harness.Sut.StartShiftAsync(OfficeId, first.Id));
        Assert.True(await harness.Sut.StartShiftAsync(OfficeId, second.Id));

        var leadResponse = await SeedResponseAsync(harness.Db, "empty-pool-lead");
        var ndzResponse = await SeedResponseAsync(harness.Db, "empty-pool-ndz");
        var emptyResponse = await SeedResponseAsync(harness.Db, "empty-pool-empty");
        var leadCard = NewCard(leadResponse.Id);
        var ndzCard = NewCard(ndzResponse.Id);
        ndzCard.Stage = "НДЗ 2";
        var emptyCard = NewCard(emptyResponse.Id);
        emptyCard.Stage = "Пустые";
        emptyCard.IsInActiveLoad = false;
        harness.Db.CrmCandidateCards.AddRange(leadCard, ndzCard, emptyCard);
        await harness.Db.SaveChangesAsync();

        harness.Clock.Advance(TimeSpan.FromMinutes(6));
        await harness.Sut.ProcessDueDailyDistributionsAsync();

        await harness.Db.Entry(leadCard).ReloadAsync();
        await harness.Db.Entry(ndzCard).ReloadAsync();
        await harness.Db.Entry(emptyCard).ReloadAsync();
        Assert.NotNull(leadCard.ManagerUserId);
        Assert.NotNull(ndzCard.ManagerUserId);
        Assert.Equal("НДЗ", ndzCard.Stage);
        Assert.Null(emptyCard.ManagerUserId);
        Assert.Equal("Пустые", emptyCard.Stage);
        Assert.False(emptyCard.IsInActiveLoad);
        Assert.DoesNotContain(
            await harness.Db.CrmCandidateHistory
                .Where(x => x.CardId == emptyCard.Id)
                .ToListAsync(),
            item => item.Action is "Assigned" or "StageChanged");
    }

    [Fact]
    public async Task NewLeadsDuringDay_ContinueByDailyReceivedCount()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var first = await harness.CreateManagerAsync("later-1@test.local", capacity: 1, onShift: false);
        var second = await harness.CreateManagerAsync("later-2@test.local", capacity: 1, onShift: false);
        Assert.True(await harness.Sut.StartShiftAsync(OfficeId, first.Id));
        Assert.True(await harness.Sut.StartShiftAsync(OfficeId, second.Id));

        for (var index = 0; index < 4; index++)
        {
            var response = await SeedResponseAsync(harness.Db, $"morning-{index}");
            harness.Db.CrmCandidateCards.Add(NewCard(response.Id));
        }

        await harness.Db.SaveChangesAsync();
        harness.Clock.Advance(TimeSpan.FromMinutes(6));
        await harness.Sut.ProcessDueDailyDistributionsAsync();

        for (var index = 0; index < 4; index++)
        {
            var response = await SeedResponseAsync(harness.Db, $"later-{index}");
            var result = await harness.Sut.TryCreateCardForDeliveryAsync(response, OfficeId);
            Assert.Null(result.Error);
        }

        var counts = await harness.Db.CrmCandidateCards
            .Where(x => x.ManagerUserId == first.Id || x.ManagerUserId == second.Id)
            .GroupBy(x => x.ManagerUserId)
            .Select(x => x.Count())
            .OrderBy(x => x)
            .ToListAsync();
        Assert.Equal([4, 4], counts);
        Assert.Equal(
            [4, 4],
            await harness.Db.CrmDailyDistributionCounters
                .Where(x => x.Pool == CrmDailyDistribution.LeadPool)
                .Select(x => x.AssignedCount)
                .OrderBy(x => x)
                .ToListAsync());
    }

    [Fact]
    public async Task StopShift_ClearsStartedAt_AndClosesHistory()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var manager = await harness.CreateManagerAsync("stop@test.local", capacity: 2, onShift: false);
        Assert.True(await harness.Sut.StartShiftAsync(OfficeId, manager.Id));

        var ok = await harness.Sut.StopShiftAsync(OfficeId, manager.Id);
        Assert.True(ok);

        var profile = await harness.Db.PanelUserProfiles.SingleAsync(x => x.UserId == manager.Id);
        Assert.False(profile.CrmShiftActive);
        Assert.Null(profile.CrmShiftStartedAtUtc);

        var history = Assert.Single(await harness.Db.CrmManagerShifts.Where(x => x.ManagerUserId == manager.Id).ToListAsync());
        Assert.NotNull(history.EndedAtUtc);
        Assert.Equal(CrmShiftEndReasons.Manual, history.EndReason);
        Assert.Equal(manager.Id, history.EndedByUserId);
        Assert.True(history.EndedAtUtc >= history.StartedAtUtc);
    }

    [Fact]
    public async Task ExpireStaleShifts_StopsPreviousBusinessDayShift()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var manager = await harness.CreateManagerAsync("stale@test.local", capacity: 2, onShift: false);
        Assert.True(await harness.Sut.StartShiftAsync(OfficeId, manager.Id));
        var profile = await harness.Db.PanelUserProfiles.SingleAsync(x => x.UserId == manager.Id);
        var started = DateTime.UtcNow.AddDays(-1);
        profile.CrmShiftStartedAtUtc = started;
        var open = await harness.Db.CrmManagerShifts.SingleAsync(x => x.ManagerUserId == manager.Id && x.EndedAtUtc == null);
        open.StartedAtUtc = started;
        await harness.Db.SaveChangesAsync();

        var expired = await harness.Sut.ExpireStaleShiftsAsync();
        Assert.Equal(1, expired);

        await harness.Db.Entry(profile).ReloadAsync();
        Assert.False(profile.CrmShiftActive);
        Assert.Null(profile.CrmShiftStartedAtUtc);

        await harness.Db.Entry(open).ReloadAsync();
        Assert.NotNull(open.EndedAtUtc);
        Assert.Equal(CrmShiftEndReasons.AutoDailyCutoff, open.EndReason);
        Assert.Null(open.EndedByUserId);
    }

    [Fact]
    public async Task ExpireStaleShifts_StopsLegacyActiveWithoutStartTime()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var manager = await harness.CreateManagerAsync("legacy@test.local", capacity: 2, onShift: true);
        var profile = await harness.Db.PanelUserProfiles.SingleAsync(x => x.UserId == manager.Id);
        profile.CrmShiftStartedAtUtc = null;
        await harness.Db.SaveChangesAsync();

        var expired = await harness.Sut.ExpireStaleShiftsAsync();
        Assert.Equal(1, expired);
        Assert.False(profile.CrmShiftActive);

        var history = Assert.Single(await harness.Db.CrmManagerShifts.Where(x => x.ManagerUserId == manager.Id).ToListAsync());
        Assert.NotNull(history.EndedAtUtc);
        Assert.Equal(CrmShiftEndReasons.LegacyCleanup, history.EndReason);
    }

    [Fact]
    public async Task StartShift_WhenAlreadyOpen_SupersedesPreviousHistory()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var manager = await harness.CreateManagerAsync("restart@test.local", capacity: 2, onShift: false);
        Assert.True(await harness.Sut.StartShiftAsync(OfficeId, manager.Id));
        Assert.True(await harness.Sut.StartShiftAsync(OfficeId, manager.Id));

        var rows = await harness.Db.CrmManagerShifts
            .Where(x => x.ManagerUserId == manager.Id)
            .OrderBy(x => x.StartedAtUtc)
            .ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(CrmShiftEndReasons.Superseded, rows[0].EndReason);
        Assert.NotNull(rows[0].EndedAtUtc);
        Assert.Null(rows[1].EndedAtUtc);
    }

    [Fact]
    public async Task GetBoard_Managers_ExposeShiftTiming()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var onShift = await harness.CreateManagerAsync("onshift-board@test.local", capacity: 5, onShift: false);
        var offShift = await harness.CreateManagerAsync("offshift-board@test.local", capacity: 5, onShift: false);
        Assert.True(await harness.Sut.StartShiftAsync(OfficeId, onShift.Id));
        Assert.True(await harness.Sut.StartShiftAsync(OfficeId, offShift.Id));
        Assert.True(await harness.Sut.StopShiftAsync(OfficeId, offShift.Id));

        var board = await harness.Sut.GetBoardAsync(OfficeId, onShift.Id, isAdmin: true);
        Assert.NotNull(board);

        var active = Assert.Single(board!.Managers, x => x.UserId == onShift.Id);
        Assert.True(active.IsShiftActive);
        Assert.NotNull(active.ShiftStartedAtUtc);
        Assert.Null(active.LastShiftEndedAtUtc);

        var inactive = Assert.Single(board.Managers, x => x.UserId == offShift.Id);
        Assert.False(inactive.IsShiftActive);
        Assert.Null(inactive.ShiftStartedAtUtc);
        Assert.NotNull(inactive.LastShiftEndedAtUtc);
    }

    [Fact]
    public async Task CreateCard_DoesNotAssignToStaleShift()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var manager = await harness.CreateManagerAsync("stale-assign@test.local", capacity: 5, onShift: true);
        var profile = await harness.Db.PanelUserProfiles.SingleAsync(x => x.UserId == manager.Id);
        profile.CrmShiftStartedAtUtc = DateTime.UtcNow.AddDays(-1);
        await harness.Db.SaveChangesAsync();
        var response = await SeedResponseAsync(harness.Db);

        await harness.Sut.CreateCardForResponseAsync(response);

        var card = Assert.Single(harness.Db.CrmCandidateCards);
        Assert.Null(card.ManagerUserId);
    }

    [Fact]
    public async Task Close_RemovesFromActiveLoad()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var manager = await harness.CreateManagerAsync("close@test.local", capacity: 5, onShift: true);
        var response = await SeedResponseAsync(harness.Db);
        var card = NewCard(response.Id, manager.Id);
        harness.Db.CrmCandidateCards.Add(card);
        await harness.Db.SaveChangesAsync();

        var (denied, deniedError) = await harness.Sut.CloseAsync(card.Id, CrmCloseReasons.NotRelevant, "  ", manager.Id, isAdmin: false);
        Assert.False(denied);
        Assert.Contains("комментарий", deniedError, StringComparison.OrdinalIgnoreCase);
        Assert.False(card.IsClosed);

        var (ok, error) = await harness.Sut.CloseAsync(card.Id, CrmCloseReasons.NotRelevant, "не интересно", manager.Id, isAdmin: false);
        Assert.True(ok, error);
        Assert.True(card.IsClosed);
        Assert.False(card.IsInActiveLoad);
        Assert.Equal(CrmCloseReasons.NotRelevant, card.CloseReason);
        Assert.DoesNotContain(harness.Db.CrmCandidateNotes, n => n.CardId == card.Id && n.Text == "не интересно");
        var detail = await harness.Sut.GetCardAsync(card.Id, manager.Id, isAdmin: false);
        var closeActivity = Assert.Single(detail!.Activity, item => item.Title == "Карточка закрыта");
        Assert.Equal(CrmCloseReasons.NotRelevant, closeActivity.Body);
        Assert.Equal("не интересно", closeActivity.ActionComment);
    }

    [Fact]
    public async Task CloseSuccess_RequiresReportAndStoresCategorizedFiles()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var manager = await harness.CreateManagerAsync("success-close@test.local", capacity: 5, onShift: true);
        var response = await SeedResponseAsync(harness.Db, "success-close");
        var card = NewCard(response.Id, manager.Id);
        harness.Db.CrmCandidateCards.Add(card);
        await harness.Db.SaveChangesAsync();

        var (plainClose, plainError) = await harness.Sut.CloseAsync(
            card.Id,
            CrmCloseReasons.Success,
            "Подписался",
            manager.Id,
            isAdmin: false);
        Assert.False(plainClose);
        Assert.Contains("отчёт", plainError, StringComparison.OrdinalIgnoreCase);

        var incomplete = new[]
        {
            SuccessUpload(CrmSuccessDocumentCategories.Correspondence, "chat.png", [1, 2, 3])
        };
        var (incompleteClose, incompleteError) = await harness.Sut.CloseSuccessAsync(
            card.Id,
            "Подписался",
            contractMissingReason: null,
            incomplete,
            manager.Id,
            isAdmin: false);
        Assert.False(incompleteClose);
        Assert.Contains("Билеты", incompleteError, StringComparison.OrdinalIgnoreCase);
        Assert.False(card.IsClosed);

        var uploads = new[]
        {
            SuccessUpload(CrmSuccessDocumentCategories.Correspondence, "chat.png", [1, 2, 3]),
            SuccessUpload(CrmSuccessDocumentCategories.Ticket, "ticket.pdf", "%PDF-test"u8.ToArray(), "application/pdf"),
            SuccessUpload(CrmSuccessDocumentCategories.TicketReceipt, "receipt.jpg", [4, 5, 6]),
            SuccessUpload(CrmSuccessDocumentCategories.Contract, "contract.png", [7, 8, 9]),
            SuccessUpload(CrmSuccessDocumentCategories.CandidateDocument, "passport.jpeg", [10, 11, 12]),
            SuccessUpload(CrmSuccessDocumentCategories.Other, "note.txt", "test"u8.ToArray(), "text/plain")
        };
        var (ok, error) = await harness.Sut.CloseSuccessAsync(
            card.Id,
            "Кандидат подписал контракт",
            contractMissingReason: null,
            uploads,
            manager.Id,
            isAdmin: false);

        Assert.True(ok, error);
        Assert.True(card.IsClosed);
        Assert.Equal(CrmCloseReasons.Success, card.CloseReason);
        Assert.False(card.IsInActiveLoad);
        Assert.Equal(uploads.Length, await harness.Db.CrmSuccessDocuments.CountAsync(x => x.CardId == card.Id));
        var detail = await harness.Sut.GetCardAsync(card.Id, manager.Id, isAdmin: false);
        Assert.Equal(uploads.Length, detail!.SuccessDocuments!.Count);
        Assert.All(
            await harness.Db.CrmSuccessDocuments.Where(x => x.CardId == card.Id).ToListAsync(),
            document => Assert.True(File.Exists(Path.Combine(
                harness.SuccessDocumentRoot,
                document.RelativePath.Replace('/', Path.DirectorySeparatorChar)))));
    }

    [Fact]
    public async Task CloseSuccess_AllowsMissingContractPhotoOnlyWithReason()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var manager = await harness.CreateManagerAsync("success-no-contract@test.local", capacity: 5, onShift: true);
        var response = await SeedResponseAsync(harness.Db, "success-no-contract");
        var card = NewCard(response.Id, manager.Id);
        harness.Db.CrmCandidateCards.Add(card);
        await harness.Db.SaveChangesAsync();

        var uploads = new[]
        {
            SuccessUpload(CrmSuccessDocumentCategories.Correspondence, "chat.png", [1, 2, 3]),
            SuccessUpload(CrmSuccessDocumentCategories.Ticket, "ticket.pdf", "%PDF-test"u8.ToArray(), "application/pdf"),
            SuccessUpload(CrmSuccessDocumentCategories.TicketReceipt, "receipt.jpg", [4, 5, 6]),
            SuccessUpload(CrmSuccessDocumentCategories.CandidateDocument, "passport.jpeg", [7, 8, 9])
        };

        var (withoutReason, reasonError) = await harness.Sut.CloseSuccessAsync(
            card.Id,
            "Кандидат подписался",
            contractMissingReason: null,
            uploads,
            manager.Id,
            isAdmin: false);
        Assert.False(withoutReason);
        Assert.Contains("причину", reasonError, StringComparison.OrdinalIgnoreCase);

        const string missingReason = "Кандидат пока не прислал фотографию контракта.";
        var (ok, error) = await harness.Sut.CloseSuccessAsync(
            card.Id,
            "Кандидат подписался",
            missingReason,
            uploads,
            manager.Id,
            isAdmin: false);

        Assert.True(ok, error);
        Assert.True(card.IsClosed);
        Assert.Equal(missingReason, card.SuccessContractMissingReason);
        var detail = await harness.Sut.GetCardAsync(card.Id, manager.Id, isAdmin: false);
        Assert.Equal(missingReason, detail!.SuccessContractMissingReason);
        Assert.DoesNotContain(detail.SuccessDocuments!, x => x.Category == CrmSuccessDocumentCategories.Contract);
    }

    [Fact]
    public async Task UpdateSuccessReport_IsRestrictedAndKeepsRequiredSectionsValid()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var manager = await harness.CreateManagerAsync("success-report-owner@test.local", capacity: 5, onShift: true);
        var officeLead = await harness.CreateDeskUserAsync(
            "success-report-lead@test.local",
            capacity: 5,
            onShift: true,
            PanelRoles.OfficeLead);
        var response = await SeedResponseAsync(harness.Db, "success-report-update");
        var card = NewCard(response.Id, manager.Id);
        harness.Db.CrmCandidateCards.Add(card);
        await harness.Db.SaveChangesAsync();

        var initialUploads = new[]
        {
            SuccessUpload(CrmSuccessDocumentCategories.Correspondence, "chat.png", [1, 2, 3]),
            SuccessUpload(CrmSuccessDocumentCategories.Ticket, "ticket.pdf", "%PDF-test"u8.ToArray(), "application/pdf"),
            SuccessUpload(CrmSuccessDocumentCategories.TicketReceipt, "receipt.jpg", [4, 5, 6]),
            SuccessUpload(CrmSuccessDocumentCategories.Contract, "contract.png", [7, 8, 9]),
            SuccessUpload(CrmSuccessDocumentCategories.CandidateDocument, "passport.jpeg", [10, 11, 12])
        };
        Assert.True((await harness.Sut.CloseSuccessAsync(
            card.Id,
            "Кандидат подписался",
            null,
            initialUploads,
            manager.Id,
            isAdmin: false)).Ok);

        var initialDocuments = await harness.Db.CrmSuccessDocuments
            .Where(document => document.CardId == card.Id)
            .ToListAsync();
        var initialPaths = initialDocuments.ToDictionary(
            document => document.Category,
            document => Path.Combine(harness.SuccessDocumentRoot, document.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
        var keptWithoutContract = initialDocuments
            .Where(document => document.Category != CrmSuccessDocumentCategories.Contract)
            .Select(document => document.Id)
            .ToArray();

        var denied = await harness.Sut.UpdateSuccessReportAsync(
            card.Id,
            keptWithoutContract,
            "Фото будет позже",
            [],
            manager.Id,
            canEditReport: false);
        Assert.False(denied.Ok);
        Assert.Equal(initialDocuments.Count, await harness.Db.CrmSuccessDocuments.CountAsync(document => document.CardId == card.Id));

        var missingRequired = await harness.Sut.UpdateSuccessReportAsync(
            card.Id,
            keptWithoutContract.Where(id => id != initialDocuments.Single(document => document.Category == CrmSuccessDocumentCategories.Ticket).Id).ToArray(),
            "Фото будет позже",
            [],
            officeLead.Id,
            canEditReport: true);
        Assert.False(missingRequired.Ok);
        Assert.Contains("Билеты", missingRequired.Error, StringComparison.OrdinalIgnoreCase);

        var updated = await harness.Sut.UpdateSuccessReportAsync(
            card.Id,
            keptWithoutContract,
            "Кандидат пришлёт фото после получения оригинала",
            [SuccessUpload(CrmSuccessDocumentCategories.Relationship, "relation.png", [13, 14, 15])],
            officeLead.Id,
            canEditReport: true);
        Assert.True(updated.Ok, updated.Error);

        var finalDocuments = await harness.Db.CrmSuccessDocuments
            .Where(document => document.CardId == card.Id)
            .ToListAsync();
        Assert.DoesNotContain(finalDocuments, document => document.Category == CrmSuccessDocumentCategories.Contract);
        Assert.Contains(finalDocuments, document => document.Category == CrmSuccessDocumentCategories.Relationship);
        Assert.Equal("Кандидат пришлёт фото после получения оригинала", card.SuccessContractMissingReason);
        Assert.False(File.Exists(initialPaths[CrmSuccessDocumentCategories.Contract]));
        Assert.Contains(harness.Db.CrmCandidateHistory, history =>
            history.CardId == card.Id && history.Action == "SuccessReportUpdated");
    }

    [Fact]
    public async Task OpenSuccessReportArchive_CreatesStructuredZipForElevatedOfficeUser()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var manager = await harness.CreateManagerAsync("archive-owner@test.local", capacity: 5, onShift: true);
        var senior = await harness.CreateDeskUserAsync(
            "archive-senior@test.local",
            capacity: 5,
            onShift: true,
            PanelRoles.SeniorManager);
        var response = await SeedResponseAsync(harness.Db, "archive-report");
        response.FullName = "Иванов Иван";
        var card = NewCard(response.Id, manager.Id);
        harness.Db.CrmCandidateCards.Add(card);
        await harness.Db.SaveChangesAsync();

        const string missingContractReason = "Кандидат пришлёт контракт после получения оригинала.";
        var chatBytes = new byte[] { 1, 2, 3, 4 };
        var uploads = new[]
        {
            SuccessUpload(CrmSuccessDocumentCategories.Correspondence, "chat.png", chatBytes),
            SuccessUpload(CrmSuccessDocumentCategories.Ticket, "ticket.pdf", "%PDF-test"u8.ToArray(), "application/pdf"),
            SuccessUpload(CrmSuccessDocumentCategories.TicketReceipt, "receipt.jpg", [5, 6, 7]),
            SuccessUpload(CrmSuccessDocumentCategories.CandidateDocument, "passport.jpeg", [8, 9, 10])
        };
        var close = await harness.Sut.CloseSuccessAsync(
            card.Id,
            "Кандидат подписался",
            missingContractReason,
            uploads,
            manager.Id,
            isAdmin: false);
        Assert.True(close.Ok, close.Error);

        var result = await harness.Sut.OpenSuccessReportArchiveAsync(
            card.Id,
            senior.Id,
            isElevated: true);
        Assert.NotNull(result.Stream);
        Assert.Contains("Иванов Иван", result.FileName, StringComparison.Ordinal);

        await using var archiveStream = result.Stream!;
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read);
        Assert.Equal(uploads.Length + 1, archive.Entries.Count);
        Assert.Contains(archive.Entries, entry => entry.FullName == "Отчёт.txt");
        Assert.Contains(archive.Entries, entry => entry.FullName == "01 Переписка/chat.png");
        Assert.Contains(archive.Entries, entry => entry.FullName == "02 Билеты/ticket.pdf");
        Assert.Contains(archive.Entries, entry => entry.FullName == "03 Чеки на билеты/receipt.jpg");
        Assert.Contains(archive.Entries, entry => entry.FullName == "06 Документы и прочие файлы/Документы кандидата/passport.jpeg");

        await using (var chatStream = archive.GetEntry("01 Переписка/chat.png")!.Open())
        {
            using var copied = new MemoryStream();
            await chatStream.CopyToAsync(copied);
            Assert.Equal(chatBytes, copied.ToArray());
        }

        using var manifestReader = new StreamReader(archive.GetEntry("Отчёт.txt")!.Open());
        var manifest = await manifestReader.ReadToEndAsync();
        Assert.Contains("Иванов Иван", manifest, StringComparison.Ordinal);
        Assert.Contains(missingContractReason, manifest, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Close_CancelsPlannedOutboundChatMessages()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var manager = await harness.CreateManagerAsync("close-chat@test.local", capacity: 5, onShift: true);
        var response = await SeedResponseAsync(harness.Db, "src-close-chat");
        var card = NewCard(response.Id, manager.Id);
        harness.Db.CrmCandidateCards.Add(card);
        await harness.Db.SaveChangesAsync();
        Assert.True((await harness.Sut.QueueChatMessageAsync(card.Id, "Не отправлять", manager.Id, isAdmin: false)).Ok);

        var (ok, error) = await harness.Sut.CloseAsync(
            card.Id,
            CrmCloseReasons.NotRelevant,
            "Закрываем карточку",
            manager.Id,
            isAdmin: false);

        Assert.True(ok, error);
        var message = Assert.Single(harness.Db.CrmOutboundChatMessages);
        Assert.NotNull(message.CancelledAtUtc);
        Assert.Equal(CrmOutboundChatStatuses.Planned, message.Status);
    }

    [Fact]
    public async Task CreateManualCard_ElevatedUserCreatesResponseAndCard()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var seniorManager = await harness.CreateDeskUserAsync(
            "manual@test.local",
            capacity: 5,
            onShift: true,
            PanelRoles.SeniorManager);

        var (cardId, error) = await harness.Sut.CreateManualCardAsync(
            OfficeId,
            new CrmManualCardCreateRequest("Сидоров Сидор", "89001234567", "Уфа", "Водитель", 28, "Битрикс", null, null, AssignToMe: true, Citizenship: "Россия"),
            seniorManager.Id,
            isAdmin: true);
        Assert.True(cardId is not null, error);
        var card = await harness.Db.CrmCandidateCards.Include(x => x.Response).SingleAsync(x => x.Id == cardId);
        Assert.Equal(seniorManager.Id, card.ManagerUserId);
        Assert.Equal(seniorManager.Id, card.InitialManagerUserId);
        Assert.NotNull(card.InitialAssignedAtUtc);
        Assert.Equal("Сидоров Сидор", card.Response.FullName);
        Assert.Equal("79001234567", card.Response.PhoneNormalized);
        Assert.Equal("Manual", card.Response.Source);
        Assert.Equal("Россия", card.Response.Citizenship);
        Assert.Contains(harness.Db.CandidateContactPhones, p => p.PersonId == card.Response.PersonId && p.IsPrimary);
    }

    [Fact]
    public async Task CreateManualCard_ManagerCreatesCardAssignedToSelf()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var manager = await harness.CreateManagerAsync("manual-manager@test.local", capacity: 5, onShift: true);

        var (cardId, error) = await harness.Sut.CreateManualCardAsync(
            OfficeId,
            new CrmManualCardCreateRequest(
                "Сидоров Сидор",
                "89001234567",
                AssignToMe: false),
            manager.Id,
            isAdmin: false);

        Assert.NotNull(cardId);
        Assert.Null(error);
        var card = await harness.Db.CrmCandidateCards
            .Include(x => x.Response)
            .SingleAsync(x => x.Id == cardId);
        Assert.Equal(manager.Id, card.ManagerUserId);
        Assert.Equal(manager.Id, card.InitialManagerUserId);
        Assert.NotNull(card.InitialAssignedAtUtc);
        Assert.Equal("Сидоров Сидор", card.Response.FullName);
        Assert.Contains(
            harness.Db.CrmCandidateHistory,
            x => x.CardId == cardId && x.Action == "Assigned" && x.ActorUserId == manager.Id);
    }

    [Fact]
    public async Task ImportLeadFile_SameFullNameWithDifferentPhones_CreatesOneCardWithBothPhones()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var manager = await harness.CreateManagerAsync("file-import@test.local", capacity: 300, onShift: true);
        var request = new CrmLeadFileImportRequest(
            "3(50).txt",
            [
                new CrmLeadFileImportEntry(
                    "Пигунов Владимир Викторович",
                    "+7 923 631-86-92",
                    "Слесарь",
                    61),
                new CrmLeadFileImportEntry(
                    "  пигунов   владимир викторович  ",
                    "+7 923 062-74-09",
                    "Слесарь вахта",
                    91)
            ]);

        var (result, error) = await harness.Sut.ImportLeadFileAsync(
            OfficeId,
            request,
            manager.Id);

        Assert.Null(error);
        Assert.NotNull(result);
        Assert.Equal(1, result.RecognizedCount);
        Assert.Equal(1, result.CreatedCount);
        Assert.Equal(1, result.AssignedCount);

        var card = await harness.Db.CrmCandidateCards
            .Include(x => x.Response)
            .SingleAsync();
        Assert.Equal(manager.Id, card.ManagerUserId);
        Assert.Equal("Пигунов Владимир Викторович", card.Response.FullName);
        Assert.Equal("79236318692", card.Response.PhoneNormalized);

        var phones = await harness.Db.CandidateContactPhones
            .Where(x => x.PersonId == card.Response.PersonId)
            .OrderByDescending(x => x.IsPrimary)
            .ThenBy(x => x.PhoneNormalized)
            .ToListAsync();
        Assert.Equal(2, phones.Count);
        Assert.Single(phones, x => x.IsPrimary);
        Assert.Equal(
            ["79230627409", "79236318692"],
            phones.Select(x => x.PhoneNormalized).OrderBy(x => x).ToArray());
        Assert.Equal(
            2,
            await harness.Db.CandidatePhoneHistory.CountAsync(x => x.PersonId == card.Response.PersonId));
    }

    [Fact]
    public async Task ImportLeadFile_ExistingAdditionalPhone_DoesNotCreateDuplicateCard()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var manager = await harness.CreateManagerAsync("file-repeat@test.local", capacity: 300, onShift: true);
        var initialRequest = new CrmLeadFileImportRequest(
            "initial.txt",
            [
                new CrmLeadFileImportEntry("Ершов Алексей Алексеевич", "+7 924 307-43-93", "Охранник", 1),
                new CrmLeadFileImportEntry("Ершов Алексей Алексеевич", "+7 969 403-68-27", "Охранник", 2)
            ]);
        var (initialResult, initialError) = await harness.Sut.ImportLeadFileAsync(
            OfficeId,
            initialRequest,
            manager.Id);
        Assert.Null(initialError);
        Assert.Equal(1, initialResult!.CreatedCount);

        var repeatedRequest = new CrmLeadFileImportRequest(
            "repeat.txt",
            [new CrmLeadFileImportEntry("Ершов Алексей Алексеевич", "+7 969 403-68-27", "Охранник", 1)]);
        var (repeatResult, repeatError) = await harness.Sut.ImportLeadFileAsync(
            OfficeId,
            repeatedRequest,
            manager.Id);

        Assert.Null(repeatError);
        Assert.NotNull(repeatResult);
        Assert.Equal(0, repeatResult.CreatedCount);
        Assert.Equal(1, repeatResult.SkippedExistingCount);
        Assert.Single(harness.Db.CrmCandidateCards);
        Assert.Equal(2, harness.Db.CandidateContactPhones.Count());
    }

    [Fact]
    public async Task AddContactPhone_AndSetPrimary_NotifiesManagerWhenChangedByAdmin()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var manager = await harness.CreateManagerAsync("phone-mgr@test.local", capacity: 5, onShift: true);
        var lead = await harness.CreateDeskUserAsync("phone-lead@test.local", capacity: 5, onShift: true, PanelRoles.OfficeLead);
        var response = await SeedResponseAsync(harness.Db);
        var card = NewCard(response.Id, manager.Id);
        harness.Db.CrmCandidateCards.Add(card);
        await harness.Db.SaveChangesAsync();

        var (phone, addError) = await harness.Sut.AddContactPhoneAsync(
            card.Id,
            new CrmContactPhoneCreateRequest("89991112233", "Доп", SetAsPrimary: false),
            lead.Id,
            isAdmin: true);
        Assert.True(phone is not null, addError);
        Assert.True(string.IsNullOrEmpty(addError));

        var (ok, setError) = await harness.Sut.SetPrimaryContactPhoneAsync(card.Id, phone!.Id, lead.Id, isAdmin: true);
        Assert.True(ok, setError);
        await harness.Db.Entry(response).ReloadAsync();
        Assert.Equal("79991112233", response.PhoneNormalized);
        Assert.Contains(
            harness.Db.CrmDeskAlerts,
            a => a.RecipientUserId == manager.Id && a.Kind == CrmTaskNotificationKinds.PhoneChanged);
    }

    [Fact]
    public async Task UpdateCard_UpdatesResponseFieldsAndHistory()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var manager = await harness.CreateManagerAsync("edit@test.local", capacity: 5, onShift: true);
        var outsider = await harness.CreateManagerAsync("outsider-edit@test.local", capacity: 5, onShift: true);
        var response = await SeedResponseAsync(harness.Db);
        var card = NewCard(response.Id, manager.Id);
        harness.Db.CrmCandidateCards.Add(card);
        await harness.Db.SaveChangesAsync();

        var denied = await harness.Sut.UpdateCardAsync(
            card.Id,
            new CrmCardUpdateRequest("Петров Пётр", "79991112233", "Казань", "Токарь", 30),
            outsider.Id,
            isAdmin: false);
        Assert.False(denied.Ok);

        var (ok, error) = await harness.Sut.UpdateCardAsync(
            card.Id,
            new CrmCardUpdateRequest(
                "Петров Пётр",
                "8 (999) 111-22-33",
                "Казань",
                "Токарь",
                30,
                "manual-42",
                "Авито HQ",
                "https://example.com/src",
                "https://example.com/vac",
                "https://t.me/test",
                "Республика Беларусь"),
            manager.Id,
            isAdmin: false);
        Assert.True(ok, error);

        await harness.Db.Entry(response).ReloadAsync();
        Assert.Equal("Петров Пётр", response.FullName);
        Assert.Equal("79991112233", response.PhoneNormalized);
        Assert.Equal("Казань", response.City);
        Assert.Equal("Токарь", response.Vacancy);
        Assert.Equal(30, response.Age);
        Assert.Equal("Республика Беларусь", response.Citizenship);
        Assert.Equal("manual-42", response.SourceResponseId);
        Assert.Equal("Авито HQ", response.AccountName);
        Assert.Contains(
            harness.Db.CrmCandidateHistory,
            h => h.CardId == card.Id && h.Action == "CardUpdated" && h.Details!.Contains("ФИО"));
        Assert.True(ResponseOperatorLocks.Contains(response.OperatorLockedFields, ResponseOperatorLocks.City));
        Assert.True(ResponseOperatorLocks.Contains(response.OperatorLockedFields, ResponseOperatorLocks.Vacancy));
        Assert.True(ResponseOperatorLocks.Contains(response.OperatorLockedFields, ResponseOperatorLocks.Age));
        Assert.True(ResponseOperatorLocks.Contains(response.OperatorLockedFields, ResponseOperatorLocks.Citizenship));
        Assert.True(ResponseOperatorLocks.Contains(response.OperatorLockedFields, ResponseOperatorLocks.VacancyUrl));
    }

    [Fact]
    public async Task SetOfficeFunnel_SavesCustomStagesAndMovesOrphans()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var manager = await harness.CreateManagerAsync("funnel@test.local", capacity: 5, onShift: true);
        var response = await SeedResponseAsync(harness.Db);
        var card = NewCard(response.Id, manager.Id);
        card.Stage = CrmStages.Ticket;
        harness.Db.CrmCandidateCards.Add(card);
        await harness.Db.SaveChangesAsync();

        var (ok, error) = await harness.Sut.SetOfficeFunnelAsync(
            OfficeId,
            ["Новый", "В работе", "Готово"],
            manager.Id);
        Assert.True(ok, error);

        var office = await harness.Db.Offices.SingleAsync(x => x.Id == OfficeId);
        Assert.Equal(["Новый", "В работе", "Готово"], CrmStages.Resolve(office.CrmStagesJson));
        Assert.Equal("Новый", card.Stage);

        var board = await harness.Sut.GetBoardAsync(OfficeId, manager.Id, isAdmin: true);
        Assert.NotNull(board);
        Assert.Equal(["Новый", "В работе", "Готово"], board.FunnelStages);
        Assert.Equal(["Новый", "В работе", "Готово"], board.Stages.Select(s => s.Name).ToList());
    }

    [Fact]
    public async Task SetOfficeSettings_EnablesDeadlineNotificationsAndDismissesThemOnDisable()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var manager = await harness.CreateManagerAsync("deadline@test.local", capacity: 5, onShift: true);

        Assert.True(await harness.Sut.SetOfficeSettingsAsync(
            OfficeId,
            enabled: true,
            requireStageComment: false,
            deadlineNotificationsEnabled: true));
        var office = await harness.Db.Offices.SingleAsync(x => x.Id == OfficeId);
        Assert.True(office.CrmDeadlineNotificationsEnabled);
        Assert.NotNull(office.CrmDeadlineNotificationsEnabledAtUtc);

        var task = await harness.Sut.CreateTaskAsync(
            OfficeId,
            new CrmTaskCreateRequest(null, "Проверить документы", null, manager.Id, DateTime.UtcNow.AddHours(2)),
            manager.Id,
            isAdmin: false);
        Assert.NotNull(task);
        var taskEntity = await harness.Db.CrmTasks.SingleAsync(x => x.Id == task.Id);
        var notification = new CrmTaskNotificationEntity
        {
            Id = Guid.NewGuid(),
            OfficeId = OfficeId,
            TaskId = task.Id,
            ReminderVersion = taskEntity.ReminderVersion,
            RecipientUserId = manager.Id,
            Kind = CrmTaskNotificationKinds.DueIn24Hours,
            DueAtUtc = taskEntity.DueAtUtc!.Value,
            CreatedAtUtc = DateTime.UtcNow
        };
        harness.Db.CrmTaskNotifications.Add(notification);
        await harness.Db.SaveChangesAsync();

        Assert.True(await harness.Sut.SetOfficeSettingsAsync(
            OfficeId,
            enabled: true,
            requireStageComment: false,
            deadlineNotificationsEnabled: false));
        Assert.False(office.CrmDeadlineNotificationsEnabled);
        Assert.Null(office.CrmDeadlineNotificationsEnabledAtUtc);
        Assert.NotNull(notification.DismissedAtUtc);
    }

    [Fact]
    public async Task CreateCard_UsesFirstOfficeStage()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var office = await harness.Db.Offices.SingleAsync(x => x.Id == OfficeId);
        office.CrmStagesJson = CrmStages.Serialize(["Старт", "Финиш"]);
        await harness.Db.SaveChangesAsync();
        var response = await SeedResponseAsync(harness.Db);

        await harness.Sut.CreateCardForResponseAsync(response);

        var card = Assert.Single(harness.Db.CrmCandidateCards);
        Assert.Equal("Старт", card.Stage);
    }

    [Fact]
    public async Task Move_RejectsStageOutsideOfficeFunnel()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var manager = await harness.CreateManagerAsync("move@test.local", capacity: 5, onShift: true);
        var office = await harness.Db.Offices.SingleAsync(x => x.Id == OfficeId);
        office.CrmStagesJson = CrmStages.Serialize(["А", "Б"]);
        var response = await SeedResponseAsync(harness.Db);
        var card = NewCard(response.Id, manager.Id);
        card.Stage = "А";
        harness.Db.CrmCandidateCards.Add(card);
        await harness.Db.SaveChangesAsync();

        var (ok, error) = await harness.Sut.MoveAsync(card.Id, CrmStages.Ticket, null, manager.Id, isAdmin: false);
        Assert.False(ok);
        Assert.Equal("Неизвестный этап.", error);

        (ok, error) = await harness.Sut.MoveAsync(card.Id, "Б", null, manager.Id, isAdmin: false);
        Assert.False(ok);
        Assert.Contains("комментарий", error, StringComparison.OrdinalIgnoreCase);

        (ok, error) = await harness.Sut.MoveAsync(card.Id, "Б", "Кандидат готов продолжить", manager.Id, isAdmin: false);
        Assert.True(ok, error);
        Assert.Equal("Б", card.Stage);
        Assert.DoesNotContain(harness.Db.CrmCandidateNotes, note => note.CardId == card.Id);
        var detail = await harness.Sut.GetCardAsync(card.Id, manager.Id, isAdmin: false);
        var stageActivity = Assert.Single(detail!.Activity, item => item.Title == "Смена этапа");
        Assert.Equal("А → Б", stageActivity.Body);
        Assert.Equal("Кандидат готов продолжить", stageActivity.ActionComment);
    }

    [Fact]
    public async Task SetOfficeFunnel_RejectsEmptyOrInvalid()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);

        var (ok, error) = await harness.Sut.SetOfficeFunnelAsync(OfficeId, [], "admin");
        Assert.False(ok);
        Assert.Contains("этап", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ManagerDefaultPermissions_ExcludeTeamAndAnalytics()
    {
        var permissions = PanelPermissions.DefaultForRole(PanelRoles.Manager);
        Assert.Contains(PanelPermissions.CrmBoard, permissions);
        Assert.Contains(PanelPermissions.CrmTasks, permissions);
        Assert.DoesNotContain(PanelPermissions.CrmTeam, permissions);
        Assert.DoesNotContain(PanelPermissions.CrmAnalytics, permissions);

        var senior = PanelPermissions.DefaultForRole(PanelRoles.SeniorManager);
        Assert.Contains(PanelPermissions.CrmTeam, senior);
        Assert.Contains(PanelPermissions.CrmAnalytics, senior);
    }

    [Fact]
    public async Task GetBoard_ManagerTeamScopeForcedToMine_AndOnlyOwnCards()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var manager = await harness.CreateManagerAsync("view@test.local", capacity: 5, onShift: true);
        var other = await harness.CreateManagerAsync("other-view@test.local", capacity: 5, onShift: true);
        var ownResponse = await SeedResponseAsync(harness.Db, "own-src");
        var foreignResponse = await SeedResponseAsync(harness.Db, "foreign-src");
        var ownCard = NewCard(ownResponse.Id, manager.Id);
        ownCard.Stage = CrmStages.Lead;
        var foreignCard = NewCard(foreignResponse.Id, other.Id);
        foreignCard.Stage = CrmStages.Lead;
        harness.Db.CrmCandidateCards.AddRange(ownCard, foreignCard);
        await harness.Db.SaveChangesAsync();

        // Manager requesting Team is forced to Mine — only own assigned cards.
        var team = await harness.Sut.GetBoardAsync(
            OfficeId,
            manager.Id,
            isAdmin: false,
            new CrmBoardQuery(Scope: CrmBoardScopes.Team, ManagerUserId: other.Id));
        Assert.NotNull(team);
        Assert.Equal(CrmBoardScopes.Mine, team.Scope);
        Assert.Null(team.ManagerUserId);
        Assert.True(team.CanEdit);
        var visibleIds = team.Stages.SelectMany(s => s.Cards).Select(c => c.Id).ToHashSet();
        Assert.Contains(ownCard.Id, visibleIds);
        Assert.DoesNotContain(foreignCard.Id, visibleIds);

        var mine = await harness.Sut.GetBoardAsync(
            OfficeId,
            manager.Id,
            isAdmin: false,
            new CrmBoardQuery(Scope: CrmBoardScopes.Mine));
        Assert.NotNull(mine);
        Assert.Equal(CrmBoardScopes.Mine, mine.Scope);
        Assert.True(mine.CanEdit);
    }

    [Fact]
    public async Task GetBoard_BoardView_DoesNotDropCardsBeyondLegacy500Limit()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var manager = await harness.CreateManagerAsync("large-board@test.local", capacity: 600, onShift: true);
        var start = DateTime.UtcNow.AddDays(-30);
        CrmCandidateCardEntity? oldestQuestionnaireCard = null;

        for (var index = 0; index < 501; index++)
        {
            var person = TestCandidatePersonFactory.CreatePerson(
                OfficeId,
                fullName: $"Кандидат {index}",
                firstName: "Кандидат",
                lastName: index.ToString());
            var response = TestCandidatePersonFactory.CreateResponse(
                OfficeId,
                person.Id,
                WorkerId,
                phone: $"79{index:000000000}",
                sourceResponseId: $"large-board-{index}",
                fullName: $"Кандидат {index}");
            var card = NewCard(response.Id, manager.Id);
            card.Stage = index == 0 ? CrmStages.Questionnaire : CrmStages.Lead;
            card.CreatedAtUtc = start.AddMinutes(index);
            card.UpdatedAtUtc = card.CreatedAtUtc;
            card.StageChangedAtUtc = card.CreatedAtUtc;

            harness.Db.CandidatePersons.Add(person);
            harness.Db.CandidateResponses.Add(response);
            harness.Db.CrmCandidateCards.Add(card);

            if (index == 0)
            {
                oldestQuestionnaireCard = card;
            }
        }

        await harness.Db.SaveChangesAsync();

        var board = await harness.Sut.GetBoardAsync(
            OfficeId,
            manager.Id,
            isAdmin: false,
            new CrmBoardQuery(Scope: CrmBoardScopes.Mine, View: CrmBoardViews.Board));

        Assert.NotNull(board);
        Assert.Equal(501, board.TotalItems);
        Assert.Equal(501, board.Stages.Sum(stage => stage.Cards.Count));
        Assert.Contains(
            board.Stages.SelectMany(stage => stage.Cards),
            card => card.Id == oldestQuestionnaireCard!.Id);
    }

    [Fact]
    public async Task GetBoard_ElevatedTeamManagerFilter_ShowsOnlySelectedManagersCards()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var first = await harness.CreateManagerAsync("first-filter@test.local", capacity: 5, onShift: true);
        var second = await harness.CreateManagerAsync("second-filter@test.local", capacity: 5, onShift: true);
        var firstResponse = await SeedResponseAsync(harness.Db, "first-filter-src");
        var secondResponse = await SeedResponseAsync(harness.Db, "second-filter-src");
        var firstCard = NewCard(firstResponse.Id, first.Id);
        var secondCard = NewCard(secondResponse.Id, second.Id);
        harness.Db.CrmCandidateCards.AddRange(firstCard, secondCard);
        await harness.Db.SaveChangesAsync();

        var board = await harness.Sut.GetBoardAsync(
            OfficeId,
            first.Id,
            isAdmin: true,
            new CrmBoardQuery(Scope: CrmBoardScopes.Team, ManagerUserId: second.Id));

        Assert.NotNull(board);
        Assert.Equal(CrmBoardScopes.Team, board.Scope);
        Assert.Equal(second.Id, board.ManagerUserId);
        var visibleIds = board.Stages.SelectMany(s => s.Cards).Select(c => c.Id).ToHashSet();
        Assert.DoesNotContain(firstCard.Id, visibleIds);
        Assert.Contains(secondCard.Id, visibleIds);
    }

    [Fact]
    public async Task GetBoard_TodayAssignmentStats_SeparateInitialAndRedistributedCards()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var manager = await harness.CreateManagerAsync("today-stats@test.local", capacity: 10, onShift: true);
        var now = DateTime.UtcNow;
        var firstAssignedAt = now.Date;
        var sameDayAssignedAt = now.Date.AddTicks(1);
        var oldAssignedAt = now.AddDays(-2);

        var firstResponse = await SeedResponseAsync(harness.Db, "today-first");
        var sameDayResponse = await SeedResponseAsync(harness.Db, "today-reassigned");
        var ndzResponse = await SeedResponseAsync(harness.Db, "today-ndz");
        var firstCard = NewCard(firstResponse.Id, manager.Id);
        firstCard.InitialAssignedAtUtc = firstAssignedAt;
        var sameDayCard = NewCard(sameDayResponse.Id, manager.Id);
        sameDayCard.InitialAssignedAtUtc = sameDayAssignedAt;
        var ndzCard = NewCard(ndzResponse.Id, manager.Id);
        ndzCard.InitialAssignedAtUtc = oldAssignedAt;
        harness.Db.CrmCandidateCards.AddRange(firstCard, sameDayCard, ndzCard);
        harness.Db.CrmCandidateHistory.AddRange(
            NewAssignmentHistory(firstCard.Id, firstAssignedAt, CrmLeadDistributionService.ReasonAuto),
            NewAssignmentHistory(sameDayCard.Id, sameDayAssignedAt, CrmLeadDistributionService.ReasonAuto),
            NewAssignmentHistory(sameDayCard.Id, now.AddMinutes(-10), CrmLeadDistributionService.ReasonManual),
            NewAssignmentHistory(ndzCard.Id, now.AddMinutes(-5), CrmLeadDistributionService.ReasonDailyNdz));
        await harness.Db.SaveChangesAsync();

        var board = await harness.Sut.GetBoardAsync(
            OfficeId,
            manager.Id,
            isAdmin: true,
            new CrmBoardQuery(
                Scope: CrmBoardScopes.Team,
                TimeZoneOffsetMinutes: 0));

        Assert.NotNull(board);
        Assert.Equal(2, board.TeamStats.AssignedToday);
        Assert.Equal(2, board.TeamStats.RedistributedToday);
        Assert.Equal(1, board.TeamStats.RedistributedNdzToday);
    }

    [Fact]
    public async Task GetBoard_ListView_PaginatesCurrentPageAndKeepsDateOrder()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var manager = await harness.CreateManagerAsync("list-page@test.local", capacity: 50, onShift: true);
        var start = DateTime.UtcNow.AddDays(-2);
        var expectedByCreated = new List<CrmCandidateCardEntity>();

        for (var index = 0; index < 25; index++)
        {
            var response = await SeedResponseAsync(harness.Db, $"list-page-{index}");
            var card = NewCard(response.Id, manager.Id);
            card.CreatedAtUtc = start.AddMinutes(index);
            card.UpdatedAtUtc = card.CreatedAtUtc;
            card.StageChangedAtUtc = card.CreatedAtUtc;
            expectedByCreated.Add(card);
            harness.Db.CrmCandidateCards.Add(card);
        }

        await harness.Db.SaveChangesAsync();

        var board = await harness.Sut.GetBoardAsync(
            OfficeId,
            manager.Id,
            isAdmin: false,
            new CrmBoardQuery(
                Scope: CrmBoardScopes.Mine,
                View: CrmBoardViews.List,
                Page: 2,
                PageSize: 20,
                Sort: CrmBoardSorts.Created,
                SortDir: "desc"));

        Assert.NotNull(board);
        Assert.Equal(CrmBoardViews.List, board.View);
        Assert.Equal(2, board.Page);
        Assert.Equal(20, board.PageSize);
        Assert.Equal(25, board.TotalItems);
        Assert.NotNull(board.ListCards);
        Assert.Equal(
            expectedByCreated.OrderByDescending(card => card.CreatedAtUtc).Skip(20).Select(card => card.Id),
            board.ListCards.Select(card => card.Id));
    }

    [Fact]
    public async Task GetBoard_ListView_FiltersByStageAndCreatedPeriodBeforePaging()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var manager = await harness.CreateManagerAsync("list-filter@test.local", capacity: 10, onShift: true);
        var utcStart = new DateTime(2026, 8, 10, 19, 0, 0, DateTimeKind.Utc);
        var utcEnd = utcStart.AddDays(1);

        var matchingResponse = await SeedResponseAsync(harness.Db, "list-filter-match");
        var matchingCard = NewCard(matchingResponse.Id, manager.Id);
        matchingCard.Stage = CrmStages.Lead;
        matchingCard.CreatedAtUtc = utcStart.AddHours(2);

        var wrongStageResponse = await SeedResponseAsync(harness.Db, "list-filter-stage");
        var wrongStageCard = NewCard(wrongStageResponse.Id, manager.Id);
        wrongStageCard.Stage = CrmStages.Ndz73;
        wrongStageCard.CreatedAtUtc = utcStart.AddHours(3);

        var wrongDateResponse = await SeedResponseAsync(harness.Db, "list-filter-date");
        var wrongDateCard = NewCard(wrongDateResponse.Id, manager.Id);
        wrongDateCard.Stage = CrmStages.Lead;
        wrongDateCard.CreatedAtUtc = utcEnd.AddMinutes(1);

        harness.Db.CrmCandidateCards.AddRange(matchingCard, wrongStageCard, wrongDateCard);
        await harness.Db.SaveChangesAsync();

        var board = await harness.Sut.GetBoardAsync(
            OfficeId,
            manager.Id,
            isAdmin: true,
            new CrmBoardQuery(
                Scope: CrmBoardScopes.Team,
                View: CrmBoardViews.List,
                Stage: CrmStages.Lead,
                CreatedFromUtc: utcStart,
                CreatedToUtc: utcEnd,
                CreatedFrom: "2026-08-11",
                CreatedTo: "2026-08-11"));

        Assert.NotNull(board);
        Assert.Equal(1, board.TotalItems);
        Assert.Equal(matchingCard.Id, Assert.Single(board.ListCards!).Id);
        Assert.Equal(CrmStages.Lead, board.Stage);
        Assert.Equal("2026-08-11", board.CreatedFrom);
        Assert.Equal("2026-08-11", board.CreatedTo);
    }

    [Fact]
    public async Task GetBoard_CityAndVacancyFilters_AreCaseInsensitive()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var manager = await harness.CreateManagerAsync("case-filter@test.local", capacity: 5, onShift: true);
        var matchingResponse = await SeedResponseAsync(harness.Db, "case-filter-match");
        matchingResponse.City = "Подольск";
        matchingResponse.Vacancy = "Сварщик вахта";
        var otherResponse = await SeedResponseAsync(harness.Db, "case-filter-other");
        otherResponse.City = "Москва";
        otherResponse.Vacancy = "Курьер";
        var matchingCard = NewCard(matchingResponse.Id, manager.Id);
        var otherCard = NewCard(otherResponse.Id, manager.Id);
        harness.Db.CrmCandidateCards.AddRange(matchingCard, otherCard);
        await harness.Db.SaveChangesAsync();

        var board = await harness.Sut.GetBoardAsync(
            OfficeId,
            manager.Id,
            isAdmin: false,
            new CrmBoardQuery(
                Scope: CrmBoardScopes.Mine,
                City: "подольск",
                Vacancy: "СВАРЩИК"));

        Assert.NotNull(board);
        var visibleIds = board.Stages.SelectMany(x => x.Cards).Select(x => x.Id).ToList();
        Assert.Equal([matchingCard.Id], visibleIds);
    }

    [Fact]
    public async Task GetBoard_ElevatedClosedFiltersByManagerAndCloseReason()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var first = await harness.CreateManagerAsync("closed-first@test.local", capacity: 5, onShift: true);
        var second = await harness.CreateManagerAsync("closed-second@test.local", capacity: 5, onShift: true);
        var firstResponse = await SeedResponseAsync(harness.Db, "closed-first");
        var secondResponse = await SeedResponseAsync(harness.Db, "closed-second");
        var wrongReasonResponse = await SeedResponseAsync(harness.Db, "closed-wrong-reason");
        var firstCard = NewCard(firstResponse.Id, first.Id);
        firstCard.IsClosed = true;
        firstCard.CloseReason = CrmCloseReasons.Success;
        var secondCard = NewCard(secondResponse.Id, second.Id);
        secondCard.IsClosed = true;
        secondCard.CloseReason = CrmCloseReasons.Success;
        var wrongReasonCard = NewCard(wrongReasonResponse.Id, second.Id);
        wrongReasonCard.IsClosed = true;
        wrongReasonCard.CloseReason = CrmCloseReasons.NotRelevant;
        harness.Db.CrmCandidateCards.AddRange(firstCard, secondCard, wrongReasonCard);
        await harness.Db.SaveChangesAsync();

        var board = await harness.Sut.GetBoardAsync(
            OfficeId,
            first.Id,
            isAdmin: true,
            new CrmBoardQuery(
                Scope: CrmBoardScopes.Closed,
                ManagerUserId: second.Id,
                CloseReason: CrmCloseReasons.Success));

        Assert.NotNull(board);
        Assert.Equal(second.Id, board.ManagerUserId);
        Assert.Equal(CrmCloseReasons.Success, board.CloseReason);
        var card = Assert.Single(board.Stages.SelectMany(x => x.Cards));
        Assert.Equal(secondCard.Id, card.Id);
    }

    [Fact]
    public async Task GetCard_ManagerCannotOpenForeignCard_OwnerCanEdit()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var owner = await harness.CreateManagerAsync("owner@test.local", capacity: 5, onShift: true);
        var viewer = await harness.CreateManagerAsync("viewer@test.local", capacity: 5, onShift: true);
        var response = await SeedResponseAsync(harness.Db);
        var card = NewCard(response.Id, owner.Id);
        harness.Db.CrmCandidateCards.Add(card);
        await harness.Db.SaveChangesAsync();

        var detail = await harness.Sut.GetCardAsync(card.Id, viewer.Id, isAdmin: false);
        Assert.Null(detail);

        var own = await harness.Sut.GetCardAsync(card.Id, owner.Id, isAdmin: false);
        Assert.NotNull(own);
        Assert.True(own.CanEdit);
    }

    [Fact]
    public async Task GetCard_ElevatedCanOpenForeignCard()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var owner = await harness.CreateManagerAsync("owner-elev@test.local", capacity: 5, onShift: true);
        var lead = await harness.CreateDeskUserAsync(
            "senior@test.local",
            capacity: 5,
            onShift: true,
            PanelRoles.SeniorManager);
        var response = await SeedResponseAsync(harness.Db);
        var card = NewCard(response.Id, owner.Id);
        harness.Db.CrmCandidateCards.Add(card);
        await harness.Db.SaveChangesAsync();

        var detail = await harness.Sut.GetCardAsync(card.Id, lead.Id, isAdmin: true);
        Assert.NotNull(detail);
        Assert.True(detail.CanEdit);
        Assert.Equal(owner.Id, detail.Card.ManagerUserId);
    }

    [Fact]
    public async Task GetCard_ElevatedFromOtherOffice_IsDenied()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var otherOfficeId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        harness.Db.Offices.Add(new OfficeEntity
        {
            Id = otherOfficeId,
            Name = "Other Office",
            RegistrationSecretHash = "hash2",
            CreatedAtUtc = DateTime.UtcNow,
            IsEnabled = true,
            CrmEnabled = true
        });
        await harness.Db.SaveChangesAsync();

        var owner = await harness.CreateManagerAsync("owner-office-a@test.local", capacity: 5, onShift: true);
        var foreignSenior = await harness.CreateDeskUserAsync(
            "senior-office-b@test.local",
            capacity: 5,
            onShift: true,
            PanelRoles.SeniorManager,
            officeId: otherOfficeId);
        var response = await SeedResponseAsync(harness.Db);
        var card = NewCard(response.Id, owner.Id);
        harness.Db.CrmCandidateCards.Add(card);
        await harness.Db.SaveChangesAsync();

        Assert.Null(await harness.Sut.GetCardAsync(card.Id, foreignSenior.Id, isAdmin: true));
        Assert.False((await harness.Sut.UpdateCardAsync(
            card.Id,
            new CrmCardUpdateRequest("X", "79991112233", "City", "Job", 30),
            foreignSenior.Id,
            isAdmin: true)).Ok);
        Assert.False(await harness.Sut.AssignAsync(card.Id, owner.Id, foreignSenior.Id, isAdmin: true));
    }

    [Fact]
    public async Task Assign_ToCurrentManager_DoesNotCreateDuplicateHistoryEntry()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var owner = await harness.CreateManagerAsync("same-owner@test.local", capacity: 5, onShift: true);
        var response = await SeedResponseAsync(harness.Db, "same-owner-card");
        var card = NewCard(response.Id, owner.Id);
        var updatedAt = card.UpdatedAtUtc;
        harness.Db.CrmCandidateCards.Add(card);
        await harness.Db.SaveChangesAsync();

        Assert.True(await harness.Sut.AssignAsync(card.Id, owner.Id, owner.Id, isAdmin: true));

        Assert.Empty(await harness.Db.CrmCandidateHistory.Where(x => x.CardId == card.Id).ToListAsync());
        Assert.Equal(updatedAt, card.UpdatedAtUtc);
    }

    [Fact]
    public async Task Assign_ToAnotherManager_PreservesInitialManagerAttribution()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var firstManager = await harness.CreateManagerAsync("first-owner@test.local", capacity: 5, onShift: true);
        var secondManager = await harness.CreateManagerAsync("second-owner@test.local", capacity: 5, onShift: true);
        var response = await SeedResponseAsync(harness.Db, "reassigned-card");
        var initialAssignedAtUtc = DateTime.UtcNow.AddMinutes(-10);
        var card = NewCard(response.Id, firstManager.Id);
        card.InitialManagerUserId = firstManager.Id;
        card.InitialAssignedAtUtc = initialAssignedAtUtc;
        harness.Db.CrmCandidateCards.Add(card);
        await harness.Db.SaveChangesAsync();

        Assert.True(await harness.Sut.AssignAsync(
            card.Id,
            secondManager.Id,
            firstManager.Id,
            isAdmin: true));

        Assert.Equal(secondManager.Id, card.ManagerUserId);
        Assert.Equal(firstManager.Id, card.InitialManagerUserId);
        Assert.Equal(initialAssignedAtUtc, card.InitialAssignedAtUtc);
    }

    [Fact]
    public async Task GetCardAvatar_OwnerReceivesStoredAvatar_ForeignDenied()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var owner = await harness.CreateManagerAsync("avatar-owner@test.local", capacity: 5, onShift: true);
        var viewer = await harness.CreateManagerAsync("avatar-viewer@test.local", capacity: 5, onShift: true);
        var response = await SeedResponseAsync(harness.Db);
        response.AvatarImage = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
        response.AvatarContentType = "image/png";
        var card = NewCard(response.Id, owner.Id);
        harness.Db.CrmCandidateCards.Add(card);
        await harness.Db.SaveChangesAsync();

        Assert.Null(await harness.Sut.GetCardAvatarAsync(card.Id, viewer.Id, isAdmin: false));

        var avatar = await harness.Sut.GetCardAvatarAsync(card.Id, owner.Id, isAdmin: false);
        Assert.NotNull(avatar);
        Assert.Equal("image/png", avatar.ContentType);
        Assert.Equal(response.AvatarImage, avatar.Bytes);
    }

    [Fact]
    public async Task Tasks_ListShowsOnlyAssigneeToManager_WhileTaskAccessKeepsAuthor()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var creator = await harness.CreateManagerAsync("creator@test.local", capacity: 5, onShift: true);
        var assignee = await harness.CreateManagerAsync("assignee@test.local", capacity: 5, onShift: true);
        var outsider = await harness.CreateManagerAsync("outsider@test.local", capacity: 5, onShift: true);
        (await harness.Db.PanelUserProfiles.SingleAsync(x => x.UserId == creator.Id)).FullName = "Иван Петров";
        (await harness.Db.PanelUserProfiles.SingleAsync(x => x.UserId == assignee.Id)).FullName = "Мария Сидорова";
        await harness.Db.SaveChangesAsync();

        var task = await harness.Sut.CreateTaskAsync(
            OfficeId,
            new CrmTaskCreateRequest(
                null,
                "Позвонить кандидату",
                "Уточнить время",
                assignee.Id,
                DateTime.UtcNow.AddHours(1),
                CrmTaskImportances.High,
                CrmTaskTypes.CallBack),
            creator.Id,
            isAdmin: false);

        Assert.NotNull(task);
        Assert.Equal(CrmTaskImportances.High, task.Importance);
        Assert.Equal(CrmTaskTypes.CallBack, task.TaskType);
        Assert.Equal("Иван Петров", task.CreatorName);
        Assert.Equal("Мария Сидорова", task.AssigneeName);
        Assert.DoesNotContain((await harness.Sut.GetTasksAsync(OfficeId, creator.Id, isAdmin: false)).Select(x => x.Id), id => id == task.Id);
        Assert.Contains((await harness.Sut.GetTasksAsync(OfficeId, assignee.Id, isAdmin: false)).Select(x => x.Id), id => id == task.Id);
        Assert.DoesNotContain((await harness.Sut.GetTasksAsync(OfficeId, outsider.Id, isAdmin: false)).Select(x => x.Id), id => id == task.Id);
        Assert.Contains((await harness.Sut.GetTasksAsync(OfficeId, "admin", isAdmin: true)).Select(x => x.Id), id => id == task.Id);

        var comment = await harness.Sut.AddTaskCommentAsync(task.Id, "Созвон согласован", creator.Id, isAdmin: false);
        Assert.NotNull(comment);
        Assert.Equal(creator.Id, comment.AuthorUserId);
        Assert.Equal("Иван Петров", comment.AuthorName);

        var detail = await harness.Sut.GetTaskAsync(task.Id, assignee.Id, isAdmin: false);
        Assert.NotNull(detail);
        Assert.True(detail.CanComplete);
        Assert.Equal(CrmTaskImportances.High, detail.Task.Importance);
        Assert.Equal(CrmTaskTypes.CallBack, detail.Task.TaskType);
        Assert.Single(detail.Comments);
        Assert.Equal("Созвон согласован", detail.Comments[0].Text);

        var adminDetail = await harness.Sut.GetTaskAsync(task.Id, "admin", isAdmin: true);
        Assert.NotNull(adminDetail);
        Assert.True(adminDetail.CanComplete);

        Assert.Null(await harness.Sut.GetTaskAsync(task.Id, outsider.Id, isAdmin: false));
        Assert.Null(await harness.Sut.AddTaskCommentAsync(task.Id, "Нет доступа", outsider.Id, isAdmin: false));
        Assert.False((await harness.Sut.CompleteTaskAsync(task.Id, "готово", creator.Id, isAdmin: false)).Ok);
        Assert.False((await harness.Sut.CompleteTaskAsync(task.Id, "   ", assignee.Id, isAdmin: false)).Ok);
        Assert.True((await harness.Sut.CompleteTaskAsync(task.Id, "Созвон проведён, анкета отправлена", assignee.Id, isAdmin: false)).Ok);
    }

    [Fact]
    public async Task ElevatedTasks_FilterBySelectedAssigneeOrShowWholeOffice()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var first = await harness.CreateManagerAsync("first@test.local", capacity: 5, onShift: true);
        var second = await harness.CreateManagerAsync("second@test.local", capacity: 5, onShift: true);

        var firstTask = await harness.Sut.CreateTaskAsync(
            OfficeId,
            new CrmTaskCreateRequest(null, "Задача первого", null, first.Id, DateTime.UtcNow.AddHours(1)),
            first.Id,
            isAdmin: false);
        var secondTask = await harness.Sut.CreateTaskAsync(
            OfficeId,
            new CrmTaskCreateRequest(null, "Задача второго", null, second.Id, DateTime.UtcNow.AddHours(1)),
            second.Id,
            isAdmin: false);

        Assert.NotNull(firstTask);
        Assert.NotNull(secondTask);

        var allOfficeTasks = await harness.Sut.GetTasksAsync(
            OfficeId,
            "admin",
            isAdmin: true,
            managerUserId: null);
        var firstManagerTasks = await harness.Sut.GetTasksAsync(
            OfficeId,
            "admin",
            isAdmin: true,
            managerUserId: first.Id);

        Assert.Contains(allOfficeTasks, task => task.Id == firstTask.Id);
        Assert.Contains(allOfficeTasks, task => task.Id == secondTask.Id);
        Assert.Contains(firstManagerTasks, task => task.Id == firstTask.Id);
        Assert.DoesNotContain(firstManagerTasks, task => task.Id == secondTask.Id);
    }

    [Fact]
    public async Task CreateTask_RejectsManagerFromAnotherOffice_AndForeignCard()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var creator = await harness.CreateManagerAsync("creator@test.local", capacity: 5, onShift: true);
        var assignee = await harness.CreateManagerAsync("assignee@test.local", capacity: 5, onShift: true);
        var foreignManager = await harness.CreateManagerAsync("foreign@test.local", capacity: 5, onShift: true);
        var foreignProfile = await harness.Db.PanelUserProfiles.SingleAsync(x => x.UserId == foreignManager.Id);
        foreignProfile.OfficeId = Guid.NewGuid();
        await harness.Db.SaveChangesAsync();

        var foreignAssigneeTask = await harness.Sut.CreateTaskAsync(
            OfficeId,
            new CrmTaskCreateRequest(null, "Не создать", null, foreignManager.Id, null),
            creator.Id,
            isAdmin: false);
        Assert.Null(foreignAssigneeTask);

        var invalidTypeTask = await harness.Sut.CreateTaskAsync(
            OfficeId,
            new CrmTaskCreateRequest(
                null,
                "Не создать без типа",
                null,
                assignee.Id,
                null,
                CrmTaskImportances.Medium,
                "Unknown"),
            creator.Id,
            isAdmin: false);
        Assert.Null(invalidTypeTask);

        var response = await SeedResponseAsync(harness.Db);
        var card = NewCard(response.Id, creator.Id);
        harness.Db.CrmCandidateCards.Add(card);
        await harness.Db.SaveChangesAsync();

        var linkedTask = await harness.Sut.CreateTaskAsync(
            OfficeId,
            new CrmTaskCreateRequest(card.Id, "Проверить анкету", null, assignee.Id, null),
            assignee.Id,
            isAdmin: false);
        Assert.Null(linkedTask);
    }

    [Fact]
    public async Task TaskAuthorAssigneeOrAdmin_CanEditCancelAndReopenTask()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var creator = await harness.CreateManagerAsync("creator@test.local", capacity: 5, onShift: true);
        var assignee = await harness.CreateManagerAsync("assignee@test.local", capacity: 5, onShift: true);
        var replacement = await harness.CreateManagerAsync("replacement@test.local", capacity: 5, onShift: true);
        var task = await harness.Sut.CreateTaskAsync(
            OfficeId,
            new CrmTaskCreateRequest(null, "Первичный звонок", null, assignee.Id, DateTime.UtcNow.AddHours(1)),
            creator.Id,
            isAdmin: false);
        Assert.NotNull(task);
        var taskEntity = await harness.Db.CrmTasks.SingleAsync(x => x.Id == task.Id);
        var initialReminderVersion = taskEntity.ReminderVersion;

        var assigneeUpdate = await harness.Sut.UpdateTaskAsync(
            task.Id,
            new CrmTaskUpdateRequest("Изменённая задача", "Описание", replacement.Id, DateTime.UtcNow.AddDays(1), CrmTaskImportances.High, CrmTaskTypes.SignContract),
            assignee.Id,
            isAdmin: false);
        Assert.True(assigneeUpdate.Ok);

        var updated = await harness.Sut.UpdateTaskAsync(
            task.Id,
            new CrmTaskUpdateRequest("Изменённая задача", "Описание", replacement.Id, DateTime.UtcNow.AddDays(1), CrmTaskImportances.High, CrmTaskTypes.SignContract),
            creator.Id,
            isAdmin: false);
        Assert.True(updated.Ok);
        var afterUpdate = await harness.Sut.GetTaskAsync(task.Id, creator.Id, isAdmin: false);
        Assert.NotNull(afterUpdate);
        Assert.True(afterUpdate.CanManage);
        Assert.Equal("Изменённая задача", afterUpdate.Task.Title);
        Assert.Equal(replacement.Id, afterUpdate.Task.AssigneeUserId);
        Assert.Equal(CrmTaskImportances.High, afterUpdate.Task.Importance);
        Assert.Equal(CrmTaskTypes.SignContract, afterUpdate.Task.TaskType);
        Assert.NotEqual(initialReminderVersion, taskEntity.ReminderVersion);

        var updatedReminderVersion = taskEntity.ReminderVersion;
        var titleOnlyUpdate = await harness.Sut.UpdateTaskAsync(
            task.Id,
            new CrmTaskUpdateRequest("Уточнённое название", "Описание", replacement.Id, taskEntity.DueAtUtc, CrmTaskImportances.High),
            creator.Id,
            isAdmin: false);
        Assert.True(titleOnlyUpdate.Ok);
        Assert.Equal(updatedReminderVersion, taskEntity.ReminderVersion);

        var pendingNotification = new CrmTaskNotificationEntity
        {
            Id = Guid.NewGuid(),
            OfficeId = OfficeId,
            TaskId = task.Id,
            ReminderVersion = taskEntity.ReminderVersion,
            RecipientUserId = replacement.Id,
            Kind = CrmTaskNotificationKinds.DueIn24Hours,
            DueAtUtc = taskEntity.DueAtUtc!.Value,
            CreatedAtUtc = DateTime.UtcNow
        };
        harness.Db.CrmTaskNotifications.Add(pendingNotification);
        await harness.Db.SaveChangesAsync();

        Assert.True((await harness.Sut.CancelTaskAsync(task.Id, replacement.Id, isAdmin: false)).Ok);
        Assert.NotNull(pendingNotification.DismissedAtUtc);
        var cancelled = await harness.Sut.GetTaskAsync(task.Id, creator.Id, isAdmin: false);
        Assert.NotNull(cancelled);
        Assert.Equal(CrmTaskStatuses.Cancelled, cancelled.Task.Status);
        Assert.False((await harness.Sut.CompleteTaskAsync(task.Id, "готово", replacement.Id, isAdmin: false)).Ok);

        var beforeReopenVersion = taskEntity.ReminderVersion;
        Assert.True((await harness.Sut.ReopenTaskAsync(task.Id, creator.Id, isAdmin: false)).Ok);
        Assert.NotEqual(beforeReopenVersion, taskEntity.ReminderVersion);
        Assert.True((await harness.Sut.CompleteTaskAsync(task.Id, "Документы получены", replacement.Id, isAdmin: false)).Ok);
        Assert.True((await harness.Sut.ReopenTaskAsync(task.Id, "admin", isAdmin: true)).Ok);
        var reopened = await harness.Sut.GetTaskAsync(task.Id, creator.Id, isAdmin: false);
        Assert.NotNull(reopened);
        Assert.Equal(CrmTaskStatuses.Open, reopened.Task.Status);
    }

    [Fact]
    public async Task TaskAttachment_IsAvailableToParticipantsOnly()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var creator = await harness.CreateManagerAsync("creator@test.local", capacity: 5, onShift: true);
        var assignee = await harness.CreateManagerAsync("assignee@test.local", capacity: 5, onShift: true);
        var outsider = await harness.CreateManagerAsync("outsider@test.local", capacity: 5, onShift: true);
        var task = await harness.Sut.CreateTaskAsync(
            OfficeId,
            new CrmTaskCreateRequest(null, "Получить документы", null, assignee.Id, null),
            creator.Id,
            isAdmin: false);
        Assert.NotNull(task);

        await using var uploadContent = new MemoryStream([1, 2, 3, 4]);
        var (attachment, error) = await harness.Sut.AddTaskAttachmentAsync(
            task.Id,
            uploadContent,
            uploadContent.Length,
            "..\\документы.pdf",
            "application/pdf",
            creator.Id,
            isAdmin: false);

        Assert.Null(error);
        Assert.NotNull(attachment);
        Assert.Equal("документы.pdf", attachment.FileName);
        var detail = await harness.Sut.GetTaskAsync(task.Id, assignee.Id, isAdmin: false);
        Assert.NotNull(detail);
        Assert.Single(detail.Attachments);
        Assert.Equal(attachment.Id, detail.Attachments[0].Id);

        var outsiderDownload = await harness.Sut.OpenTaskAttachmentAsync(task.Id, attachment.Id, outsider.Id, isAdmin: false);
        Assert.Null(outsiderDownload.Stream);
        var assigneeDownload = await harness.Sut.OpenTaskAttachmentAsync(task.Id, attachment.Id, assignee.Id, isAdmin: false);
        Assert.NotNull(assigneeDownload.Stream);
        await using var downloadStream = assigneeDownload.Stream!;
        using var downloaded = new MemoryStream();
        await downloadStream.CopyToAsync(downloaded);
        Assert.Equal([1, 2, 3, 4], downloaded.ToArray());
    }

    [Fact]
    public async Task SearchByHistoricalPhone_IncludesClosedCard()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var manager = await harness.CreateManagerAsync("history-search@test.local", capacity: 5, onShift: true);
        var response = await SeedResponseAsync(harness.Db, "history-search");
        harness.Db.CandidatePhoneHistory.Add(new CandidatePhoneHistoryEntity
        {
            Id = Guid.NewGuid(),
            PersonId = response.PersonId,
            ResponseId = response.Id,
            PhoneRaw = "+7 (912) 345-67-89",
            PhoneNormalized = "79123456789",
            RecordedAtUtc = DateTime.UtcNow.AddDays(-1)
        });
        var card = NewCard(response.Id, manager.Id);
        card.IsClosed = true;
        card.CloseReason = CrmCloseReasons.NotRelevant;
        card.ClosedAtUtc = DateTime.UtcNow;
        harness.Db.CrmCandidateCards.Add(card);
        await harness.Db.SaveChangesAsync();

        var board = await harness.Sut.GetBoardAsync(
            OfficeId,
            manager.Id,
            isAdmin: false,
            new CrmBoardQuery(Search: "8 (912) 345-67-89", Scope: CrmBoardScopes.Mine));

        Assert.NotNull(board);
        Assert.False(board.IncludeClosed);
        Assert.Contains(board.Stages.SelectMany(x => x.Cards), x => x.Id == card.Id && x.IsClosed);
    }

    [Fact]
    public async Task CardNotesAndTaskComments_CanBeManagedFromCard()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var manager = await harness.CreateManagerAsync("card-actions@test.local", capacity: 5, onShift: true);
        var response = await SeedResponseAsync(harness.Db, "card-actions");
        response.City = "Екатеринбург";
        var card = NewCard(response.Id, manager.Id);
        harness.Db.CrmCandidateCards.Add(card);
        await harness.Db.SaveChangesAsync();

        Assert.True(await harness.Sut.AddNoteAsync(card.Id, "Первый текст", manager.Id, isAdmin: false));
        var note = await harness.Db.CrmCandidateNotes.SingleAsync(x => x.CardId == card.Id);
        Assert.True((await harness.Sut.SetNotePinnedAsync(card.Id, note.Id, true, manager.Id, isAdmin: false)).Ok);
        Assert.True((await harness.Sut.UpdateNoteAsync(card.Id, note.Id, "Обновлённый текст", manager.Id, isAdmin: false)).Ok);

        var task = await harness.Sut.CreateTaskAsync(
            OfficeId,
            new CrmTaskCreateRequest(card.Id, "Позвонить", null, manager.Id, DateTime.UtcNow.AddHours(1)),
            manager.Id,
            isAdmin: false);
        Assert.NotNull(task);
        var comment = await harness.Sut.AddTaskCommentAsync(task.Id, "Согласовано", manager.Id, isAdmin: false);
        Assert.NotNull(comment);
        Assert.True((await harness.Sut.UpdateTaskCommentAsync(
            task.Id, comment.Id, "Перенесли звонок", manager.Id, isAdmin: false)).Ok);

        var detail = await harness.Sut.GetCardAsync(card.Id, manager.Id, isAdmin: false);
        Assert.NotNull(detail);
        Assert.NotNull(detail.ClientTime);
        Assert.Equal(300, detail.ClientTime.UtcOffsetMinutes);
        var detailNote = Assert.Single(detail.Notes);
        Assert.True(detailNote.IsPinned);
        Assert.Equal("Обновлённый текст", detailNote.Text);
        Assert.True(detailNote.CanEdit);
        var noteActivity = Assert.Single(detail.Activity.Where(x => x.Kind == "note"));
        Assert.Equal("Закреплённый комментарий", noteActivity.Title);
        Assert.Equal(detailNote.CreatedAtUtc, noteActivity.AtUtc);
        Assert.Equal(detailNote.UpdatedAtUtc, noteActivity.UpdatedAtUtc);
        Assert.DoesNotContain(detail.Activity, x => x.Kind == "history" && x.Title is
            "Комментарий изменён" or "Комментарий закреплён" or "Комментарий откреплён");
        var detailComment = Assert.Single(detail.TaskComments!);
        Assert.Equal("Перенесли звонок", detailComment.Text);
        Assert.True(detailComment.CanDelete);

        Assert.True((await harness.Sut.DeleteTaskCommentAsync(
            task.Id, comment.Id, manager.Id, isAdmin: false)).Ok);
        Assert.True((await harness.Sut.DeleteTaskAsync(task.Id, manager.Id, isAdmin: false)).Ok);
        Assert.Empty(harness.Db.CrmTasks);
        Assert.True((await harness.Sut.DeleteNoteAsync(card.Id, note.Id, manager.Id, isAdmin: false)).Ok);
        Assert.Empty(harness.Db.CrmCandidateNotes);
    }

    [Fact]
    public async Task UpdateNote_KeepsOriginalPositionInActivityFeed()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var manager = await harness.CreateManagerAsync("note-order@test.local", capacity: 5, onShift: true);
        var response = await SeedResponseAsync(harness.Db, "note-order");
        var card = NewCard(response.Id, manager.Id);
        harness.Db.CrmCandidateCards.Add(card);
        await harness.Db.SaveChangesAsync();

        Assert.True(await harness.Sut.AddNoteAsync(card.Id, "Старый комментарий", manager.Id, isAdmin: false));
        Assert.True(await harness.Sut.AddNoteAsync(card.Id, "Новый комментарий", manager.Id, isAdmin: false));
        var notes = await harness.Db.CrmCandidateNotes
            .Where(x => x.CardId == card.Id)
            .OrderBy(x => x.CreatedAtUtc)
            .ToListAsync();
        var olderNote = notes[0];
        var newerNote = notes[1];
        olderNote.CreatedAtUtc = DateTime.UtcNow.AddMinutes(-10);
        newerNote.CreatedAtUtc = DateTime.UtcNow.AddMinutes(-5);
        await harness.Db.SaveChangesAsync();

        Assert.True((await harness.Sut.UpdateNoteAsync(
            card.Id,
            olderNote.Id,
            "Изменённый старый комментарий",
            manager.Id,
            isAdmin: false)).Ok);

        var detail = await harness.Sut.GetCardAsync(card.Id, manager.Id, isAdmin: false);
        Assert.NotNull(detail);
        var activityNotes = detail.Activity.Where(x => x.Kind == "note").ToList();
        Assert.Equal([newerNote.Id, olderNote.Id], activityNotes.Select(x => x.NoteId));
        Assert.Equal(olderNote.CreatedAtUtc, activityNotes[1].AtUtc);
        Assert.NotNull(activityNotes[1].UpdatedAtUtc);
    }

    [Fact]
    public async Task CompleteTask_CardActivityIncludesDescriptionAndCompletionReason()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var manager = await harness.CreateManagerAsync("task-feed@test.local", capacity: 5, onShift: true);
        var profile = await harness.Db.PanelUserProfiles.SingleAsync(x => x.UserId == manager.Id);
        profile.FullName = "Мария Сидорова";
        var response = await SeedResponseAsync(harness.Db, "task-feed");
        var card = NewCard(response.Id, manager.Id);
        harness.Db.CrmCandidateCards.Add(card);
        await harness.Db.SaveChangesAsync();

        var task = await harness.Sut.CreateTaskAsync(
            OfficeId,
            new CrmTaskCreateRequest(
                card.Id,
                "Связаться",
                "Уточнить готовность выйти на смену",
                manager.Id,
                DateTime.UtcNow.AddHours(1)),
            manager.Id,
            isAdmin: false);
        Assert.NotNull(task);

        Assert.True((await harness.Sut.UpdateTaskAsync(
            task.Id,
            new CrmTaskUpdateRequest(
                "Связаться",
                "Уточнить готовность и время выхода на смену",
                manager.Id,
                task.DueAtUtc,
                CrmTaskImportances.Medium,
                CrmTaskTypes.Contact),
            manager.Id,
            isAdmin: false)).Ok);

        Assert.True((await harness.Sut.CompleteTaskAsync(
            task.Id,
            "Кандидат подтвердил выход",
            manager.Id,
            isAdmin: false)).Ok);

        var detail = await harness.Sut.GetCardAsync(card.Id, manager.Id, isAdmin: false);
        Assert.NotNull(detail);
        var activity = Assert.Single(detail.Activity, item => item.TaskId == task.Id);
        Assert.Equal("task-done", activity.Kind);
        Assert.Equal("Связаться", activity.Title);
        Assert.Equal("Уточнить готовность и время выхода на смену", activity.Body);
        Assert.Equal("Кандидат подтвердил выход", activity.CompletionReason);
        Assert.Equal("Мария Сидорова", activity.ActorName);
        var completedTask = Assert.Single(detail.Tasks, item => item.Id == task.Id);
        Assert.NotNull(completedTask.UpdatedAtUtc);
        Assert.NotNull(completedTask.CompletedAtUtc);
        Assert.True(completedTask.UpdatedAtUtc >= completedTask.CreatedAtUtc);
        Assert.True(completedTask.CompletedAtUtc >= completedTask.UpdatedAtUtc);
        Assert.DoesNotContain(detail.Activity, item => item.Title == "Задача изменена");
    }

    [Fact]
    public async Task QueueChatMessage_Owner_CreatesPlannedMessage()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var manager = await harness.CreateManagerAsync("chat@test.local", capacity: 5, onShift: true);
        var response = await SeedResponseAsync(harness.Db, "avito-src-1");
        var card = NewCard(response.Id, manager.Id);
        harness.Db.CrmCandidateCards.Add(card);
        await harness.Db.SaveChangesAsync();

        var (ok, error) = await harness.Sut.QueueChatMessageAsync(card.Id, "  Напишите номер  ", manager.Id, isAdmin: false);

        Assert.True(ok, error);
        var stored = Assert.Single(harness.Db.CrmOutboundChatMessages);
        Assert.Equal("Напишите номер", stored.Text);
        Assert.Equal(CrmOutboundChatStatuses.Planned, stored.Status);
        Assert.Null(stored.SentAtUtc);
        var detail = await harness.Sut.GetCardAsync(card.Id, manager.Id, isAdmin: false);
        var planned = Assert.Single(detail!.Chat);
        Assert.Equal("Напишите номер", planned.Text);
        Assert.Equal(CrmOutboundChatStatuses.Planned, planned.Status);
        Assert.Equal("Запланировано", planned.StatusLabel);
        Assert.True(planned.CanCancel);
        Assert.False(string.IsNullOrWhiteSpace(planned.TimeLabel));
    }

    [Fact]
    public async Task QueueChatMessage_WithoutSourceResponseId_Fails()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var manager = await harness.CreateManagerAsync("nosrc@test.local", capacity: 5, onShift: true);
        var response = await SeedResponseAsync(harness.Db, " ");
        response.SourceResponseId = "";
        var card = NewCard(response.Id, manager.Id);
        harness.Db.CrmCandidateCards.Add(card);
        await harness.Db.SaveChangesAsync();

        var (ok, error) = await harness.Sut.QueueChatMessageAsync(card.Id, "Привет", manager.Id, isAdmin: false);

        Assert.False(ok);
        Assert.Contains("Avito", error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(harness.Db.CrmOutboundChatMessages);
    }

    [Fact]
    public async Task QueueChatMessage_ClosedCard_Fails()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var manager = await harness.CreateManagerAsync("closed-chat@test.local", capacity: 5, onShift: true);
        var response = await SeedResponseAsync(harness.Db, "src-closed");
        var card = NewCard(response.Id, manager.Id);
        card.IsClosed = true;
        harness.Db.CrmCandidateCards.Add(card);
        await harness.Db.SaveChangesAsync();

        var (ok, error) = await harness.Sut.QueueChatMessageAsync(card.Id, "Привет", manager.Id, isAdmin: false);

        Assert.False(ok);
        Assert.Contains("закрытой", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CancelChatMessage_Planned_HidesFromThread()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var manager = await harness.CreateManagerAsync("cancel-chat@test.local", capacity: 5, onShift: true);
        var response = await SeedResponseAsync(harness.Db, "src-cancel");
        var card = NewCard(response.Id, manager.Id);
        harness.Db.CrmCandidateCards.Add(card);
        await harness.Db.SaveChangesAsync();
        await harness.Sut.QueueChatMessageAsync(card.Id, "Отменить меня", manager.Id, isAdmin: false);
        var messageId = Assert.Single(harness.Db.CrmOutboundChatMessages).Id;

        var (ok, error) = await harness.Sut.CancelChatMessageAsync(card.Id, messageId, manager.Id, isAdmin: false);

        Assert.True(ok, error);
        var detail = await harness.Sut.GetCardAsync(card.Id, manager.Id, isAdmin: false);
        Assert.Empty(detail!.Chat);
    }

    [Fact]
    public async Task GetCard_MergesSentOutboundWithAvitoOutgoing()
    {
        await using var harness = await Harness.CreateAsync();
        SeedOffice(harness.Db, crmEnabled: true);
        var manager = await harness.CreateManagerAsync("merge-chat@test.local", capacity: 5, onShift: true);
        var response = await SeedResponseAsync(harness.Db, "src-merge");
        response.ChatMessagesJson =
            """[{"text":"Ещё актуально?","at":"2026-08-13T10:00:00Z","side":"left","isPlatform":false},{"text":"Да, напишите номер","at":"2026-08-13T10:05:00Z","side":"right","isPlatform":false}]""";
        var card = NewCard(response.Id, manager.Id);
        harness.Db.CrmCandidateCards.Add(card);
        harness.Db.CrmOutboundChatMessages.Add(new CrmOutboundChatMessageEntity
        {
            Id = Guid.NewGuid(),
            CardId = card.Id,
            ResponseId = response.Id,
            AuthorUserId = manager.Id,
            AuthorName = "Менеджер",
            Text = "Да, напишите номер",
            Status = CrmOutboundChatStatuses.Sent,
            CreatedAtUtc = DateTime.UtcNow.AddMinutes(-10),
            SentAtUtc = DateTime.UtcNow.AddMinutes(-5)
        });
        await harness.Db.SaveChangesAsync();

        var detail = await harness.Sut.GetCardAsync(card.Id, manager.Id, isAdmin: false);

        Assert.Equal(2, detail!.Chat.Count);
        Assert.Equal("incoming", detail.Chat[0].Tone);
        Assert.Equal(CrmOutboundChatStatuses.Sent, detail.Chat[1].Status);
        Assert.Equal("Отправлено", detail.Chat[1].StatusLabel);
        Assert.False(detail.Chat[1].CanCancel);
    }

    private static CrmCandidateCardEntity NewCard(Guid responseId, string? managerId = null) => new()
    {
        Id = Guid.NewGuid(),
        ResponseId = responseId,
        OfficeId = OfficeId,
        ManagerUserId = managerId,
        InitialManagerUserId = managerId,
        InitialAssignedAtUtc = managerId is null ? null : DateTime.UtcNow,
        IsInActiveLoad = true,
        CreatedAtUtc = DateTime.UtcNow,
        UpdatedAtUtc = DateTime.UtcNow,
        StageChangedAtUtc = DateTime.UtcNow
    };

    private static CrmSuccessDocumentUpload SuccessUpload(
        string category,
        string fileName,
        byte[] content,
        string contentType = "image/png") =>
        new(category, fileName, contentType, content.LongLength, () => new MemoryStream(content, writable: false));

    private static CrmCandidateHistoryEntity NewAssignmentHistory(
        Guid cardId,
        DateTime createdAtUtc,
        string details) => new()
        {
            Id = Guid.NewGuid(),
            CardId = cardId,
            Action = "Assigned",
            Details = details,
            ActorUserId = "system",
            ActorName = "Система",
            CreatedAtUtc = createdAtUtc
        };

    private static void SeedOffice(
        OrbitaDbContext db,
        bool crmEnabled,
        IReadOnlyList<string>? stages = null)
    {
        db.Offices.Add(new OfficeEntity
        {
            Id = OfficeId,
            Name = "CRM Office",
            RegistrationSecretHash = "hash",
            CreatedAtUtc = DateTime.UtcNow,
            IsEnabled = true,
            CrmEnabled = crmEnabled,
            CrmStagesJson = stages is null ? null : CrmStages.Serialize(stages)
        });
        db.Workers.Add(new WorkerEntity
        {
            Id = WorkerId,
            OfficeId = OfficeId,
            DisplayName = "w",
            MachineName = "pc",
            ApiKeyHash = "h",
            AppVersion = "1",
            MonitoringStatus = "Stopped",
            CreatedAtUtc = DateTime.UtcNow
        });
        db.SaveChanges();
    }

    private static async Task<CandidateResponseEntity> SeedResponseAsync(OrbitaDbContext db, string sourceId = "src")
    {
        var person = TestCandidatePersonFactory.CreatePerson(OfficeId, fullName: "Иванов Иван", firstName: "Иван", lastName: "Иванов");
        var response = TestCandidatePersonFactory.CreateResponse(
            OfficeId,
            person.Id,
            WorkerId,
            phone: "79990001122",
            sourceResponseId: sourceId,
            fullName: "Иванов Иван");
        response.Status = ResponseStatuses.New;
        response.Vacancy = "Сварщик";
        db.CandidatePersons.Add(person);
        db.CandidateResponses.Add(response);
        await db.SaveChangesAsync();
        return response;
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        public OrbitaDbContext Db { get; }
        public UserManager<IdentityUser> Users { get; }
        public CrmWorkspaceService Sut { get; }
        public ManualTimeProvider Clock { get; }
        public string AttachmentRoot { get; }
        public string SuccessDocumentRoot { get; }

        private Harness(
            ServiceProvider services,
            OrbitaDbContext db,
            UserManager<IdentityUser> users,
            CrmWorkspaceService sut,
            ManualTimeProvider clock,
            string attachmentRoot,
            string successDocumentRoot)
        {
            _services = services;
            Db = db;
            Users = users;
            Sut = sut;
            Clock = clock;
            AttachmentRoot = attachmentRoot;
            SuccessDocumentRoot = successDocumentRoot;
        }

        public static async Task<Harness> CreateAsync()
        {
            var dbName = Guid.NewGuid().ToString("N");
            var services = new ServiceCollection();
            services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
            services.AddDbContext<OrbitaDbContext>(o => o
                .UseInMemoryDatabase(dbName)
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
            services.AddIdentityCore<IdentityUser>(o =>
                {
                    o.Password.RequireDigit = false;
                    o.Password.RequireLowercase = false;
                    o.Password.RequireUppercase = false;
                    o.Password.RequireNonAlphanumeric = false;
                    o.Password.RequiredLength = 6;
                })
                .AddRoles<IdentityRole>()
                .AddEntityFrameworkStores<OrbitaDbContext>();

            var sp = services.BuildServiceProvider();
            var db = sp.GetRequiredService<OrbitaDbContext>();
            await db.Database.EnsureCreatedAsync();

            var roleManager = sp.GetRequiredService<RoleManager<IdentityRole>>();
            foreach (var role in PanelRoles.CrmDeskRoles)
            {
                if (!await roleManager.RoleExistsAsync(role))
                {
                    await roleManager.CreateAsync(new IdentityRole(role));
                }
            }

            var users = sp.GetRequiredService<UserManager<IdentityUser>>();
            var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
            var distribution = new CrmLeadDistributionService(db, users, clock);
            var attachmentRoot = Path.Combine(Path.GetTempPath(), "orbita-crm-task-tests", Guid.NewGuid().ToString("N"));
            var attachments = new CrmTaskAttachmentStorageService(Options.Create(new CrmTaskAttachmentOptions
            {
                DataPath = attachmentRoot
            }));
            var successDocumentRoot = Path.Combine(Path.GetTempPath(), "orbita-crm-success-tests", Guid.NewGuid().ToString("N"));
            var successDocuments = new CrmSuccessDocumentStorageService(Options.Create(new CrmSuccessDocumentOptions
            {
                DataPath = successDocumentRoot
            }));
            var deadlineNotifications = new CrmDeadlineNotificationService(
                db,
                TimeProvider.System,
                Options.Create(new CrmDeadlineNotificationOptions()),
                new NoOpCrmNotificationRealtimeNotifier(),
                sp.GetRequiredService<ILogger<CrmDeadlineNotificationService>>());
            var sut = new CrmWorkspaceService(
                db,
                users,
                distribution,
                taskAttachments: attachments,
                successDocuments: successDocuments,
                deadlineNotifications: deadlineNotifications);
            return new Harness(sp, db, users, sut, clock, attachmentRoot, successDocumentRoot);
        }

        public Task<IdentityUser> CreateManagerAsync(string email, int capacity, bool onShift) =>
            CreateDeskUserAsync(email, capacity, onShift, PanelRoles.Manager);

        public async Task<IdentityUser> CreateDeskUserAsync(
            string email,
            int capacity,
            bool onShift,
            string role,
            Guid? officeId = null)
        {
            var user = new IdentityUser { UserName = email, Email = email, EmailConfirmed = true };
            var result = await Users.CreateAsync(user, "Password1!");
            Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Description)));
            await Users.AddToRoleAsync(user, role);
            Db.PanelUserProfiles.Add(new PanelUserProfileEntity
            {
                UserId = user.Id,
                OfficeId = officeId ?? OfficeId,
                CrmCapacity = capacity,
                CrmShiftActive = onShift,
                CrmShiftStartedAtUtc = onShift ? Clock.GetUtcNow().UtcDateTime : null
            });
            await Db.SaveChangesAsync();
            return user;
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _services.DisposeAsync();
            if (Directory.Exists(AttachmentRoot))
            {
                Directory.Delete(AttachmentRoot, recursive: true);
            }
            if (Directory.Exists(SuccessDocumentRoot))
            {
                Directory.Delete(SuccessDocumentRoot, recursive: true);
            }
        }
    }

    public sealed class ManualTimeProvider(DateTimeOffset initialUtc) : TimeProvider
    {
        private DateTimeOffset _utcNow = initialUtc;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan amount) => _utcNow = _utcNow.Add(amount);
    }

    private sealed class NoOpCrmNotificationRealtimeNotifier : ICrmNotificationRealtimeNotifier
    {
        public Task NotifyAsync(
            string recipientUserId,
            CrmTaskNotificationDto notification,
            CancellationToken ct = default) => Task.CompletedTask;
    }
}
