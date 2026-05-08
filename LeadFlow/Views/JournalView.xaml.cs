using System.Windows.Controls;
using LeadFlow.Logging.Audit;
using LeadFlow.ViewModels;

namespace LeadFlow.Views;

public partial class JournalView : System.Windows.Controls.UserControl
{
    public JournalView()
    {
        InitializeComponent();
    }

    private void JournalLogsListBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not JournalViewModel vm || sender is not ListBox listBox)
        {
            return;
        }

        var selected = listBox.SelectedItems.OfType<LogFileEntry>().ToList();
        vm.UpdateSelectedLogEntries(selected);
    }
}
