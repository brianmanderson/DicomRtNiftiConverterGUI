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
    /// ViewModel for the ROI Association editor window.
    /// </summary>
    public class RoiAssociationViewModel : INotifyPropertyChanged
    {
        private readonly SettingsService _settingsService;
        private RoiAssociationItemViewModel _selectedAssociation;

        /// <summary>
        /// Creates a new RoiAssociationViewModel and loads existing associations.
        /// </summary>
        public RoiAssociationViewModel(SettingsService settingsService)
        {
            _settingsService = settingsService;

            Associations = new ObservableCollection<RoiAssociationItemViewModel>();

            AddAssociationCommand = new RelayCommand(_ => AddAssociation());
            RemoveAssociationCommand = new RelayCommand(_ => RemoveAssociation(), _ => SelectedAssociation != null);
            SaveCommand = new RelayCommand(_ => Save());
            ImportCommand = new RelayCommand(_ => Import());
            ExportCommand = new RelayCommand(_ => Export());

            // Load existing associations
            var loaded = _settingsService.LoadAssociations();
            foreach (var assoc in loaded)
            {
                Associations.Add(new RoiAssociationItemViewModel(assoc));
            }
        }

        /// <summary>
        /// All ROI associations.
        /// </summary>
        public ObservableCollection<RoiAssociationItemViewModel> Associations { get; }

        /// <summary>
        /// The currently selected association in the list.
        /// </summary>
        public RoiAssociationItemViewModel SelectedAssociation
        {
            get { return _selectedAssociation; }
            set { _selectedAssociation = value; OnPropertyChanged(); }
        }

        public ICommand AddAssociationCommand { get; }
        public ICommand RemoveAssociationCommand { get; }
        public ICommand SaveCommand { get; }
        public ICommand ImportCommand { get; }
        public ICommand ExportCommand { get; }

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
            ExtraAttributes = new ObservableCollection<KeyValuePairViewModel>(
                model.ExtraAttributes.Select(kv => new KeyValuePairViewModel(kv.Key, kv.Value)));

            AddAliasCommand = new RelayCommand(_ => AddAlias());
            RemoveAliasCommand = new RelayCommand(param => RemoveAlias(param as string), _ => Aliases.Count > 0);
            AddAttributeCommand = new RelayCommand(_ => AddAttribute());
            RemoveAttributeCommand = new RelayCommand(param => RemoveAttribute(param as KeyValuePairViewModel));
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
        /// Arbitrary key-value metadata for this ROI.
        /// </summary>
        public ObservableCollection<KeyValuePairViewModel> ExtraAttributes { get; }

        public ICommand AddAliasCommand { get; }
        public ICommand RemoveAliasCommand { get; }
        public ICommand AddAttributeCommand { get; }
        public ICommand RemoveAttributeCommand { get; }

        private void AddAlias()
        {
            Aliases.Add("new_alias");
        }

        private void RemoveAlias(string alias)
        {
            if (alias != null)
            {
                Aliases.Remove(alias);
            }
        }

        private void AddAttribute()
        {
            ExtraAttributes.Add(new KeyValuePairViewModel("key", "value"));
        }

        private void RemoveAttribute(KeyValuePairViewModel attr)
        {
            if (attr != null)
            {
                ExtraAttributes.Remove(attr);
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
                Aliases = Aliases.ToList(),
                ExtraAttributes = ExtraAttributes.ToDictionary(a => a.Key, a => a.Value)
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

    /// <summary>
    /// ViewModel for an editable key-value pair in the ExtraAttributes DataGrid.
    /// </summary>
    public class KeyValuePairViewModel : INotifyPropertyChanged
    {
        private string _key;
        private string _value;

        /// <summary>
        /// Creates a new key-value pair.
        /// </summary>
        public KeyValuePairViewModel(string key, string value)
        {
            _key = key;
            _value = value;
        }

        /// <summary>
        /// The attribute key.
        /// </summary>
        public string Key
        {
            get { return _key; }
            set { _key = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// The attribute value.
        /// </summary>
        public string Value
        {
            get { return _value; }
            set { _value = value; OnPropertyChanged(); }
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
