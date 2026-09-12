namespace LeadFlow.Core.Models;

/// <summary>Внутреннее состояние срока объявления. Статус Avito хранится отдельно в <c>StatusText</c>.</summary>
public static class AvitoAdListingStates
{
    public const string Active = "Active";
    public const string ApproachingExpiry = "ApproachingExpiry";
    public const string ExpiresToday = "ExpiresToday";
    public const string Expired = "Expired";
    public const string NotActive = "NotActive";
    public const string UnknownPublicationDate = "UnknownPublicationDate";
    public const string ParseFailed = "ParseFailed";
}
