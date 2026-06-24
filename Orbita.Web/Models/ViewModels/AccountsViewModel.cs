namespace Orbita.Web.Models.ViewModels;

public sealed class AccountsIndexViewModel
{
    public PageHeaderViewModel Header { get; init; } = new() { Title = "Аккаунты", Subtitle = "Все аккаунты Avito по воркерам" };
    public IReadOnlyList<AccountRowViewModel> Rows { get; init; } = [];
}

public sealed class AccountRowViewModel
{
    public string AccountName { get; init; } = string.Empty;
    public string WorkerName { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public int ActiveAds { get; init; }
    public int Blocked { get; init; }
    public string? LastError { get; init; }
}