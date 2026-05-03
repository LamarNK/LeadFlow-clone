using System.Windows;
using LeadFlow.ViewModels;

namespace LeadFlow.Views;

public partial class DuplicateCheckWindow : Window
{
    public DuplicateCheckWindow(DuplicateCheckViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
