using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Dicom_RT_images_Csharp.Models;

namespace Dicom_RT_images_Csharp.ViewModels
{
    /// <summary>
    /// ViewModel for the ROI selection window. Shows all unique canonical ROI names
    /// (after resolving associations) with checkboxes, search filtering, and a summary
    /// panel with patient counts. Ported from WPF unchanged except for dropping the
    /// unused SelectAllCommand (the "Select All" checkbox binds AllSelected directly).
    /// </summary>
    public class RoiSelectionViewModel : INotifyPropertyChanged
    {
        private bool _allSelected = true;
        private string _searchText = "";
        private readonly List<RoiSelectionItem> _allItems;

        public RoiSelectionViewModel(
            List<string> discoveredRoiNames,
            List<RoiAssociation> associations,
            HashSet<string> previouslySelected,
            Dictionary<string, List<string>> perPatientRoiNames)
        {
            _allItems = new List<RoiSelectionItem>();
            FilteredRoiItems = new ObservableCollection<RoiSelectionItem>();
            SelectedSummaryItems = new ObservableCollection<RoiSummaryItem>();

            var canonicalNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rawName in discoveredRoiNames)
            {
                string resolved = ResolveToCanonical(rawName, associations);
                canonicalNames.Add(resolved);
            }

            var patientCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (perPatientRoiNames != null)
            {
                foreach (var kvp in perPatientRoiNames)
                {
                    var resolvedForPatient = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var rawRoi in kvp.Value)
                        resolvedForPatient.Add(ResolveToCanonical(rawRoi, associations));

                    foreach (var canonical in resolvedForPatient)
                    {
                        if (!patientCounts.ContainsKey(canonical))
                            patientCounts[canonical] = 0;
                        patientCounts[canonical]++;
                    }
                }
            }

            int totalPatients = perPatientRoiNames?.Count ?? 0;

            foreach (var name in canonicalNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            {
                bool isSelected = previouslySelected == null || previouslySelected.Contains(name);
                patientCounts.TryGetValue(name, out int count);

                var item = new RoiSelectionItem(name, isSelected, count, totalPatients);
                item.PropertyChanged += Item_PropertyChanged;
                _allItems.Add(item);
            }

            ApplyFilter();
            UpdateAllSelectedState();
            UpdateSummary();
        }

        public ObservableCollection<RoiSelectionItem> FilteredRoiItems { get; }

        public ObservableCollection<RoiSummaryItem> SelectedSummaryItems { get; }

        public bool AllSelected
        {
            get { return _allSelected; }
            set
            {
                if (_allSelected != value)
                {
                    _allSelected = value;
                    OnPropertyChanged();
                    foreach (var item in _allItems)
                        item.IsSelected = value;
                }
            }
        }

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

        /// <summary>Gets the set of canonical ROI names that the user selected.</summary>
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
                    SelectedSummaryItems.Add(new RoiSummaryItem(
                        item.CanonicalName, item.PatientCount, item.TotalPatients));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    /// <summary>A single ROI name with a selection checkbox, patient count, and total patients.</summary>
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

        public string CanonicalName { get; }
        public int PatientCount { get; }
        public int TotalPatients { get; }

        public string DisplayText => string.Format("{0}  ({1}/{2})", CanonicalName, PatientCount, TotalPatients);

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
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    /// <summary>Read-only summary item for the right panel.</summary>
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
        public string DisplayText => string.Format("{0}/{1} patients", PatientCount, TotalPatients);
    }
}
