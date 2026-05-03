using System.Collections.ObjectModel;

namespace LeadFlow.Models;

public sealed class DashboardStats
{
    public int NewResponses { get; set; }
    public int TotalToday { get; set; }
    public int SentToCrm { get; set; }
    public int InProgress { get; set; }
    public int Duplicates { get; set; }
    public int Errors { get; set; }
    public int ConnectedAccounts { get; set; }
    public int RequiresAuthorization { get; set; }
    public ObservableCollection<ActivityPoint> Activity { get; set; } = new();
}

public sealed class ActivityPoint
{
    public string Label { get; set; } = string.Empty;
    public int NewCount { get; set; }
    public int SentCount { get; set; }
    public int DuplicateCount { get; set; }
    public int ErrorCount { get; set; }
}
