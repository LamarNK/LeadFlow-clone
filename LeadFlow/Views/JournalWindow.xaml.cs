using System.Windows;
using LeadFlow.ViewModels;

namespace LeadFlow.Views;

public partial class JournalWindow : Window
{
    public JournalWindow(JournalViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.RefreshCommand.Execute(null);
    }
}
