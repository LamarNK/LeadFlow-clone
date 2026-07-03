namespace Orbita.Web.Models.ViewModels;

public sealed class AccountComboboxViewModel
{
    public string FieldName { get; init; } = "accountId";
    public string Label { get; init; } = "Аккаунт";
    public string Placeholder { get; init; } = "Все аккаунты";
    public string? SelectedValue { get; init; }
    public IReadOnlyList<EventFilterOptionViewModel> Options { get; init; } = [];

    public string SelectedLabel =>
        Options.FirstOrDefault(o => o.Value == (SelectedValue ?? ""))?.Label
        ?? (string.IsNullOrWhiteSpace(SelectedValue) ? "" : SelectedValue);
}