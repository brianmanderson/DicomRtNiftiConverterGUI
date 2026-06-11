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
    /// <summary>
    /// ViewModel for the metadata-tag picker. Lists every selectable DICOM attribute
    /// (<see cref="DicomMetadataExtractor.GetSelectableTags"/>) with a checkbox, a search box
    /// that matches keyword/name/tag, a "select all (filtered)" toggle, and a live summary of
    /// the chosen tags. Modeled on <see cref="RoiSelectionViewModel"/>; the list is large
    /// (~thousands of tags), so the filtered collection is rebuilt as a whole and the view
    /// virtualizes it via a ListBox.
    /// </summary>
    public class MetadataTagSelectionViewModel : INotifyPropertyChanged
    {
        private readonly List<MetadataTagItem> _allItems;
        private bool _allSelected;
        private bool _suppressItemUpdates;
        private string _searchText = "";
        private ObservableCollection<MetadataTagItem> _filteredItems;

        public MetadataTagSelectionViewModel(IReadOnlyList<string> previouslySelectedKeywords)
        {
            var selected = new HashSet<string>(
                previouslySelectedKeywords ?? Array.Empty<string>(), StringComparer.Ordinal);

            _allItems = new List<MetadataTagItem>();
            foreach (var option in DicomMetadataExtractor.GetSelectableTags())
            {
                var item = new MetadataTagItem(option, selected.Contains(option.Keyword));
                item.PropertyChanged += Item_PropertyChanged;
                _allItems.Add(item);
            }

            _filteredItems = new ObservableCollection<MetadataTagItem>(_allItems);
            SelectedSummaryItems = new ObservableCollection<MetadataTagItem>();

            UpdateAllSelectedState();
            UpdateSummary();
        }

        /// <summary>The tags matching the current search, virtualized by the ListBox.</summary>
        public ObservableCollection<MetadataTagItem> FilteredItems
        {
            get { return _filteredItems; }
            private set { _filteredItems = value; OnPropertyChanged(); }
        }

        /// <summary>The currently-selected tags, shown in the right-hand summary panel.</summary>
        public ObservableCollection<MetadataTagItem> SelectedSummaryItems { get; }

        /// <summary>"N selected" label for the header.</summary>
        public string SelectionSummaryText => $"{_allItems.Count(i => i.IsSelected)} selected";

        /// <summary>
        /// Header checkbox. Reflects whether every currently-filtered tag is selected; toggling it
        /// selects/clears only the filtered tags (a global select-all over all tags is not useful).
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

                    _suppressItemUpdates = true;
                    foreach (var item in FilteredItems)
                        item.IsSelected = value;
                    _suppressItemUpdates = false;

                    UpdateSummary();
                }
            }
        }

        public string SearchText
        {
            get { return _searchText; }
            set { _searchText = value; OnPropertyChanged(); ApplyFilter(); }
        }

        /// <summary>The keywords the user selected, sorted for a stable settings round-trip.</summary>
        public List<string> GetSelectedKeywords()
            => _allItems.Where(i => i.IsSelected)
                        .Select(i => i.Keyword)
                        .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                        .ToList();

        private void ApplyFilter()
        {
            var filter = (_searchText ?? "").Trim();
            IEnumerable<MetadataTagItem> matches = string.IsNullOrEmpty(filter)
                ? _allItems
                : _allItems.Where(i =>
                    i.Keyword.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    i.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    i.TagDisplay.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);

            FilteredItems = new ObservableCollection<MetadataTagItem>(matches);
            UpdateAllSelectedState();
        }

        private void Item_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (_suppressItemUpdates || e.PropertyName != nameof(MetadataTagItem.IsSelected))
                return;
            UpdateAllSelectedState();
            UpdateSummary();
        }

        private void UpdateAllSelectedState()
        {
            bool all = FilteredItems.Count > 0 && FilteredItems.All(i => i.IsSelected);
            if (_allSelected != all)
            {
                _allSelected = all;
                OnPropertyChanged(nameof(AllSelected));
            }
        }

        private void UpdateSummary()
        {
            SelectedSummaryItems.Clear();
            foreach (var item in _allItems.Where(i => i.IsSelected))
                SelectedSummaryItems.Add(item);
            OnPropertyChanged(nameof(SelectionSummaryText));
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    /// <summary>A single selectable DICOM tag with a checkbox, for the picker list and summary.</summary>
    public class MetadataTagItem : INotifyPropertyChanged
    {
        private bool _isSelected;

        public MetadataTagItem(MetadataTagOption option, bool isSelected)
        {
            Keyword = option.Keyword;
            Name = option.Name ?? "";
            VrCode = option.VrCode ?? "";
            TagDisplay = option.TagDisplay;
            _isSelected = isSelected;
        }

        public string Keyword { get; }
        public string Name { get; }
        public string VrCode { get; }
        public string TagDisplay { get; }

        public string DisplayText => string.IsNullOrEmpty(Name)
            ? $"{Keyword}  {TagDisplay} [{VrCode}]"
            : $"{Keyword} — {Name}  {TagDisplay} [{VrCode}]";

        public bool IsSelected
        {
            get { return _isSelected; }
            set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(); } }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
