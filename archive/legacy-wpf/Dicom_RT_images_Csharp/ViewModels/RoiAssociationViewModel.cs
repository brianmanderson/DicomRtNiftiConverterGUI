using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Dicom_RT_images_Csharp.Models;
using Dicom_RT_images_Csharp.Services;
using Microsoft.Win32;

namespace Dicom_RT_images_Csharp.ViewModels
{
    /// <summary>
    /// ViewModel for the ROI Association editor window with discovered ROI name browsing.
    /// </summary>
    public class RoiAssociationViewModel : INotifyPropertyChanged
    {
        private readonly SettingsService _settingsService;
        private readonly List<string> _allDiscoveredRoiNames;
        private RoiAssociationItemViewModel _selectedAssociation;
        private string _roiSearchText = "";
        private string _newAliasText = "";

        /// <summary>
        /// Creates a new RoiAssociationViewModel with discovered ROI names from the scan.
        /// </summary>
        public RoiAssociationViewModel(SettingsService settingsService, List<string> discoveredRoiNames)
        {
            _settingsService = settingsService;
            _allDiscoveredRoiNames = discoveredRoiNames ?? new List<string>();

            Associations = new ObservableCollection<RoiAssociationItemViewModel>();
            FilteredDiscoveredRoiNames = new ObservableCollection<string>();

            AddAssociationCommand = new RelayCommand(_ => AddAssociation());
            RemoveAssociationCommand = new RelayCommand(_ => RemoveAssociation(), _ => SelectedAssociation != null);
            SaveCommand = new RelayCommand(_ => Save());
            ImportCommand = new RelayCommand(_ => Import());
            ExportCommand = new RelayCommand(_ => Export());
            AddDiscoveredNameAsAliasCommand = new RelayCommand(param => AddDiscoveredNameAsAlias(param));
            AddCustomAliasCommand = new RelayCommand(_ => AddCustomAlias());

            // Load existing associations
            var loaded = _settingsService.LoadAssociations();
            foreach (var assoc in loaded)
            {
                Associations.Add(new RoiAssociationItemViewModel(assoc));
            }

            // Initialize discovered names list
            FilterDiscoveredRoiNames();
        }

        /// <summary>
        /// All ROI associations.
        /// </summary>
        public ObservableCollection<RoiAssociationItemViewModel> Associations { get; }

        /// <summary>
        /// Discovered ROI names filtered by the search text.
        /// </summary>
        public ObservableCollection<string> FilteredDiscoveredRoiNames { get; }

        /// <summary>
        /// The currently selected association in the list.
        /// </summary>
        public RoiAssociationItemViewModel SelectedAssociation
        {
            get { return _selectedAssociation; }
            set { _selectedAssociation = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Search/filter text for the discovered ROI names panel.
        /// </summary>
        public string RoiSearchText
        {
            get { return _roiSearchText; }
            set
            {
                _roiSearchText = value;
                OnPropertyChanged();
                FilterDiscoveredRoiNames();
            }
        }

        /// <summary>
        /// Text for adding a custom alias (typed by the user).
        /// </summary>
        public string NewAliasText
        {
            get { return _newAliasText; }
            set { _newAliasText = value; OnPropertyChanged(); }
        }

        public ICommand AddAssociationCommand { get; }
        public ICommand RemoveAssociationCommand { get; }
        public ICommand SaveCommand { get; }
        public ICommand ImportCommand { get; }
        public ICommand ExportCommand { get; }

        /// <summary>
        /// Command to add a discovered ROI name as an alias to the selected association.
        /// </summary>
        public ICommand AddDiscoveredNameAsAliasCommand { get; }

        /// <summary>
        /// Command to add a custom-typed alias to the selected association.
        /// </summary>
        public ICommand AddCustomAliasCommand { get; }

        private void AddAssociation()
        {
            var newAssoc = new RoiAssociationItemViewModel(new RoiAssociation
            {
                CanonicalName = "NewROI"
            });
            Associations.Add(newAssoc);
            SelectedAssociation = newAssoc;
        }

        private void RemoveAssociation()
        {
            if (SelectedAssociation != null)
            {
                Associations.Remove(SelectedAssociation);
                SelectedAssociation = Associations.FirstOrDefault();
            }
        }

        private void AddDiscoveredNameAsAlias(object param)
        {
            var roiName = param as string;
            if (roiName != null && SelectedAssociation != null)
            {
                if (!SelectedAssociation.Aliases.Contains(roiName))
                {
                    SelectedAssociation.Aliases.Add(roiName);
                }
            }
        }

        private void AddCustomAlias()
        {
            var text = (_newAliasText ?? "").Trim();
            if (!string.IsNullOrEmpty(text) && SelectedAssociation != null)
            {
                if (!SelectedAssociation.Aliases.Contains(text))
                {
                    SelectedAssociation.Aliases.Add(text);
                }
                NewAliasText = "";
            }
        }

        private void FilterDiscoveredRoiNames()
        {
            FilteredDiscoveredRoiNames.Clear();
            var filter = (_roiSearchText ?? "").Trim();
            foreach (var name in _allDiscoveredRoiNames)
            {
                if (string.IsNullOrEmpty(filter) ||
                    name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    FilteredDiscoveredRoiNames.Add(name);
                }
            }
        }

        private void Save()
        {
            var models = Associations.Select(a => a.ToModel()).ToList();
            _settingsService.SaveAssociations(models);
        }

        private void Import()
        {
            var dlg = new OpenFileDialog
            {
                Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
                Title = "Import ROI Associations"
            };
            if (dlg.ShowDialog() == true)
            {
                try
                {
                    var imported = _settingsService.ImportAssociations(dlg.FileName);
                    Associations.Clear();
                    foreach (var assoc in imported)
                    {
                        Associations.Add(new RoiAssociationItemViewModel(assoc));
                    }
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show($"Import failed: {ex.Message}", "Error",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                }
            }
        }

        private void Export()
        {
            var dlg = new SaveFileDialog
            {
                Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
                Title = "Export ROI Associations",
                FileName = "roi_associations.json"
            };
            if (dlg.ShowDialog() == true)
            {
                try
                {
                    var models = Associations.Select(a => a.ToModel()).ToList();
                    _settingsService.ExportAssociations(models, dlg.FileName);
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show($"Export failed: {ex.Message}", "Error",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                }
            }
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

    /// <summary>
    /// ViewModel wrapper for a single RoiAssociation with INotifyPropertyChanged support.
    /// </summary>
    public class RoiAssociationItemViewModel : INotifyPropertyChanged
    {
        private string _canonicalName;

        /// <summary>
        /// Creates a new item ViewModel from a model.
        /// </summary>
        public RoiAssociationItemViewModel(RoiAssociation model)
        {
            _canonicalName = model.CanonicalName;
            Aliases = new ObservableCollection<string>(model.Aliases);

            RemoveAliasCommand = new RelayCommand(param => RemoveAlias(param as string));
        }

        /// <summary>
        /// The canonical (standardized) name for this ROI.
        /// </summary>
        public string CanonicalName
        {
            get { return _canonicalName; }
            set { _canonicalName = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Alternative names that map to this canonical name.
        /// </summary>
        public ObservableCollection<string> Aliases { get; }

        /// <summary>
        /// Command to remove an alias from the list.
        /// </summary>
        public ICommand RemoveAliasCommand { get; }

        private void RemoveAlias(string alias)
        {
            if (alias != null)
            {
                Aliases.Remove(alias);
            }
        }

        /// <summary>
        /// Converts this ViewModel back to a model for persistence.
        /// </summary>
        public RoiAssociation ToModel()
        {
            return new RoiAssociation
            {
                CanonicalName = CanonicalName,
                Aliases = Aliases.ToList()
            };
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
