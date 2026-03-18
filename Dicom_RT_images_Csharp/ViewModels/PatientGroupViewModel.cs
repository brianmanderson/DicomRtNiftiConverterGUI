using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Dicom_RT_images_Csharp.Models;

namespace Dicom_RT_images_Csharp.ViewModels
{
    /// <summary>
    /// ViewModel for a patient node in the TreeView.
    /// </summary>
    public class PatientGroupViewModel : INotifyPropertyChanged
    {
        private bool _isExpanded;

        /// <summary>
        /// Creates a ViewModel from a patient model.
        /// </summary>
        public PatientGroupViewModel(DicomPatientGroup model)
        {
            Model = model;
            DisplayName = string.IsNullOrEmpty(model.PatientName)
                ? model.PatientID
                : $"{model.PatientID} - {model.PatientName}";

            Studies = new ObservableCollection<StudyGroupViewModel>(
                model.Studies.Select(s => new StudyGroupViewModel(s)));
        }

        /// <summary>
        /// The underlying model.
        /// </summary>
        public DicomPatientGroup Model { get; }

        /// <summary>
        /// Display string for the tree node.
        /// </summary>
        public string DisplayName { get; }

        /// <summary>
        /// Whether the tree node is expanded.
        /// </summary>
        public bool IsExpanded
        {
            get { return _isExpanded; }
            set { _isExpanded = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Child study ViewModels.
        /// </summary>
        public ObservableCollection<StudyGroupViewModel> Studies { get; }

        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>
        /// Raises the PropertyChanged event.
        /// </summary>
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>
    /// ViewModel for a study node in the TreeView.
    /// </summary>
    public class StudyGroupViewModel : INotifyPropertyChanged
    {
        private bool _isExpanded;

        /// <summary>
        /// Creates a ViewModel from a study model.
        /// </summary>
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

        /// <summary>
        /// The underlying model.
        /// </summary>
        public DicomStudyGroup Model { get; }

        /// <summary>
        /// Display string for the tree node.
        /// </summary>
        public string DisplayName { get; }

        /// <summary>
        /// Whether the tree node is expanded.
        /// </summary>
        public bool IsExpanded
        {
            get { return _isExpanded; }
            set { _isExpanded = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Child image series ViewModels (only CT/MR/PT, not RTSTRUCT/RTDOSE directly).
        /// </summary>
        public ObservableCollection<SeriesGroupViewModel> ImageSeries { get; }

        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>
        /// Raises the PropertyChanged event.
        /// </summary>
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>
    /// ViewModel for a series node in the TreeView, with a selection checkbox.
    /// </summary>
    public class SeriesGroupViewModel : INotifyPropertyChanged
    {
        private bool _isSelected = true;
        private bool _isExpanded;

        /// <summary>
        /// Creates a ViewModel from a series model.
        /// </summary>
        public SeriesGroupViewModel(DicomSeriesGroup model)
        {
            Model = model;
            DisplayName = string.IsNullOrEmpty(model.SeriesDescription)
                ? $"{model.Modality} ({model.FilePaths.Count} files)"
                : $"{model.Modality} - {model.SeriesDescription} ({model.FilePaths.Count} files)";

            HasLinkedRtStruct = model.LinkedRtStruct != null;
            HasLinkedRtDose = model.LinkedRtDose != null;

            RoiNames = new ObservableCollection<string>();
            if (model.LinkedRtStruct != null)
            {
                foreach (var name in model.LinkedRtStruct.RoiNames)
                {
                    RoiNames.Add(name);
                }
            }
        }

        /// <summary>
        /// The underlying model.
        /// </summary>
        public DicomSeriesGroup Model { get; }

        /// <summary>
        /// Display string for the tree node.
        /// </summary>
        public string DisplayName { get; }

        /// <summary>
        /// Whether an RTSTRUCT is linked to this image series.
        /// </summary>
        public bool HasLinkedRtStruct { get; }

        /// <summary>
        /// Whether an RTDOSE is linked to this image series.
        /// </summary>
        public bool HasLinkedRtDose { get; }

        /// <summary>
        /// ROI names from the linked RTSTRUCT.
        /// </summary>
        public ObservableCollection<string> RoiNames { get; }

        /// <summary>
        /// Whether this series is selected for conversion.
        /// </summary>
        public bool IsSelected
        {
            get { return _isSelected; }
            set { _isSelected = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Whether the ROI list is expanded.
        /// </summary>
        public bool IsExpanded
        {
            get { return _isExpanded; }
            set { _isExpanded = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>
        /// Raises the PropertyChanged event.
        /// </summary>
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
