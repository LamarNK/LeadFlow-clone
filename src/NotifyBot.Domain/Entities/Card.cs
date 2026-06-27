namespace NotifyBot.Domain.Entities;

public sealed class Card
{
    public int Id { get; set; }

    public required string Last4 { get; set; }

    public string? Label { get; set; }

    public long? DestinationChatId { get; set; }

    public bool Enabled { get; set; } = true;
}