using CommunityToolkit.Mvvm.ComponentModel;


namespace LeadFlow.ViewModels;

public partial class CandidateDetailsViewModel : ObservableObject
{
    [ObservableProperty]
    private CandidateResponse? currentResponse;

    public void Update(CandidateResponse? response) => CurrentResponse = response;
}
