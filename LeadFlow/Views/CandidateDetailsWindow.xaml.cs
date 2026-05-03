using System.Windows;
using LeadFlow.ViewModels;

namespace LeadFlow.Views;

public partial class CandidateDetailsWindow : Window
{
    public CandidateDetailsWindow(CandidateDetailsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
