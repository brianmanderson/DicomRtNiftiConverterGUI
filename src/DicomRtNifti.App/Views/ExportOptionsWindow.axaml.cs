using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DicomRtNifti.App.Views
{
    /// <summary>
    /// Non-modal Export Options window. Shares the forward window's MainViewModel (set as its
    /// DataContext by the caller) so every toggle and button drives the same export state. Holds
    /// the ROI-filtering, output-spacing, and anonymization controls that used to live in the
    /// right-hand side panel; the Data-to-Export checkboxes stay on the main window. Opened via
    /// Show() so it can sit alongside the main window while the user keeps working.
    /// </summary>
    public partial class ExportOptionsWindow : Window
    {
        public ExportOptionsWindow() => InitializeComponent();

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    }
}
