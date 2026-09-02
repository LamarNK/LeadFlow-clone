namespace Orbita.Web.Models.ViewModels;

public static class ListPageSizeDefaults
{
    public static readonly int[] Options = [10, 12, 25, 50, 100];

    public const int Accounts = 10;
    public const int Workers = 12;
    public const int Dashboard = 25;
    public const int Responses = 10;
    public const int Events = 10;
    public const int Errors = 10;

    public static int Normalize(int? pageSize, int defaultSize)
    {
        if (pageSize is null or <= 0)
            return defaultSize;

        return Options.Contains(pageSize.Value) ? pageSize.Value : defaultSize;
    }
}