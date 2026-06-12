using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Core.Services;

namespace DicomRtNifti.App.ViewModels
{
    /// <summary>
    /// ViewModel for the metadata-tag picker window. Aggregates the three tabs — Images,
    /// Structures, Dose — each a <see cref="MetadataTagSectionViewModel"/> seeded with that
    /// modality's curated keywords and computed options plus the user's previous selections.
    /// On confirm the window reads each section's <see cref="MetadataTagSectionViewModel.GetSelectedKeywords"/>.
    /// </summary>
    public class MetadataTagSelectionViewModel
    {
        public MetadataTagSelectionViewModel(
            IReadOnlyList<string> imageKeywords,
            IReadOnlyList<string> structureKeywords,
            IReadOnlyList<string> doseKeywords)
        {
            ImagesSection = new MetadataTagSectionViewModel(
                MetadataTagCatalog.CuratedImageKeywords,
                MetadataTagCatalog.ImageComputedOptions, imageKeywords);

            StructuresSection = new MetadataTagSectionViewModel(
                MetadataTagCatalog.CuratedStructureKeywords,
                MetadataTagCatalog.StructureComputedOptions, structureKeywords);

            DoseSection = new MetadataTagSectionViewModel(
                MetadataTagCatalog.CuratedDoseKeywords,
                MetadataTagCatalog.DoseComputedOptions, doseKeywords);
        }

        public MetadataTagSectionViewModel ImagesSection { get; }
        public MetadataTagSectionViewModel StructuresSection { get; }
        public MetadataTagSectionViewModel DoseSection { get; }
    }

    /// <summary>
    /// A single selectable item in a picker tab: either a raw DICOM attribute or a computed
    /// (derived) value. Computed items carry a friendly name + description and render a "computed"
    /// badge instead of a tag number.
    /// </summary>
    public class MetadataTagItem : INotifyPropertyChanged
    {
        private bool _isSelected;

        /// <summary>Constructs a raw DICOM tag item.</summary>
        public MetadataTagItem(MetadataTagOption option, bool isSelected)
        {
            Keyword = option.Keyword;
            Name = option.Name ?? "";
            VrCode = option.VrCode ?? "";
            TagDisplay = option.TagDisplay;
            FriendlyName = MetadataTagCatalog.FriendlyName(option.Keyword);
            Description = "";
            IsComputed = false;
            _isSelected = isSelected;
        }

        /// <summary>Constructs a computed (derived) value item; always part of the curated set.</summary>
        public MetadataTagItem(ComputedMetadataOption option, bool isSelected)
        {
            Keyword = option.Key;
            Name = option.Description ?? "";
            VrCode = "";
            TagDisplay = "";
            FriendlyName = option.FriendlyName ?? "";
            Description = option.Description ?? "";
            IsComputed = true;
            IsCurated = true;
            _isSelected = isSelected;
        }

        /// <summary>Settings/extraction key: a DICOM keyword, or an "@..." computed key.</summary>
        public string Keyword { get; }
        public string Name { get; }
        public string VrCode { get; }
        public string TagDisplay { get; }

        /// <summary>Friendly name used as the JSON key on export (and shown in the summary).</summary>
        public string FriendlyName { get; }
        public string Description { get; }
        public bool IsComputed { get; }

        /// <summary>True when this item belongs to its tab's curated list (shown without "Show all tags").</summary>
        public bool IsCurated { get; internal set; }

        public string DisplayText => IsComputed
            ? $"{FriendlyName} — {Description}  [computed]"
            : (string.IsNullOrEmpty(Name)
                ? $"{Keyword}  {TagDisplay} [{VrCode}]"
                : $"{Keyword} — {Name}  {TagDisplay} [{VrCode}]");

        /// <summary>Right-hand badge in the summary: the tag number, or "computed" for derived values.</summary>
        public string Badge => IsComputed ? "computed" : TagDisplay;

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
