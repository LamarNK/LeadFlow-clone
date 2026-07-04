namespace Orbita.Web.Services;

public interface IOfficeContext
{
    bool IsAdmin { get; }

    bool ShowAllOffices { get; }

    bool ShowOfficeColumn { get; }

    Guid? EffectiveOfficeId { get; }

    string? ContextLabel { get; }

    void Bind(HttpContext context);
}