namespace Orbita.Web.Options;

/// <summary>
/// Локальный режим просмотра UI без PostgreSQL и Orbita.Api.
/// Включать только для разработки дизайна.
/// </summary>
public sealed class DesignPreviewOptions
{
    public const string SectionName = "DesignPreview";

    public bool Enabled { get; set; }

    public string Email { get; set; } = "admin@orbita.local";

    public string Password { get; set; } = "OrbitaAdmin1!";

    public string DisplayName { get; set; } = "Администратор";
}