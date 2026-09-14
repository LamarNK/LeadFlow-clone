namespace Orbita.Contracts;

/// <summary>Explicit per-user grant, never inherited from a role/access profile.</summary>
public static class CrmCardDeletionPermission
{
    public const string ClaimType = "orbita.crm-card-deletion";
    public const string GrantedValue = "true";
    public const string DeniedMessage = "Нет права на удаление этой карточки.";
}
