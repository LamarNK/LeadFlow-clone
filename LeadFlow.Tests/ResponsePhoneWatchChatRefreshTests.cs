using LeadFlow.Core.Services.Worker;
using Xunit;

namespace LeadFlow.Tests;

public sealed class ResponsePhoneWatchChatRefreshTests
{
    [Fact]
    public void ShouldPublish_WhenWatchOpen_Skip_AndChatPresent()
    {
        Assert.True(ResponsePhoneWatchChatRefresh.ShouldPublish(
            watchingOpen: true,
            ResponsePhoneWatchAction.Skip,
            chatMessagesJson: """[{"text":"привет","side":"left"}]"""));
    }

    [Fact]
    public void ShouldNotPublish_WhenWatchClosed()
    {
        Assert.False(ResponsePhoneWatchChatRefresh.ShouldPublish(
            watchingOpen: false,
            ResponsePhoneWatchAction.Skip,
            chatMessagesJson: """[{"text":"привет","side":"left"}]"""));
    }

    [Fact]
    public void ShouldNotPublish_WhenChatEmpty()
    {
        Assert.False(ResponsePhoneWatchChatRefresh.ShouldPublish(
            watchingOpen: true,
            ResponsePhoneWatchAction.Skip,
            chatMessagesJson: ""));
        Assert.False(ResponsePhoneWatchChatRefresh.ShouldPublish(
            watchingOpen: true,
            ResponsePhoneWatchAction.Skip,
            chatMessagesJson: null));
        Assert.False(ResponsePhoneWatchChatRefresh.ShouldPublish(
            watchingOpen: true,
            ResponsePhoneWatchAction.Skip,
            chatMessagesJson: "   "));
    }

    [Fact]
    public void ShouldPublish_WhenWatchOpen_Skip_AndProfileFieldsPresent()
    {
        Assert.True(ResponsePhoneWatchChatRefresh.ShouldPublish(
            watchingOpen: true,
            ResponsePhoneWatchAction.Skip,
            chatMessagesJson: "",
            hasProfileRefresh: true));
        Assert.True(ResponsePhoneWatchChatRefresh.HasProfileRefresh(new CandidateResponse
        {
            City = "Батайск",
            Vacancy = "Разнорабочий вахта"
        }));
        Assert.False(ResponsePhoneWatchChatRefresh.HasProfileRefresh(new CandidateResponse()));
    }

    [Fact]
    public void ShouldNotPublish_WhenActionIsNotSkip()
    {
        Assert.False(ResponsePhoneWatchChatRefresh.ShouldPublish(
            watchingOpen: true,
            ResponsePhoneWatchAction.PublishInitial,
            chatMessagesJson: """[{"text":"привет"}]"""));
        Assert.False(ResponsePhoneWatchChatRefresh.ShouldPublish(
            watchingOpen: true,
            ResponsePhoneWatchAction.PublishPhoneChanged,
            chatMessagesJson: """[{"text":"привет"}]"""));
    }
}
