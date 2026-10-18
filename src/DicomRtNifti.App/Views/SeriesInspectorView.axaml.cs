using Avalonia.Controls;

namespace DicomRtNifti.App.Views
{
    /// <summary>
    /// The series inspector panel of the forward window. DataContext is the
    /// <see cref="ViewModels.SeriesInspectorViewModel"/> of the series selected in the tree.
    /// </summary>
    public partial class SeriesInspectorView : UserControl
    {
        public SeriesInspectorView() => InitializeComponent();
    }
}
