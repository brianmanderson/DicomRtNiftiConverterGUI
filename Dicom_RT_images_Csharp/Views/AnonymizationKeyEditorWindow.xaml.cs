using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using Dicom_RT_images_Csharp.Services;

namespace Dicom_RT_images_Csharp.Views
{
    /// <summary>
    /// Anonymization key editor window for viewing and editing MRN/hash/export ID mappings.
    /// </summary>
    public partial class AnonymizationKeyEditorWindow : Window
    {
        private string _keyFilePath;
        private readonly ObservableCollection<MappingItem> _mappings;
        private bool _hasChanges = false;

        public AnonymizationKeyEditorWindow(string keyFilePath)
        {
            InitializeComponent();

            _mappings = new ObservableCollection<MappingItem>();
            _mappings.CollectionChanged += (s, e) => _hasChanges = true;
            MappingsDataGrid.ItemsSource = _mappings;

            SetKeyFilePath(keyFilePath);
            LoadMappings();
        }

        /// <summary>
        /// The current key file path (may be changed by user via Change Folder).
        /// </summary>
        public string SelectedKeyFilePath
        {
            get { return _keyFilePath; }
        }

        private void SetKeyFilePath(string path)
        {
            _keyFilePath = path;
            KeyFilePathTextBox.Text = path;
            UpdateFileStatus();
        }

        private void UpdateFileStatus()
        {
            if (File.Exists(_keyFilePath))
            {
                FileStatusTextBlock.Text = string.Format("File exists - {0} mapping(s) loaded", _mappings.Count);
                FileStatusTextBlock.Foreground = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#4CAF50"));
            }
            else
            {
                FileStatusTextBlock.Text = "No key file found - a new file will be created on save";
                FileStatusTextBlock.Foreground = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FF9800"));
            }
        }

        private void LoadMappings()
        {
            _mappings.Clear();

            var keyFile = AnonymizationService.LoadKeyFile(_keyFilePath);
            if (keyFile != null && keyFile.Entries != null)
            {
                foreach (var kvp in keyFile.Entries.OrderBy(e => e.Value.ExportID))
                {
                    var item = new MappingItem
                    {
                        MRN = kvp.Value.MRN ?? "",
                        StudyUID = kvp.Value.StudyUID ?? "",
                        SeriesUID = kvp.Value.SeriesUID ?? "",
                        HashString = kvp.Key,
                        ExportID = kvp.Value.ExportID
                    };
                    item.PropertyChanged += MappingItem_PropertyChanged;
                    _mappings.Add(item);
                }
            }

            _hasChanges = false;
            UpdateFileStatus();
        }

        private void MappingItem_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            _hasChanges = true;
        }

        private void ChangeFolderButton_Click(object sender, RoutedEventArgs e)
        {
            if (_hasChanges)
            {
                var result = MessageBox.Show(
                    "You have unsaved changes. Discard them and change folder?",
                    "Unsaved Changes",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (result != MessageBoxResult.Yes)
                    return;
            }

            var dialog = new Microsoft.Win32.OpenFileDialog();
            dialog.Title = "Select folder for AnonymizationKey.json";
            dialog.ValidateNames = false;
            dialog.CheckFileExists = false;
            dialog.CheckPathExists = true;
            dialog.FileName = "Select Folder";

            string currentDir = Path.GetDirectoryName(_keyFilePath);
            if (!string.IsNullOrEmpty(currentDir) && Directory.Exists(currentDir))
            {
                dialog.InitialDirectory = currentDir;
            }

            if (dialog.ShowDialog() == true)
            {
                string folder = Path.GetDirectoryName(dialog.FileName);
                if (!string.IsNullOrEmpty(folder))
                {
                    SetKeyFilePath(Path.Combine(folder, "AnonymizationKey.json"));
                    LoadMappings();
                }
            }
        }

        private void AddMappingButton_Click(object sender, RoutedEventArgs e)
        {
            int nextId = _mappings.Count > 0 ? _mappings.Max(m => m.ExportID) + 1 : 0;
            var item = new MappingItem
            {
                MRN = "NEW_MRN",
                StudyUID = "",
                SeriesUID = "",
                HashString = "NEW_HASH",
                ExportID = nextId
            };
            item.PropertyChanged += MappingItem_PropertyChanged;
            _mappings.Add(item);
            _hasChanges = true;
        }

        private void DeleteMappingButton_Click(object sender, RoutedEventArgs e)
        {
            var selected = MappingsDataGrid.SelectedItem as MappingItem;
            if (selected == null)
            {
                MessageBox.Show("Please select a mapping to delete.", "No Selection",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var result = MessageBox.Show(
                string.Format("Delete mapping for MRN '{0}' (Export ID {1})?", selected.MRN, selected.ExportID),
                "Confirm Delete",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                _mappings.Remove(selected);
                _hasChanges = true;
            }
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (SaveMappings())
            {
                DialogResult = true;
                Close();
            }
        }

        private bool SaveMappings()
        {
            // Validate: no empty MRNs
            foreach (var m in _mappings)
            {
                if (string.IsNullOrWhiteSpace(m.MRN))
                {
                    MessageBox.Show("All mappings must have a non-empty MRN.",
                        "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
                if (string.IsNullOrWhiteSpace(m.HashString))
                {
                    MessageBox.Show(string.Format("Mapping for MRN '{0}' has an empty hash string.", m.MRN),
                        "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
            }

            // Validate: no duplicate hashes
            var duplicateHashes = _mappings.GroupBy(m => m.HashString)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();

            if (duplicateHashes.Count > 0)
            {
                MessageBox.Show(string.Format("Duplicate hash strings found: {0}", string.Join(", ", duplicateHashes)),
                    "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            // Validate: no duplicate export IDs
            var duplicateIds = _mappings.GroupBy(m => m.ExportID)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();

            if (duplicateIds.Count > 0)
            {
                MessageBox.Show(string.Format("Duplicate export IDs found: {0}", string.Join(", ", duplicateIds)),
                    "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            // Build key file
            var keyFile = new AnonymizationKeyFile();
            keyFile.NextExportID = _mappings.Count > 0 ? _mappings.Max(m => m.ExportID) + 1 : 0;
            keyFile.Salt = "DicomToNifti"; // Keep the default salt

            foreach (var m in _mappings)
            {
                keyFile.Entries[m.HashString] = new AnonymizationKeyEntry
                {
                    ExportID = m.ExportID,
                    MRN = m.MRN,
                    StudyUID = m.StudyUID,
                    SeriesUID = m.SeriesUID
                };
            }

            try
            {
                AnonymizationService.SaveKeyFile(_keyFilePath, keyFile);
                _hasChanges = false;
                MessageBox.Show(string.Format("Saved {0} mapping(s) to:\n{1}", _mappings.Count, _keyFilePath),
                    "Saved", MessageBoxButton.OK, MessageBoxImage.Information);
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(string.Format("Failed to save key file:\n{0}", ex.Message),
                    "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            if (_hasChanges)
            {
                var result = MessageBox.Show(
                    "You have unsaved changes. Discard them?",
                    "Unsaved Changes",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (result != MessageBoxResult.Yes)
                    return;
            }
            DialogResult = false;
            Close();
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            if (_hasChanges && DialogResult != true && DialogResult != false)
            {
                var result = MessageBox.Show(
                    "You have unsaved changes. Discard them?",
                    "Unsaved Changes",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (result != MessageBoxResult.Yes)
                {
                    e.Cancel = true;
                }
            }
        }
    }

    /// <summary>
    /// Represents a single mapping item in the DataGrid with change notification.
    /// </summary>
    public class MappingItem : INotifyPropertyChanged
    {
        private string _mrn = "";
        private string _studyUID = "";
        private string _seriesUID = "";
        private string _hashString = "";
        private int _exportID;

        public string MRN
        {
            get { return _mrn; }
            set
            {
                if (_mrn != value)
                {
                    _mrn = value;
                    OnPropertyChanged("MRN");
                }
            }
        }

        public string StudyUID
        {
            get { return _studyUID; }
            set
            {
                if (_studyUID != value)
                {
                    _studyUID = value;
                    OnPropertyChanged("StudyUID");
                }
            }
        }

        public string SeriesUID
        {
            get { return _seriesUID; }
            set
            {
                if (_seriesUID != value)
                {
                    _seriesUID = value;
                    OnPropertyChanged("SeriesUID");
                }
            }
        }

        public string HashString
        {
            get { return _hashString; }
            set
            {
                if (_hashString != value)
                {
                    _hashString = value;
                    OnPropertyChanged("HashString");
                }
            }
        }

        public int ExportID
        {
            get { return _exportID; }
            set
            {
                if (_exportID != value)
                {
                    _exportID = value;
                    OnPropertyChanged("ExportID");
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
