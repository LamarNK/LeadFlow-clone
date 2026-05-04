using LeadFlow.Models;

namespace LeadFlow.Services;

public class ProfileResult
{
    public List<AvitoAdStatus> ActiveAds { get; set; } = new();
    public int BlockedCount { get; set; }
    public int DraftsCount { get; set; }
}
