using Avalonia.Controls;

namespace DicomRtNifti.App.Views
{
    /// <summary>
    /// The patient inspector panel of the forward window. DataContext is the
    /// <see cref="ViewModels.PatientInspectorViewModel"/> of the patient selected in the tree.
    /// </summary>
    public partial class PatientInspectorView : UserControl
    {
        public PatientInspectorView() => InitializeComponent();
    }
}
