using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.Input;
using Dicom_RT_images_Csharp.Models;
using Dicom_RT_images_Csharp.Services;

namespace Dicom_RT_images_Csharp.ViewModels
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
        private readonly List<string> _allDiscoveredRoiNames;
        private RoiAssociationItemViewModel _selectedAssociation;
        private string _roiSearchText = "";
        private string _newAliasText = "";
        private string _statusText = "";

        public RoiAssociationViewModel(SettingsService settingsService, List<string> discoveredRoiNames)
        {
            _settingsService = settingsService;
            _allDiscoveredRoiNames = discoveredRoiNames ?? new List<string>();

            Associations = new ObservableCollection<RoiAssociationItemViewModel>();
            FilteredDiscoveredRoiNames = new ObservableCollection<string>();

            AddAssociationCommand = new RelayCommand(AddAssociation);
            RemoveAssociationCommand = new RelayCommand(RemoveAssociation);
            SaveCommand = new RelayCommand(Save);
            AddDiscoveredNameAsAliasCommand = new RelayCommand<string>(AddDiscoveredNameAsAlias);
            AddCustomAliasCommand = new RelayCommand(AddCustomAlias);

            foreach (var assoc in _settingsService.LoadAssociations())
                Associations.Add(new RoiAssociationItemViewModel(assoc));
            SelectedAssociation = Associations.FirstOrDefault();

            FilterDiscoveredRoiNames();
        }

        /// <summary>All ROI associations being edited.</summary>
        public ObservableCollection<RoiAssociationItemViewModel> Associations { get; }

        /// <summary>Discovered ROI names filtered by <see cref="RoiSearchText"/>.</summary>
        public ObservableCollection<string> FilteredDiscoveredRoiNames { get; }

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
            foreach (var name in _allDiscoveredRoiNames)
            {
                if (string.IsNullOrEmpty(filter) ||
                    name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                    FilteredDiscoveredRoiNames.Add(name);
            }
        }

        /// <summary>Persists the current associations to the app's roi_associations.json.</summary>
        public void Save()
        {
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
}
