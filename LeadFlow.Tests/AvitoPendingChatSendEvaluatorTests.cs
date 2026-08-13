using LeadFlow.Core.Models;
using LeadFlow.Core.Services.Avito;
using Orbita.Contracts;
using Xunit;

namespace LeadFlow.Tests;

public sealed class AvitoPendingChatSendEvaluatorTests
{
    [Fact]
    public void Evaluate_EmptyPending_ReturnsEmpty()
    {
        var decision = AvitoPendingChatSendEvaluator.Evaluate([Employer("Hi")], []);

        Assert.Empty(decision.AlreadyInChat);
        Assert.Empty(decision.ToSend);
    }

    [Fact]
    public void Evaluate_TextAlreadyInChat_AcksWithoutSend()
    {
        var pendingId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var decision = AvitoPendingChatSendEvaluator.Evaluate(
            [Candidate("Вопрос"), Employer("Напишите номер")],
            [Pending(pendingId, "Напишите номер")]);

        Assert.Equal([pendingId], decision.AlreadyInChat.Select(x => x.Id));
        Assert.Empty(decision.ToSend);
    }

    [Fact]
    public void Evaluate_MissingText_NeedsSend()
    {
        var pendingId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var decision = AvitoPendingChatSendEvaluator.Evaluate(
            [Candidate("Вопрос")],
            [Pending(pendingId, "Напишите номер")]);

        Assert.Empty(decision.AlreadyInChat);
        Assert.Equal([pendingId], decision.ToSend.Select(x => x.Id));
    }

    [Fact]
    public void Evaluate_TwoSameTexts_OneAlreadyPresent_SendsSecond()
    {
        var first = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var second = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var decision = AvitoPendingChatSendEvaluator.Evaluate(
            [Employer("Привет")],
            [Pending(first, "Привет"), Pending(second, "Привет")]);

        Assert.Equal([first], decision.AlreadyInChat.Select(x => x.Id));
        Assert.Equal([second], decision.ToSend.Select(x => x.Id));
    }

    [Fact]
    public void Evaluate_IgnoresPlatformAndCandidateMessages()
    {
        var pendingId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var decision = AvitoPendingChatSendEvaluator.Evaluate(
            [
                new AvitoChatMessage { Text = "Напишите номер", Side = "left", IsPlatform = true },
                Candidate("Напишите номер")
            ],
            [Pending(pendingId, "Напишите номер")]);

        Assert.Empty(decision.AlreadyInChat);
        Assert.Equal([pendingId], decision.ToSend.Select(x => x.Id));
    }

    [Fact]
    public void Evaluate_EmployerMessageBeforeQueue_NeedsSend()
    {
        var queuedAt = DateTime.UtcNow;
        var decision = AvitoPendingChatSendEvaluator.Evaluate(
            [Employer("Здравствуйте", queuedAt.AddMinutes(-5))],
            [Pending(Guid.NewGuid(), "Здравствуйте", queuedAt)]);

        Assert.Empty(decision.AlreadyInChat);
        Assert.Single(decision.ToSend);
    }

    private static AvitoChatMessage Candidate(string text) => new()
    {
        Text = text,
        Side = "left"
    };

    private static AvitoChatMessage Employer(string text, DateTime? at = null) => new()
    {
        Text = text,
        Side = "right",
        At = (at ?? DateTime.UtcNow).ToString("O")
    };

    private static WorkerPendingChatMessageDto Pending(Guid id, string text, DateTime? queuedAt = null) =>
        new(id, "src-1", text, QueuedAtUtc: queuedAt ?? DateTime.UtcNow.AddMinutes(-1));
}
