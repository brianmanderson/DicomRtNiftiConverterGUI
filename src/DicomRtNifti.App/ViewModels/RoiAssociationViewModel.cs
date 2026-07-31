using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.Input;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Core.Services;

namespace DicomRtNifti.App.ViewModels
{
    /// <summary>
    /// View-model for the ROI Association editor. Ported from WPF; the editing model is unchanged
    /// (canonical names with alias sets, persisted via <see cref="SettingsService"/>). Cross-platform
    /// changes: CommunityToolkit commands instead of the WPF RelayCommand, and Import/Export file
    /// dialogs live in the window code-behind (Avalonia IStorageProvider) calling
    /// <see cref="ImportFromFile"/> / <see cref="ExportToFile"/> here, rather than Win32 dialogs in the VM.
    /// </summary>
    public class RoiAssociationViewModel : INotifyPropertyChanged
    {
        private readonly SettingsService _settingsService;

        /// <summary>
        /// Set when the existing associations file could not be read. Saving would replace it
        /// with whatever is on screen, which is not what the user has.
        /// </summary>
        private bool _saveBlocked;
        private readonly List<DiscoveredRoiName> _allDiscoveredRoiNames;
        private RoiAssociationItemViewModel _selectedAssociation;
        private string _roiSearchText = "";
        private string _newAliasText = "";
        private string _statusText = "";

        /// <param name="discoveredRoiCounts">
        /// Unique discovered ROI name -> number of series it was found in. Shown in the browser as
        /// "Name (count)"; double-clicking adds the bare Name (not the count) as an alias.
        /// </param>
        public RoiAssociationViewModel(SettingsService settingsService, IReadOnlyDictionary<string, int> discoveredRoiCounts)
        {
            _settingsService = settingsService;
            _allDiscoveredRoiNames = new List<DiscoveredRoiName>();
            if (discoveredRoiCounts != null)
                foreach (var kvp in discoveredRoiCounts.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                    _allDiscoveredRoiNames.Add(new DiscoveredRoiName(kvp.Key, kvp.Value));

            Associations = new ObservableCollection<RoiAssociationItemViewModel>();
            FilteredDiscoveredRoiNames = new ObservableCollection<DiscoveredRoiName>();

            AddAssociationCommand = new RelayCommand(AddAssociation);
            RemoveAssociationCommand = new RelayCommand(RemoveAssociation);
            SaveCommand = new RelayCommand(Save);
            AddDiscoveredNameAsAliasCommand = new RelayCommand<string>(AddDiscoveredNameAsAlias);
            AddCustomAliasCommand = new RelayCommand(AddCustomAlias);

            // An unreadable roi_associations.json must not present itself as "no associations
            // yet" — the user would hit Save and overwrite the real file with an empty list.
            try
            {
                foreach (var assoc in _settingsService.LoadAssociations())
                    Associations.Add(new RoiAssociationItemViewModel(assoc));
            }
            catch (InvalidDataException ex)
            {
                _saveBlocked = true;
                StatusText = ex.Message;
            }
            SelectedAssociation = Associations.FirstOrDefault();

            // Track every edit that can change whether a discovered name is covered (associations
            // added/removed, canonical renamed, aliases added/removed) so the warning flags stay live.
            Associations.CollectionChanged += OnAssociationsChanged;
            foreach (var item in Associations)
                HookAssociation(item);

            FilterDiscoveredRoiNames();
            RefreshDiscoveredWarnings();
        }

        /// <summary>All ROI associations being edited.</summary>
        public ObservableCollection<RoiAssociationItemViewModel> Associations { get; }

        /// <summary>Discovered ROI names (with counts) filtered by <see cref="RoiSearchText"/>.</summary>
        public ObservableCollection<DiscoveredRoiName> FilteredDiscoveredRoiNames { get; }

        public RoiAssociationItemViewModel SelectedAssociation
        {
            get => _selectedAssociation;
            set { _selectedAssociation = value; OnPropertyChanged(); }
        }

        public string RoiSearchText
        {
            get => _roiSearchText;
            set { _roiSearchText = value; OnPropertyChanged(); FilterDiscoveredRoiNames(); }
        }

        public string NewAliasText
        {
            get => _newAliasText;
            set { _newAliasText = value; OnPropertyChanged(); }
        }

        /// <summary>Transient feedback line shown in the footer (Save/Import/Export results).</summary>
        public string StatusText
        {
            get => _statusText;
            set { _statusText = value; OnPropertyChanged(); }
        }

        public IRelayCommand AddAssociationCommand { get; }
        public IRelayCommand RemoveAssociationCommand { get; }
        public IRelayCommand SaveCommand { get; }
        public IRelayCommand<string> AddDiscoveredNameAsAliasCommand { get; }
        public IRelayCommand AddCustomAliasCommand { get; }

        private void AddAssociation()
        {
            var newAssoc = new RoiAssociationItemViewModel(new RoiAssociation { CanonicalName = "NewROI" });
            Associations.Add(newAssoc);
            SelectedAssociation = newAssoc;
        }

        private void RemoveAssociation()
        {
            if (SelectedAssociation == null) return;
            Associations.Remove(SelectedAssociation);
            SelectedAssociation = Associations.FirstOrDefault();
        }

        private void AddDiscoveredNameAsAlias(string roiName)
        {
            if (string.IsNullOrEmpty(roiName) || SelectedAssociation == null) return;
            if (!SelectedAssociation.Aliases.Contains(roiName))
                SelectedAssociation.Aliases.Add(roiName);
        }

        private void AddCustomAlias()
        {
            var text = (_newAliasText ?? "").Trim();
            if (text.Length == 0 || SelectedAssociation == null) return;
            if (!SelectedAssociation.Aliases.Contains(text))
                SelectedAssociation.Aliases.Add(text);
            NewAliasText = "";
        }

        private void FilterDiscoveredRoiNames()
        {
            FilteredDiscoveredRoiNames.Clear();
            var filter = (_roiSearchText ?? "").Trim();
            foreach (var item in _allDiscoveredRoiNames)
            {
                if (string.IsNullOrEmpty(filter) ||
                    item.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                    FilteredDiscoveredRoiNames.Add(item);
            }
        }

        // --- Live "not yet mapped" warning flags on discovered names ------------------------------

        private void OnAssociationsChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null)
                foreach (RoiAssociationItemViewModel item in e.OldItems)
                    UnhookAssociation(item);
            if (e.NewItems != null)
                foreach (RoiAssociationItemViewModel item in e.NewItems)
                    HookAssociation(item);
            RefreshDiscoveredWarnings();
        }

        private void HookAssociation(RoiAssociationItemViewModel item)
        {
            item.PropertyChanged += OnAssociationItemPropertyChanged;
            item.Aliases.CollectionChanged += OnAliasesChanged;
        }

        private void UnhookAssociation(RoiAssociationItemViewModel item)
        {
            item.PropertyChanged -= OnAssociationItemPropertyChanged;
            item.Aliases.CollectionChanged -= OnAliasesChanged;
        }

        private void OnAssociationItemPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            // A canonical-name edit can change which discovered names are covered.
            if (e.PropertyName == nameof(RoiAssociationItemViewModel.CanonicalName))
                RefreshDiscoveredWarnings();
        }

        private void OnAliasesChanged(object sender, NotifyCollectionChangedEventArgs e)
            => RefreshDiscoveredWarnings();

        /// <summary>
        /// Flags each discovered name that is not covered by any association's canonical name or alias
        /// (using the same forgiving matcher the conversion uses), so the browser can warn the user.
        /// </summary>
        private void RefreshDiscoveredWarnings()
        {
            foreach (var item in _allDiscoveredRoiNames)
                item.IsUnmatched = !IsNameCovered(item.Name);
        }

        private bool IsNameCovered(string name)
        {
            foreach (var assoc in Associations)
            {
                if (RoiNameMatcher.Matches(name, assoc.CanonicalName))
                    return true;
                foreach (var alias in assoc.Aliases)
                    if (RoiNameMatcher.Matches(name, alias))
                        return true;
            }
            return false;
        }

        /// <summary>Persists the current associations to the app's roi_associations.json.</summary>
        public void Save()
        {
            if (_saveBlocked)
            {
                StatusText = "Not saved — the existing roi_associations.json could not be read " +
                             "and is being preserved. Move it aside first.";
                return;
            }

            try
            {
                _settingsService.SaveAssociations(Associations.Select(a => a.ToModel()).ToList());
                StatusText = $"Saved {Associations.Count} association(s).";
            }
            catch (Exception ex)
            {
                StatusText = "Save failed: " + ex.Message;
            }
        }

        /// <summary>Replaces the in-editor list with associations loaded from an external JSON file.</summary>
        public void ImportFromFile(string path)
        {
            try
            {
                var imported = _settingsService.ImportAssociations(path);
                Associations.Clear();
                foreach (var assoc in imported)
                    Associations.Add(new RoiAssociationItemViewModel(assoc));
                SelectedAssociation = Associations.FirstOrDefault();
                StatusText = $"Imported {Associations.Count} association(s) from {path}.";
            }
            catch (Exception ex)
            {
                StatusText = "Import failed: " + ex.Message;
            }
        }

        /// <summary>Writes the current associations to an external JSON file.</summary>
        public void ExportToFile(string path)
        {
            try
            {
                _settingsService.ExportAssociations(Associations.Select(a => a.ToModel()).ToList(), path);
                StatusText = $"Exported {Associations.Count} association(s) to {path}.";
            }
            catch (Exception ex)
            {
                StatusText = "Export failed: " + ex.Message;
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    /// <summary>
    /// View-model wrapper for a single <see cref="RoiAssociation"/> with change notification.
    /// </summary>
    public class RoiAssociationItemViewModel : INotifyPropertyChanged
    {
        private string _canonicalName;

        public RoiAssociationItemViewModel(RoiAssociation model)
        {
            _canonicalName = model.CanonicalName;
            Aliases = new ObservableCollection<string>(model.Aliases);
            RemoveAliasCommand = new RelayCommand<string>(RemoveAlias);
        }

        public string CanonicalName
        {
            get => _canonicalName;
            set { _canonicalName = value; OnPropertyChanged(); }
        }

        public ObservableCollection<string> Aliases { get; }

        public IRelayCommand<string> RemoveAliasCommand { get; }

        private void RemoveAlias(string alias)
        {
            if (alias != null) Aliases.Remove(alias);
        }

        public RoiAssociation ToModel()
            => new RoiAssociation { CanonicalName = CanonicalName, Aliases = Aliases.ToList() };

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    /// <summary>
    /// One discovered ROI name in the browser, with the number of series it was found in.
    /// <see cref="Display"/> ("Name (Count)") is what the list shows; <see cref="Name"/> is the bare
    /// value added as an alias on double-click. <see cref="IsUnmatched"/> drives the "not yet mapped"
    /// warning icon and is kept up to date by the owning view-model.
    /// </summary>
    public class DiscoveredRoiName : INotifyPropertyChanged
    {
        private bool _isUnmatched;

        public DiscoveredRoiName(string name, int count)
        {
            Name = name;
            Count = count;
        }

        public string Name { get; }
        public int Count { get; }
        public string Display => $"{Name} ({Count})";

        /// <summary>True when no association's canonical name or alias covers this discovered name.</summary>
        public bool IsUnmatched
        {
            get => _isUnmatched;
            set { if (_isUnmatched != value) { _isUnmatched = value; OnPropertyChanged(); } }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
