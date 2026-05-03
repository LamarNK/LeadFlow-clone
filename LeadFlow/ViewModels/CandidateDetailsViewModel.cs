using CommunityToolkit.Mvvm.ComponentModel;
using LeadFlow.Models;

namespace LeadFlow.ViewModels;

public partial class CandidateDetailsViewModel : ObservableObject
{
    [ObservableProperty]
    private CandidateResponse? currentResponse;

    public void Update(CandidateResponse? response) => CurrentResponse = response;
}
