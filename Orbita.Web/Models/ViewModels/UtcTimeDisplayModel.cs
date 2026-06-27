namespace Orbita.Web.Models.ViewModels;

public sealed record UtcTimeDisplayModel(DateTime? Utc, string Format = "time", string? CssClass = null);