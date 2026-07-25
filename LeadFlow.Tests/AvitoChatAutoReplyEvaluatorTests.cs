using LeadFlow.Core.Models;
using LeadFlow.Core.Services.Avito;
using Xunit;

namespace LeadFlow.Tests;

public sealed class AvitoChatAutoReplyEvaluatorTests
{
    [Fact]
    public void NeedsAutoReply_CandidateQuestionWithoutEmployerReply_ReturnsTrue()
    {
        var messages = new[]
        {
            Platform("Кандидат откликнулся на вакансию."),
            Platform("Кандидат соответствует требованиям."),
            Candidate("Здравствуйте а где именно объект?")
        };

        Assert.True(AvitoChatAutoReplyEvaluator.NeedsAutoReply(messages));
    }

    [Fact]
    public void NeedsAutoReply_EmployerAlreadyReplied_ReturnsFalse()
    {
        var messages = new[]
        {
            Platform("Кандидат откликнулся на вакансию."),
            Candidate("Здравствуйте а где именно объект?"),
            Employer("Доброго времени суток, наши коллеги с вами свяжутся и поподробнее расскажут о вакансии.")
        };

        Assert.False(AvitoChatAutoReplyEvaluator.NeedsAutoReply(messages));
    }

    [Fact]
    public void NeedsAutoReply_OnlyPlatformMessages_ReturnsFalse()
    {
        var messages = new[]
        {
            Platform("Кандидат откликнулся на вакансию."),
            Platform("Кандидат соответствует требованиям.")
        };

        Assert.False(AvitoChatAutoReplyEvaluator.NeedsAutoReply(messages));
    }

    [Fact]
    public void NeedsAutoReply_PlatformAfterCandidateQuestion_StillNeedsReply()
    {
        var messages = new[]
        {
            Candidate("Здравствуйте"),
            Platform("Вот его резюме Подсобный рабочий.")
        };

        Assert.True(AvitoChatAutoReplyEvaluator.NeedsAutoReply(messages));
    }

    [Fact]
    public void NeedsAutoReply_TwoCandidateQuestionsWithoutEmployerReply_ReturnsTrueOnce()
    {
        var messages = new[]
        {
            Candidate("Здравствуйте, уточните пожалуйста Где предстоит работать?"),
            Candidate("Вы пишете проезд туда бесплатно не понравится всё равно за наш счёт куда надо ехать?")
        };

        Assert.True(AvitoChatAutoReplyEvaluator.NeedsAutoReply(messages));
    }

    [Fact]
    public void NeedsAutoReply_DefaultAutoReplyAlreadySent_ReturnsFalse()
    {
        var messages = new[]
        {
            Candidate("Здравствуйте, уточните пожалуйста Где предстоит работать?"),
            Candidate("Вы пишете проезд туда бесплатно не понравится всё равно за наш счёт куда надо ехать?"),
            Employer(AvitoMessengerAutoReply.DefaultMessage)
        };

        Assert.False(AvitoChatAutoReplyEvaluator.NeedsAutoReply(messages));
    }

    [Fact]
    public void NeedsAutoReply_ConfiguredAutoReplyAlreadySent_ReturnsFalse()
    {
        const string configuredMessage = "Спасибо, мы вам перезвоним.";
        var messages = new[]
        {
            Candidate("Здравствуйте"),
            Employer(configuredMessage)
        };

        Assert.False(AvitoChatAutoReplyEvaluator.NeedsAutoReply(messages, configuredMessage));
    }
    private static AvitoChatMessage Platform(string text) => new()
    {
        Text = text,
        Side = "left",
        IsPlatform = true
    };

    private static AvitoChatMessage Candidate(string text) => new()
    {
        Text = text,
        Side = "left",
        IsPlatform = false
    };

    private static AvitoChatMessage Employer(string text) => new()
    {
        Text = text,
        Side = "right",
        IsPlatform = false
    };
}