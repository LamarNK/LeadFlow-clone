using CommunityToolkit.Mvvm.ComponentModel;
using LeadFlow.Models;

namespace LeadFlow.ViewModels;

public partial class DuplicateCheckViewModel : ObservableObject
{
    [ObservableProperty]
    private DuplicateCheckResult result = new();

    public void Update(CandidateResponse? response)
    {
        if (response is null)
        {
            Result = new DuplicateCheckResult();
            return;
        }

        Result = new DuplicateCheckResult
        {
            PhoneRaw = response.PhoneRaw,
            PhoneNormalized = response.PhoneNormalized,
            IsLocalDuplicate = response.Status == ResponseStatus.Duplicate,
            IsBitrixDuplicate = false
        };
    }
}
