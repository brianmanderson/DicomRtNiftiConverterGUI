using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Core.Services;

namespace DicomRtNifti.App.ViewModels
{
    /// <summary>ViewModel for a patient node in the TreeView. Ported from WPF (no UI
    /// dependencies — INotifyPropertyChanged + Core models only), plus the geometry and link
    /// badges rolled up from its series.</summary>
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

            // Rolled up from the series so the badges show on a collapsed patient row too.
            var series = Studies.SelectMany(s => s.ImageSeries).ToList();
            NonUniformSeriesCount = series.Count(s => s.IsNonUniformZ);
            GuessedLinkSeriesCount = series.Count(s => s.HasGuessedLink);
        }

        public DicomPatientGroup Model { get; }
        public string DisplayName { get; }

        /// <summary>Image series of this patient badged "non-uniform Z".</summary>
        public int NonUniformSeriesCount { get; }
        public bool HasNonUniformSeries => NonUniformSeriesCount > 0;
        public string NonUniformBadgeText => NonUniformSeriesCount == 1
            ? "non-uniform Z"
            : $"{NonUniformSeriesCount} non-uniform Z";

        /// <summary>Image series of this patient badged "link guessed".</summary>
        public int GuessedLinkSeriesCount { get; }
        public bool HasGuessedLinkSeries => GuessedLinkSeriesCount > 0;
        public string GuessedLinkBadgeText => GuessedLinkSeriesCount == 1
            ? "link guessed"
            : $"{GuessedLinkSeriesCount} links guessed";

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
        private SeriesInspectorViewModel _inspector;

        public SeriesGroupViewModel(DicomSeriesGroup model)
        {
            Model = model;
            DisplayName = string.IsNullOrEmpty(model.SeriesDescription)
                ? $"({model.FilePaths.Count} files)"
                : $"{model.SeriesDescription} ({model.FilePaths.Count} files)";

            HasLinkedRtStruct = model.LinkedRtStruct != null;
            HasLinkedRtDose = model.LinkedRtDose != null;

            // The same test the conversions use to decide whether to warn, so a badge in the tree
            // means the log line will follow on Convert Selected and Export Manifest Only.
            string warning;
            IsNonUniformZ = SeriesGeometryProbe.TryBuildNonUniformSpacingWarning(model, out warning);
            NonUniformZWarning = warning ?? "";

            HasGuessedLink = new[] { model.LinkedRtStruct, model.LinkedRtDose }
                .Concat(model.LinkedRtStructs ?? Enumerable.Empty<DicomSeriesGroup>())
                .Concat(model.LinkedRtDoses ?? Enumerable.Empty<DicomSeriesGroup>())
                .Any(rt => rt != null && rt.LinkMatchRule == RtLinkMatchRule.LargestSeriesFallback);

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

        /// <summary>Drives the "non-uniform Z" badge on the series row.</summary>
        public bool IsNonUniformZ { get; }

        /// <summary>The conversion warning, shown as the badge's tooltip.</summary>
        public string NonUniformZWarning { get; }

        /// <summary>Drives the "link guessed" badge: an RT object was attached by the largest-series fallback.</summary>
        public bool HasGuessedLink { get; }

        /// <summary>The inspector panel's content for this series, built on first selection.</summary>
        public SeriesInspectorViewModel Inspector => _inspector ??= new SeriesInspectorViewModel(Model);

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
