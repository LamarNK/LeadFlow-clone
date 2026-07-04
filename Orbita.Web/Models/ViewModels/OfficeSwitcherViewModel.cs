namespace Orbita.Web.Models.ViewModels;

public sealed class OfficeSwitcherViewModel
{
    public bool IsVisible { get; init; }

    public bool IsAllOfficesSelected { get; init; }

    public Guid? SelectedOfficeId { get; init; }

    public string SelectedLabel { get; init; } = "Все офисы";

    public IReadOnlyList<OfficeSwitcherOptionViewModel> Offices { get; init; } = [];

    public string ReturnUrl { get; init; } = "/";
}

public sealed class OfficeSwitcherOptionViewModel
{
    public Guid Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public bool IsEnabled { get; init; } = true;
}