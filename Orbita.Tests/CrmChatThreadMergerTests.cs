using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class CrmChatThreadMergerTests
{
    [Fact]
    public void Merge_EmptyOutbound_ReturnsAvitoAsIs()
    {
        var avito = new[]
        {
            Incoming("Привет", "10:00"),
            Outgoing("Ок", "10:01")
        };

        var merged = CrmChatThreadMerger.Merge(avito, []);

        Assert.Equal(avito, merged);
    }

    [Fact]
    public void Merge_ReplacesMatchingAvitoOutgoingWithSentOutbound()
    {
        var sentId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var avito = new[]
        {
            Incoming("Здравствуйте", "10:00"),
            Outgoing("Напишите номер", "10:05")
        };
        var outbound = new[]
        {
            Sent(sentId, "Напишите номер", "10:05")
        };

        var merged = CrmChatThreadMerger.Merge(avito, outbound);

        Assert.Equal(2, merged.Count);
        Assert.Equal("incoming", merged[0].Tone);
        Assert.Equal(sentId, merged[1].Id);
        Assert.Equal(CrmOutboundChatStatuses.Sent, merged[1].Status);
        Assert.Equal("Отправлено", merged[1].StatusLabel);
    }

    [Fact]
    public void Merge_AppendsUnmatchedSentAndPlanned()
    {
        var plannedId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var sentId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var avito = new[]
        {
            Incoming("Ещё актуально?", "09:00")
        };
        var outbound = new[]
        {
            Sent(sentId, "Да, актуально", "09:10"),
            Planned(plannedId, "Когда удобно созвониться?", "09:12")
        };

        var merged = CrmChatThreadMerger.Merge(avito, outbound);

        Assert.Equal(3, merged.Count);
        Assert.Equal(sentId, merged[1].Id);
        Assert.Equal(plannedId, merged[2].Id);
        Assert.Equal(CrmOutboundChatStatuses.Planned, merged[2].Status);
        Assert.True(merged[2].CanCancel);
    }

    [Fact]
    public void Merge_TwoIdenticalTexts_ConsumesOneAvitoOutgoingPerSent()
    {
        var first = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var second = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var avito = new[]
        {
            Outgoing("Привет", "10:00"),
            Outgoing("Привет", "10:01")
        };
        var outbound = new[]
        {
            Sent(first, "Привет", "10:00"),
            Sent(second, "Привет", "10:01")
        };

        var merged = CrmChatThreadMerger.Merge(avito, outbound);

        Assert.Equal(2, merged.Count);
        Assert.Equal(first, merged[0].Id);
        Assert.Equal(second, merged[1].Id);
    }

    private static CrmChatMessageDto Incoming(string text, string time) =>
        new(text, time, "incoming");

    private static CrmChatMessageDto Outgoing(string text, string time) =>
        new(text, time, "outgoing");

    private static CrmChatMessageDto Sent(Guid id, string text, string time) =>
        new(text, time, "outgoing", id, CrmOutboundChatStatuses.Sent, CrmOutboundChatStatuses.GetLabel(CrmOutboundChatStatuses.Sent));

    private static CrmChatMessageDto Planned(Guid id, string text, string time) =>
        new(text, time, "outgoing", id, CrmOutboundChatStatuses.Planned, CrmOutboundChatStatuses.GetLabel(CrmOutboundChatStatuses.Planned), CanCancel: true);
}