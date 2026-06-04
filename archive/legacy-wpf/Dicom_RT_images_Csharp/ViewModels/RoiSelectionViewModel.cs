using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Dicom_RT_images_Csharp.Models;

namespace Dicom_RT_images_Csharp.ViewModels
{
    /// <summary>
    /// ViewModel for the ROI selection window. Shows all unique canonical ROI names
    /// (after resolving associations) with checkboxes, search filtering, and a
    /// summary panel showing selected ROIs with patient counts.
    /// </summary>
    public class RoiSelectionViewModel : INotifyPropertyChanged
    {
        private bool _allSelected = true;
        private string _searchText = "";
        private readonly List<RoiSelectionItem> _allItems;

        /// <summary>
        /// Creates the ViewModel by resolving all discovered ROI names through associations
        /// to produce a deduplicated list of canonical names with patient counts.
        /// </summary>
        /// <param name="discoveredRoiNames">Raw DICOM ROI names from all scanned RTSTRUCT files.</param>
        /// <param name="associations">Current ROI associations (may be empty).</param>
        /// <param name="previouslySelected">Previously selected ROI names to restore check state (null = all selected).</param>
        /// <param name="perPatientRoiNames">
        /// Dictionary mapping PatientID -> list of raw ROI names across all series for that patient.
        /// Used to compute how many patients have each canonical ROI.
        /// </param>
        public RoiSelectionViewModel(
            List<string> discoveredRoiNames,
            List<RoiAssociation> associations,
            HashSet<string> previouslySelected,
            Dictionary<string, List<string>> perPatientRoiNames)
        {
            _allItems = new List<RoiSelectionItem>();
            FilteredRoiItems = new ObservableCollection<RoiSelectionItem>();
            SelectedSummaryItems = new ObservableCollection<RoiSummaryItem>();
            SelectAllCommand = new RelayCommand(_ => ToggleSelectAll());

            // Resolve each discovered name to its canonical name
            var canonicalNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rawName in discoveredRoiNames)
            {
                string resolved = ResolveToCanonical(rawName, associations);
                canonicalNames.Add(resolved);
            }

            // Count how many patients have each canonical ROI
            var patientCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (perPatientRoiNames != null)
            {
                foreach (var kvp in perPatientRoiNames)
                {
                    // Resolve this patient's raw ROI names to canonical, deduplicate per patient
                    var resolvedForPatient = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var rawRoi in kvp.Value)
                    {
                        resolvedForPatient.Add(ResolveToCanonical(rawRoi, associations));
                    }

                    foreach (var canonical in resolvedForPatient)
                    {
                        if (!patientCounts.ContainsKey(canonical))
                            patientCounts[canonical] = 0;
                        patientCounts[canonical]++;
                    }
                }
            }

            int totalPatients = perPatientRoiNames?.Count ?? 0;

            // Create checkbox items sorted alphabetically
            foreach (var name in canonicalNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            {
                bool isSelected = previouslySelected == null || previouslySelected.Contains(name);
                int count = 0;
                patientCounts.TryGetValue(name, out count);

                var item = new RoiSelectionItem(name, isSelected, count, totalPatients);
                item.PropertyChanged += Item_PropertyChanged;
                _allItems.Add(item);
            }

            ApplyFilter();
            UpdateAllSelectedState();
            UpdateSummary();
        }

        /// <summary>
        /// The filtered list of ROI items shown in the left checklist.
        /// </summary>
        public ObservableCollection<RoiSelectionItem> FilteredRoiItems { get; }

        /// <summary>
        /// Summary of currently selected ROIs with patient counts, shown in the right panel.
        /// </summary>
        public ObservableCollection<RoiSummaryItem> SelectedSummaryItems { get; }

        /// <summary>
        /// Whether all items are currently selected (for the "Select All" checkbox).
        /// </summary>
        public bool AllSelected
        {
            get { return _allSelected; }
            set
            {
                if (_allSelected != value)
                {
                    _allSelected = value;
                    OnPropertyChanged();

                    // Propagate to all items (not just filtered)
                    foreach (var item in _allItems)
                    {
                        item.IsSelected = value;
                    }
                }
            }
        }

        /// <summary>
        /// Search/filter text for the ROI checklist.
        /// </summary>
        public string SearchText
        {
            get { return _searchText; }
            set
            {
                _searchText = value;
                OnPropertyChanged();
                ApplyFilter();
            }
        }

        public ICommand SelectAllCommand { get; }

        /// <summary>
        /// Gets the set of canonical ROI names that the user selected.
        /// </summary>
        public HashSet<string> GetSelectedRoiNames()
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in _allItems)
            {
                if (item.IsSelected)
                    result.Add(item.CanonicalName);
            }
            return result;
        }

        /// <summary>
        /// Resolves a raw DICOM ROI name to its canonical name using associations.
        /// If no association matches, the raw name is returned as-is.
        /// </summary>
        private string ResolveToCanonical(string rawName, List<RoiAssociation> associations)
        {
            if (associations == null || associations.Count == 0)
                return rawName;

            foreach (var assoc in associations)
            {
                if (string.Equals(assoc.CanonicalName, rawName, StringComparison.OrdinalIgnoreCase))
                    return assoc.CanonicalName;

                foreach (var alias in assoc.Aliases)
                {
                    if (string.Equals(alias, rawName, StringComparison.OrdinalIgnoreCase))
                        return assoc.CanonicalName;
                }
            }

            return rawName;
        }

        private void ApplyFilter()
        {
            FilteredRoiItems.Clear();
            var filter = (_searchText ?? "").Trim();
            foreach (var item in _allItems)
            {
                if (string.IsNullOrEmpty(filter) ||
                    item.CanonicalName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    FilteredRoiItems.Add(item);
                }
            }
        }

        private void ToggleSelectAll()
        {
            AllSelected = !AllSelected;
        }

        private void Item_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(RoiSelectionItem.IsSelected))
            {
                UpdateAllSelectedState();
                UpdateSummary();
            }
        }

        private void UpdateAllSelectedState()
        {
            bool all = _allItems.Count > 0 && _allItems.All(i => i.IsSelected);
            if (_allSelected != all)
            {
                _allSelected = all;
                OnPropertyChanged(nameof(AllSelected));
            }
        }

        private void UpdateSummary()
        {
            SelectedSummaryItems.Clear();
            foreach (var item in _allItems)
            {
                if (item.IsSelected)
                {
                    SelectedSummaryItems.Add(new RoiSummaryItem(
                        item.CanonicalName, item.PatientCount, item.TotalPatients));
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>
    /// A single ROI name with a selection checkbox, patient count, and total patients.
    /// </summary>
    public class RoiSelectionItem : INotifyPropertyChanged
    {
        private bool _isSelected;

        public RoiSelectionItem(string canonicalName, bool isSelected, int patientCount, int totalPatients)
        {
            CanonicalName = canonicalName;
            _isSelected = isSelected;
            PatientCount = patientCount;
            TotalPatients = totalPatients;
        }

        /// <summary>
        /// The canonical (resolved) ROI name.
        /// </summary>
        public string CanonicalName { get; }

        /// <summary>
        /// How many patients have this ROI (after resolving associations).
        /// </summary>
        public int PatientCount { get; }

        /// <summary>
        /// Total number of patients in the scan.
        /// </summary>
        public int TotalPatients { get; }

        /// <summary>
        /// Display string showing the count, e.g. "Brain (12/15 patients)"
        /// </summary>
        public string DisplayText
        {
            get { return string.Format("{0}  ({1}/{2})", CanonicalName, PatientCount, TotalPatients); }
        }

        /// <summary>
        /// Whether this ROI is selected for export.
        /// </summary>
        public bool IsSelected
        {
            get { return _isSelected; }
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    OnPropertyChanged();
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>
    /// Read-only summary item for the right panel showing a selected ROI and its patient count.
    /// </summary>
    public class RoiSummaryItem
    {
        public RoiSummaryItem(string canonicalName, int patientCount, int totalPatients)
        {
            CanonicalName = canonicalName;
            PatientCount = patientCount;
            TotalPatients = totalPatients;
        }

        public string CanonicalName { get; }
        public int PatientCount { get; }
        public int TotalPatients { get; }
        public string DisplayText
        {
            get { return string.Format("{0}/{1} patients", PatientCount, TotalPatients); }
        }
    }
}
