using System;
using System.Collections.Generic;
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
        private PatientInspectorViewModel _inspector;

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

        /// <summary>The inspector panel's content for this patient, built on first selection.</summary>
        public PatientInspectorViewModel Inspector => _inspector ??= new PatientInspectorViewModel(Model);

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
        private StudyInspectorViewModel _inspector;

        public StudyGroupViewModel(DicomStudyGroup model)
        {
            Model = model;
            DisplayName = string.IsNullOrEmpty(model.StudyDescription)
                ? $"Study {model.StudyDate}"
                : $"{model.StudyDate} - {model.StudyDescription}";

            ImageSeries = new ObservableCollection<SeriesGroupViewModel>(
                model.Series
                    .Where(IsImageSeries)
                    .Select(s => new SeriesGroupViewModel(s)));
        }

        /// <summary>
        /// The series the tree lists, the scanner links RT objects to, and Convert Selected
        /// exports. Everything else in a study (RTSTRUCT, RTDOSE, RTPLAN, any other modality) shows
        /// only in the study inspector.
        /// </summary>
        internal static bool IsImageSeries(DicomSeriesGroup series)
            => series != null && (series.Modality == "CT" || series.Modality == "MR" || series.Modality == "PT");

        public DicomStudyGroup Model { get; }
        public string DisplayName { get; }

        /// <summary>The inspector panel's content for this study, built on first selection.</summary>
        public StudyInspectorViewModel Inspector => _inspector ??= new StudyInspectorViewModel(Model);

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

    /// <summary>
    /// One entry of a series row's structure-set picker: a linked RTSTRUCT, or (with a null
    /// <see cref="Model"/>) "Automatic", which leaves the choice to the export's own rule.
    /// </summary>
    public sealed class StructureSetChoice
    {
        public StructureSetChoice(DicomSeriesGroup model, string label)
        {
            Model = model;
            Label = label;
        }

        /// <summary>The structure set, or null for "Automatic".</summary>
        public DicomSeriesGroup Model { get; }
        public string Label { get; }
        public override string ToString() => Label;
    }

    /// <summary>ViewModel for a series node in the TreeView, with a selection checkbox.</summary>
    public class SeriesGroupViewModel : INotifyPropertyChanged
    {
        /// <summary>
        /// The picker's first entry. CohortExportService then chooses, as it does for the CLI: the
        /// structure set the Export Options structure-set filter matches, else the first linked.
        /// </summary>
        public const string AutomaticStructureSetLabel = "Automatic";

        private bool _isSelected = true;
        private bool _isExpanded;
        private SeriesInspectorViewModel _inspector;
        private StructureSetChoice _selectedStructureSet;

        public SeriesGroupViewModel(DicomSeriesGroup model)
        {
            Model = model;
            DisplayName = string.IsNullOrEmpty(model.SeriesDescription)
                ? $"({model.FilePaths.Count} files)"
                : $"{model.SeriesDescription} ({model.FilePaths.Count} files)";

            var structureSets = model.LinkedRtStructs ?? new List<DicomSeriesGroup>();
            var doses = model.LinkedRtDoses ?? new List<DicomSeriesGroup>();
            HasLinkedRtStruct = model.LinkedRtStruct != null || structureSets.Count > 0;
            HasLinkedRtDose = model.LinkedRtDose != null || doses.Count > 0;

            // The same test the conversions use to decide whether to warn, so a badge in the tree
            // means the log line will follow on Convert Selected and Export Manifest Only.
            string warning;
            IsNonUniformZ = SeriesGeometryProbe.TryBuildNonUniformSpacingWarning(model, out warning);
            NonUniformZWarning = warning ?? "";

            HasGuessedLink = AnyLinkGuessed(model);

            // Several structure sets: the row offers a choice among them (GUI-07). One or none: no
            // picker; the export takes the one there is.
            var choices = new List<StructureSetChoice>();
            if (structureSets.Count > 1)
            {
                choices.Add(new StructureSetChoice(null, AutomaticStructureSetLabel));
                var labels = structureSets.Select(StructureSetLabel).ToList();
                for (int i = 0; i < structureSets.Count; i++)
                {
                    // Two sets with one description and ROI count are told apart by their UIDs' tails.
                    string label = labels.Count(l => l == labels[i]) > 1
                        ? labels[i] + " · …" + Tail(structureSets[i].SeriesInstanceUID, 8)
                        : labels[i];
                    choices.Add(new StructureSetChoice(structureSets[i], label));
                }
            }
            StructureSetChoices = choices;
            _selectedStructureSet = choices.FirstOrDefault();

            RoiNames = new ObservableCollection<string>();
            FillRoiNames();

            // Every linked dose, as every one is exported.
            var doseLabels = new List<string>();
            foreach (var dose in doses.Count > 0 ? doses : new List<DicomSeriesGroup> { model.LinkedRtDose })
            {
                if (dose == null) continue;
                string desc = string.IsNullOrWhiteSpace(dose.SeriesDescription)
                    ? "(no description)"
                    : dose.SeriesDescription.Trim();
                int frames = dose.FilePaths != null ? dose.FilePaths.Count : 0;
                doseLabels.Add(frames > 1 ? $"{desc} ({frames} files)" : desc);
            }
            LinkedRtDoseLabel = doseLabels.Count == 0 ? null : string.Join("; ", doseLabels);
        }

        public DicomSeriesGroup Model { get; }
        public string DisplayName { get; }
        public bool HasLinkedRtStruct { get; }
        public bool HasLinkedRtDose { get; }

        /// <summary>Every linked dose's description (with its file count when it has several), joined by "; ".</summary>
        public string LinkedRtDoseLabel { get; }

        /// <summary>
        /// The ROI names of the structure set the row shows as exported: the chosen one, or the
        /// first linked when the choice is Automatic or there is nothing to choose.
        /// </summary>
        public ObservableCollection<string> RoiNames { get; }

        /// <summary>
        /// Every ROI name of every linked structure set, once each (case-insensitive, first
        /// spelling kept): what the scan discovered for this series, whichever set is chosen.
        /// </summary>
        public IReadOnlyList<string> AllRoiNames
        {
            get
            {
                var names = new List<string>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var sets = (Model.LinkedRtStructs != null && Model.LinkedRtStructs.Count > 0)
                    ? Model.LinkedRtStructs
                    : new List<DicomSeriesGroup> { Model.LinkedRtStruct };
                foreach (var rs in sets)
                    if (rs?.RoiNames != null)
                        foreach (var name in rs.RoiNames)
                            if (seen.Add(name)) names.Add(name);
                return names;
            }
        }

        /// <summary>The picker's entries, Automatic first; empty when the series has fewer than two structure sets.</summary>
        public IReadOnlyList<StructureSetChoice> StructureSetChoices { get; }

        /// <summary>True when the series has several structure sets, so its row shows the picker.</summary>
        public bool HasStructureSetChoice => StructureSetChoices.Count > 0;

        /// <summary>The picker's selection; Automatic until the user picks a structure set.</summary>
        public StructureSetChoice SelectedStructureSet
        {
            get { return _selectedStructureSet; }
            set
            {
                if (ReferenceEquals(_selectedStructureSet, value) || value == null) return;
                _selectedStructureSet = value;
                OnPropertyChanged();
                FillRoiNames();
            }
        }

        /// <summary>
        /// The structure set picked for export, or null when the export's own rule chooses
        /// (Automatic, or a series with one structure set or none).
        /// </summary>
        public DicomSeriesGroup ChosenStructureSet => _selectedStructureSet?.Model;

        private void FillRoiNames()
        {
            var shown = ChosenStructureSet ?? Model.LinkedRtStruct
                ?? (Model.LinkedRtStructs != null && Model.LinkedRtStructs.Count > 0 ? Model.LinkedRtStructs[0] : null);
            RoiNames.Clear();
            if (shown?.RoiNames != null)
                foreach (var name in shown.RoiNames)
                    RoiNames.Add(name);
        }

        private static string StructureSetLabel(DicomSeriesGroup rs)
        {
            string desc = string.IsNullOrWhiteSpace(rs.SeriesDescription) ? "(no description)" : rs.SeriesDescription.Trim();
            int count = rs.RoiNames?.Count ?? 0;
            return $"{desc} ({count} ROI{(count == 1 ? "" : "s")})";
        }

        private static string Tail(string value, int length) =>
            string.IsNullOrEmpty(value) ? "" : value.Substring(Math.Max(0, value.Length - length));

        /// <summary>Drives the "non-uniform Z" badge on the series row.</summary>
        public bool IsNonUniformZ { get; }

        /// <summary>The conversion warning, shown as the badge's tooltip.</summary>
        public string NonUniformZWarning { get; }

        /// <summary>Drives the "link guessed" badge: an RT object was attached by the largest-series fallback.</summary>
        public bool HasGuessedLink { get; }

        /// <summary>The inspector panel's content for this series, built on first selection.</summary>
        public SeriesInspectorViewModel Inspector => _inspector ??= new SeriesInspectorViewModel(Model);

        /// <summary>
        /// The "link guessed" test: an RTSTRUCT or RTDOSE linked to <paramref name="series"/> was
        /// attached by the largest-series fallback. Shared with the patient and study inspectors so
        /// their counts are the tree's.
        /// </summary>
        internal static bool AnyLinkGuessed(DicomSeriesGroup series)
            => new[] { series.LinkedRtStruct, series.LinkedRtDose }
                .Concat(series.LinkedRtStructs ?? Enumerable.Empty<DicomSeriesGroup>())
                .Concat(series.LinkedRtDoses ?? Enumerable.Empty<DicomSeriesGroup>())
                .Any(rt => rt != null && rt.LinkMatchRule == RtLinkMatchRule.LargestSeriesFallback);

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
