using System.Collections.ObjectModel;

namespace LeadFlow.Core.Models;

public sealed class AvitoSettings
{
    public ObservableCollection<AvitoAccount> Accounts { get; set; } = new();
}
