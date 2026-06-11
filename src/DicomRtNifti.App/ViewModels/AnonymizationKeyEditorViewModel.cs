using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using CommunityToolkit.Mvvm.Input;
using DicomRtNifti.Core.Services;

namespace DicomRtNifti.App.ViewModels
{
    /// <summary>
    /// One editable input -> hash mapping shown in the anonymization-key editor.
    /// </summary>
    public class MappingRow : INotifyPropertyChanged
    {
        private string _input = "";
        private string _hash = "";

        public string Input
        {
            get => _input;
            set { _input = value; OnPropertyChanged(nameof(Input)); }
        }

        public string Hash
        {
            get => _hash;
            set { _hash = value; OnPropertyChanged(nameof(Hash)); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged(string name)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// View-model for the anonymization-key editor. Loads the three maps (MRN→PatientHash,
    /// StudyUID→StudyHash, SeriesUID→SeriesHash) from AnonymizationKey.json and lets the user view,
    /// add, delete, and override mappings — including defining a specific output hash for a given
    /// input. On the next export, <see cref="AnonymizationService"/> honors any mapping found here.
    /// </summary>
    public class AnonymizationKeyEditorViewModel : INotifyPropertyChanged
    {
        private readonly string _keyFilePath;
        private readonly string _salt;
        private string _errorText = "";

        public AnonymizationKeyEditorViewModel(string keyFilePath, string salt)
        {
            _keyFilePath = keyFilePath;
            _salt = salt ?? "DicomToNifti";

            Patients = new ObservableCollection<MappingRow>();
            Studies = new ObservableCollection<MappingRow>();
            Series = new ObservableCollection<MappingRow>();

            AddPatientCommand = new RelayCommand(() => Patients.Add(new MappingRow()));
            AddStudyCommand = new RelayCommand(() => Studies.Add(new MappingRow()));
            AddSeriesCommand = new RelayCommand(() => Series.Add(new MappingRow()));
            DeletePatientCommand = new RelayCommand(() => Remove(Patients, SelectedPatient));
            DeleteStudyCommand = new RelayCommand(() => Remove(Studies, SelectedStudy));
            DeleteSeriesCommand = new RelayCommand(() => Remove(Series, SelectedSeries));

            Load();
        }

        public string KeyFilePath => _keyFilePath;

        public string FileStatus => File.Exists(_keyFilePath)
            ? $"Editing: {_keyFilePath}"
            : $"No key file yet — a new one will be created at: {_keyFilePath}";

        public ObservableCollection<MappingRow> Patients { get; }
        public ObservableCollection<MappingRow> Studies { get; }
        public ObservableCollection<MappingRow> Series { get; }

        // Bound to each DataGrid's SelectedItem so the Delete buttons know what to remove.
        public MappingRow SelectedPatient { get; set; }
        public MappingRow SelectedStudy { get; set; }
        public MappingRow SelectedSeries { get; set; }

        public IRelayCommand AddPatientCommand { get; }
        public IRelayCommand AddStudyCommand { get; }
        public IRelayCommand AddSeriesCommand { get; }
        public IRelayCommand DeletePatientCommand { get; }
        public IRelayCommand DeleteStudyCommand { get; }
        public IRelayCommand DeleteSeriesCommand { get; }

        public string ErrorText
        {
            get => _errorText;
            set
            {
                _errorText = value;
                OnPropertyChanged(nameof(ErrorText));
                OnPropertyChanged(nameof(HasError));
            }
        }

        public bool HasError => !string.IsNullOrEmpty(_errorText);

        private static void Remove(ObservableCollection<MappingRow> rows, MappingRow row)
        {
            if (row != null) rows.Remove(row);
        }

        private void Load()
        {
            var keyFile = AnonymizationService.LoadKeyFile(_keyFilePath);
            if (keyFile == null) return;

            if (keyFile.Patients != null)
                foreach (var kvp in keyFile.Patients)
                    Patients.Add(new MappingRow { Input = kvp.Key, Hash = kvp.Value });
            if (keyFile.Studies != null)
                foreach (var kvp in keyFile.Studies)
                    Studies.Add(new MappingRow { Input = kvp.Key, Hash = kvp.Value });
            if (keyFile.Series != null)
                foreach (var kvp in keyFile.Series)
                    Series.Add(new MappingRow { Input = kvp.Key, Hash = kvp.Value });
        }

        /// <summary>
        /// Validates and writes the key file. Returns false and sets <see cref="ErrorText"/> on a
        /// validation/write error (the window stays open).
        /// </summary>
        public bool TrySave()
        {
            if (!TryBuild(Patients, "patient", out var patients)) return false;
            if (!TryBuild(Studies, "study", out var studies)) return false;
            if (!TryBuild(Series, "series", out var series)) return false;

            var keyFile = new AnonymizationKeyFile
            {
                Salt = _salt,
                Patients = patients,
                Studies = studies,
                Series = series,
            };

            try
            {
                AnonymizationService.SaveKeyFile(_keyFilePath, keyFile);
                ErrorText = "";
                return true;
            }
            catch (Exception ex)
            {
                ErrorText = "Failed to save key file: " + ex.Message;
                return false;
            }
        }

        private bool TryBuild(ObservableCollection<MappingRow> rows, string label, out Dictionary<string, string> map)
        {
            map = new Dictionary<string, string>();
            foreach (var r in rows)
            {
                string input = (r.Input ?? "").Trim();
                string hash = (r.Hash ?? "").Trim();

                // Skip fully-blank rows the user added but never filled in.
                if (input.Length == 0 && hash.Length == 0) continue;

                if (input.Length == 0)
                {
                    ErrorText = $"A {label} mapping has an empty input value.";
                    return false;
                }
                if (hash.Length == 0)
                {
                    ErrorText = $"The {label} mapping for '{input}' has an empty hash.";
                    return false;
                }
                if (map.ContainsKey(input))
                {
                    ErrorText = $"Duplicate {label} input '{input}'. Each input may map to only one hash.";
                    return false;
                }
                map[input] = hash;
            }
            return true;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged(string name)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
