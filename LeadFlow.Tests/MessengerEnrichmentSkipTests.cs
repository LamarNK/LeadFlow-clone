using LeadFlow.Core.Services.AdsPower;
using Xunit;

namespace LeadFlow.Tests;

public sealed class MessengerEnrichmentSkipTests
{
    [Fact]
    public void ShouldSkip_KnownWithoutUnreadOrWatchOrPending_IsTrue() =>
        Assert.True(MessengerEnrichmentSkip.ShouldSkipKnownCandidate(
            isKnownSourceId: true,
            hasUnread: false,
            openPhoneWatch: false,
            hasPendingOutbound: false));

    [Fact]
    public void ShouldSkip_PendingOutbound_IsFalse() =>
        Assert.False(MessengerEnrichmentSkip.ShouldSkipKnownCandidate(
            isKnownSourceId: true,
            hasUnread: false,
            openPhoneWatch: false,
            hasPendingOutbound: true));

    [Fact]
    public void ShouldSkip_Unread_IsFalse() =>
        Assert.False(MessengerEnrichmentSkip.ShouldSkipKnownCandidate(
            isKnownSourceId: true,
            hasUnread: true,
            openPhoneWatch: false,
            hasPendingOutbound: false));

    [Fact]
    public void ShouldSkip_UnknownCandidate_IsFalse() =>
        Assert.False(MessengerEnrichmentSkip.ShouldSkipKnownCandidate(
            isKnownSourceId: false,
            hasUnread: false,
            openPhoneWatch: false,
            hasPendingOutbound: false));
}