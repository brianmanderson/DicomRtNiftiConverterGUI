using Avalonia.Controls;

namespace DicomRtNifti.App.Views
{
    /// <summary>
    /// One tab of the metadata-tag picker (Images / Structures / Dose). Its DataContext is a
    /// <see cref="ViewModels.MetadataTagSectionViewModel"/> bound by the parent window's TabControl.
    /// </summary>
    public partial class MetadataTagSectionView : UserControl
    {
        public MetadataTagSectionView() => InitializeComponent();
    }
}
