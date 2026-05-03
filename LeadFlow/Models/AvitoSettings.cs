using System.Collections.ObjectModel;

namespace LeadFlow.Models;

public sealed class AvitoSettings
{
    public ObservableCollection<AvitoAccount> Accounts { get; set; } = new();
}
