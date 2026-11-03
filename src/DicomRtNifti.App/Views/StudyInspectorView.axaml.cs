using Avalonia.Controls;

namespace DicomRtNifti.App.Views
{
    /// <summary>
    /// The study inspector panel of the forward window. DataContext is the
    /// <see cref="ViewModels.StudyInspectorViewModel"/> of the study selected in the tree.
    /// </summary>
    public partial class StudyInspectorView : UserControl
    {
        public StudyInspectorView() => InitializeComponent();
    }
}
