using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Dicom_RT_images_Csharp.Models;

namespace Dicom_RT_images_Csharp.ViewModels
{
    /// <summary>ViewModel for a patient node in the TreeView. Ported verbatim from WPF
    /// (no UI dependencies — INotifyPropertyChanged + Core models only).</summary>
    public class PatientGroupViewModel : INotifyPropertyChanged
    {
        private bool _isExpanded;
        private bool _isSelected = true;

        public PatientGroupViewModel(DicomPatientGroup model)
        {
            Model = model;
            DisplayName = string.IsNullOrEmpty(model.PatientName)
                ? model.PatientID
                : $"{model.PatientID} - {model.PatientName}";

            Studies = new ObservableCollection<StudyGroupViewModel>(
                model.Studies.Select(s => new StudyGroupViewModel(s)));
        }

        public DicomPatientGroup Model { get; }
        public string DisplayName { get; }

        /// <summary>Whether this patient is selected for export. Propagates to all child series.</summary>
        public bool IsSelected
        {
            get { return _isSelected; }
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    OnPropertyChanged();
                    foreach (var study in Studies)
                        foreach (var series in study.ImageSeries)
                            series.IsSelected = value;
                }
            }
        }

        public bool IsExpanded
        {
            get { return _isExpanded; }
            set { _isExpanded = value; OnPropertyChanged(); }
        }

        public ObservableCollection<StudyGroupViewModel> Studies { get; }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    /// <summary>ViewModel for a study node in the TreeView.</summary>
    public class StudyGroupViewModel : INotifyPropertyChanged
    {
        private bool _isExpanded;

        public StudyGroupViewModel(DicomStudyGroup model)
        {
            Model = model;
            DisplayName = string.IsNullOrEmpty(model.StudyDescription)
                ? $"Study {model.StudyDate}"
                : $"{model.StudyDate} - {model.StudyDescription}";

            ImageSeries = new ObservableCollection<SeriesGroupViewModel>(
                model.Series
                    .Where(s => s.Modality == "CT" || s.Modality == "MR" || s.Modality == "PT")
                    .Select(s => new SeriesGroupViewModel(s)));
        }

        public DicomStudyGroup Model { get; }
        public string DisplayName { get; }

        public bool IsExpanded
        {
            get { return _isExpanded; }
            set { _isExpanded = value; OnPropertyChanged(); }
        }

        public ObservableCollection<SeriesGroupViewModel> ImageSeries { get; }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    /// <summary>ViewModel for a series node in the TreeView, with a selection checkbox.</summary>
    public class SeriesGroupViewModel : INotifyPropertyChanged
    {
        private bool _isSelected = true;
        private bool _isExpanded;

        public SeriesGroupViewModel(DicomSeriesGroup model)
        {
            Model = model;
            DisplayName = string.IsNullOrEmpty(model.SeriesDescription)
                ? $"({model.FilePaths.Count} files)"
                : $"{model.SeriesDescription} ({model.FilePaths.Count} files)";

            HasLinkedRtStruct = model.LinkedRtStruct != null;
            HasLinkedRtDose = model.LinkedRtDose != null;

            RoiNames = new ObservableCollection<string>();
            if (model.LinkedRtStruct != null)
                foreach (var name in model.LinkedRtStruct.RoiNames)
                    RoiNames.Add(name);

            if (model.LinkedRtDose != null)
            {
                string desc = string.IsNullOrWhiteSpace(model.LinkedRtDose.SeriesDescription)
                    ? "(no description)"
                    : model.LinkedRtDose.SeriesDescription.Trim();
                int frames = model.LinkedRtDose.FilePaths != null ? model.LinkedRtDose.FilePaths.Count : 0;
                LinkedRtDoseLabel = frames > 1 ? $"{desc} ({frames} files)" : desc;
            }
        }

        public DicomSeriesGroup Model { get; }
        public string DisplayName { get; }
        public bool HasLinkedRtStruct { get; }
        public bool HasLinkedRtDose { get; }
        public string LinkedRtDoseLabel { get; }
        public ObservableCollection<string> RoiNames { get; }

        public bool IsSelected
        {
            get { return _isSelected; }
            set { _isSelected = value; OnPropertyChanged(); }
        }

        public bool IsExpanded
        {
            get { return _isExpanded; }
            set { _isExpanded = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
