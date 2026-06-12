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
    /// One tab of the metadata-tag picker (Images, Structures, or Dose). Lists that modality's
    /// curated DICOM tags plus its computed values (e.g. "@VoxelSize"); a "Show all tags" toggle
    /// widens the list to the full <see cref="DicomMetadataExtractor.GetSelectableTags"/> dictionary.
    /// Selections made while showing all tags stay visible after toggling back (curated OR selected).
    /// Carries the search box, "select all (filtered)" header, and live summary of the former
    /// single-list picker; the list is large, so the filtered collection is rebuilt as a whole and
    /// the view virtualizes it via a ListBox.
    /// </summary>
    public class MetadataTagSectionViewModel : INotifyPropertyChanged
    {
        private readonly List<MetadataTagItem> _allItems;
        private bool _allSelected;
        private bool _suppressItemUpdates;
        private bool _showAllTags;
        private string _searchText = "";
        private ObservableCollection<MetadataTagItem> _filteredItems;

        public MetadataTagSectionViewModel(
            IReadOnlyList<string> curatedKeywords,
            IReadOnlyList<ComputedMetadataOption> computedOptions,
            IReadOnlyList<string> previouslySelectedKeywords)
        {
            var selected = new HashSet<string>(
                previouslySelectedKeywords ?? Array.Empty<string>(), StringComparer.Ordinal);
            var curated = new HashSet<string>(
                curatedKeywords ?? Array.Empty<string>(), StringComparer.Ordinal);

            _allItems = new List<MetadataTagItem>();

            // Computed values first (always curated, pinned to the top of the list).
            if (computedOptions != null)
            {
                foreach (var option in computedOptions)
                {
                    var item = new MetadataTagItem(option, selected.Contains(option.Key));
                    item.PropertyChanged += Item_PropertyChanged;
                    _allItems.Add(item);
                }
            }

            // All raw DICOM tags; curated ones are flagged so they show without "Show all tags".
            foreach (var option in DicomMetadataExtractor.GetSelectableTags())
            {
                var item = new MetadataTagItem(option, selected.Contains(option.Keyword));
                item.IsCurated = curated.Contains(option.Keyword);
                item.PropertyChanged += Item_PropertyChanged;
                _allItems.Add(item);
            }

            _filteredItems = new ObservableCollection<MetadataTagItem>();
            SelectedSummaryItems = new ObservableCollection<MetadataTagItem>();

            ApplyFilter();
            UpdateSummary();
        }

        /// <summary>The tags matching the current filter, virtualized by the ListBox.</summary>
        public ObservableCollection<MetadataTagItem> FilteredItems
        {
            get { return _filteredItems; }
            private set { _filteredItems = value; OnPropertyChanged(); }
        }

        /// <summary>The currently-selected tags, shown in the right-hand summary panel.</summary>
        public ObservableCollection<MetadataTagItem> SelectedSummaryItems { get; }

        /// <summary>"N selected" label for the tab header.</summary>
        public string SelectionSummaryText => $"{_allItems.Count(i => i.IsSelected)} selected";

        /// <summary>
        /// When true the list shows the entire DICOM dictionary; otherwise only this tab's curated
        /// tags plus any already-selected tags.
        /// </summary>
        public bool ShowAllTags
        {
            get { return _showAllTags; }
            set
            {
                if (_showAllTags != value)
                {
                    _showAllTags = value;
                    OnPropertyChanged();
                    ApplyFilter();
                }
            }
        }

        /// <summary>
        /// Header checkbox. Reflects whether every currently-filtered tag is selected; toggling it
        /// selects/clears only the filtered tags.
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
            IEnumerable<MetadataTagItem> source = _showAllTags
                ? _allItems
                : _allItems.Where(i => i.IsCurated || i.IsSelected);

            var filter = (_searchText ?? "").Trim();
            if (!string.IsNullOrEmpty(filter))
            {
                source = source.Where(i =>
                    i.Keyword.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    i.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    i.FriendlyName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    i.TagDisplay.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            FilteredItems = new ObservableCollection<MetadataTagItem>(source);
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
}
