using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Core.Services;
using FellowOakDicom;

namespace DicomRtNifti.App.ViewModels
{
    /// <summary>
    /// What the forward window's inspector panel shows for the row selected in the tree: a
    /// patient (<see cref="PatientInspectorViewModel"/>), a study (<see cref="StudyInspectorViewModel"/>)
    /// or an image series (<see cref="SeriesInspectorViewModel"/>). Each level maps the scanned
    /// model with no file access; this base owns what the three share, which is the panel header
    /// and the read-only DICOM tags tab. The tag grid is the one part that reads files (headers
    /// only), once, off the UI thread, the first time the row is inspected.
    /// </summary>
    public abstract class InspectorViewModel : INotifyPropertyChanged
    {
        /// <summary>Shown in place of a value the headers did not carry.</summary>
        public const string Missing = "—";

        /// <summary>The panel's text while no tree row is selected (bound with x:Static, so a property, not a const).</summary>
        public static string NoSelectionHint { get; } =
            "Select a patient, study or series in the tree. A patient shows its studies and what they hold; " +
            "a study lists every image series and RT object in it, including the RTSTRUCT, RTDOSE and RTPLAN " +
            "objects the tree does not show; a series shows its slice spacing, frame of reference, RT links and DICOM tags.";

        private Task _tagsLoad;
        private string _tagStatus = "";

        protected InspectorViewModel()
        {
            Tags = new ObservableCollection<InspectorTagRow>();
        }

        /// <summary>The panel header for this level: "Patient inspector", "Study inspector" or "Series inspector".</summary>
        public abstract string Header { get; }

        /// <summary>Curated tags (<see cref="MetadataTagCatalog"/>) read from the files <see cref="TagSources"/> names.</summary>
        public ObservableCollection<InspectorTagRow> Tags { get; }

        /// <summary>"Reading tags..." while loading; an error or "no tags" note afterwards; empty when rows are shown.</summary>
        public string TagStatus
        {
            get { return _tagStatus; }
            private set { _tagStatus = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasTagStatus)); }
        }
        public bool HasTagStatus => !string.IsNullOrEmpty(TagStatus);

        /// <summary>
        /// Reads the tag grid once; later calls return the same task. The file reads run on the
        /// thread pool and the rows are added on the caller's (UI) context.
        /// </summary>
        public Task EnsureTagsLoadedAsync()
        {
            if (_tagsLoad == null)
                _tagsLoad = LoadTagsAsync();
            return _tagsLoad;
        }

        /// <summary>The files the tag grid reads, one section each, in display order. Resolved from the model; no I/O.</summary>
        protected abstract IReadOnlyList<InspectorTagSource> TagSources();

        /// <summary>The tag status when every source was absent or carried none of its keywords.</summary>
        protected abstract string NoTagsText { get; }

        private async Task LoadTagsAsync()
        {
            TagStatus = "Reading tags...";
            List<InspectorTagRow> rows;
            try
            {
                var sources = TagSources();
                rows = await Task.Run(() =>
                {
                    var all = new List<InspectorTagRow>();
                    foreach (var source in sources)
                        all.AddRange(ReadSection(source.Section, source.Path, source.Keywords));
                    return all;
                }).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                TagStatus = "Could not read tags: " + ex.Message;
                return;
            }

            foreach (var row in rows)
                Tags.Add(row);
            TagStatus = rows.Count == 0 ? NoTagsText : "";
        }

        private static IEnumerable<InspectorTagRow> ReadSection(string section, string path, IReadOnlyList<string> keywords)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return Enumerable.Empty<InspectorTagRow>();

            DicomDataset dataset;
            try
            {
                dataset = DicomFile.Open(path, FileReadOption.SkipLargeTags).Dataset;
            }
            catch (Exception ex)
            {
                return new[] { new InspectorTagRow(section, "(unreadable)", "", Path.GetFileName(path) + ": " + ex.Message) };
            }
            return BuildTagRows(section, keywords, DicomMetadataExtractor.Extract(dataset, keywords));
        }

        /// <summary>
        /// One row per keyword that has a value, in catalog order, labelled with the same friendly
        /// name the metadata.json sidecar writes. Absent tags are left out rather than listed empty,
        /// so the grid shows what the file carries.
        /// </summary>
        internal static List<InspectorTagRow> BuildTagRows(
            string section, IReadOnlyList<string> keywords, IReadOnlyDictionary<string, object> values)
        {
            var rows = new List<InspectorTagRow>();
            if (keywords == null || values == null)
                return rows;

            foreach (var keyword in keywords)
            {
                object value;
                if (!values.TryGetValue(keyword, out value))
                    continue;
                string text = FormatTagValue(value);
                if (text.Length == 0)
                    continue;
                rows.Add(new InspectorTagRow(section, MetadataTagCatalog.FriendlyName(keyword), keyword, text));
            }
            return rows;
        }

        /// <summary>
        /// Renders an extracted value: numbers invariantly, multi-valued tags joined with DICOM's
        /// backslash separator, null as empty.
        /// </summary>
        internal static string FormatTagValue(object value)
        {
            if (value == null)
                return "";
            if (value is string s)
                return s.Trim();
            if (value is IEnumerable sequence)
                return string.Join("\\", sequence.Cast<object>().Select(FormatTagValue));
            if (value is IFormattable formattable)
                return formattable.ToString(null, CultureInfo.InvariantCulture);
            return value.ToString() ?? "";
        }

        /// <summary>How the scanner matched an RT object to an image series, in the words the help uses.</summary>
        internal static string LinkRuleLabel(RtLinkMatchRule rule)
        {
            switch (rule)
            {
                case RtLinkMatchRule.ReferencedSeriesUid: return "referenced series UID";
                case RtLinkMatchRule.FrameOfReferenceUid: return "frame of reference UID";
                case RtLinkMatchRule.LargestSeriesFallback: return "largest-series fallback (a guess)";
                default: return "an unrecorded rule";
            }
        }

        /// <summary>A DICOM DA value as "2024-03-05"; any other text trimmed as read; <see cref="Missing"/> when empty.</summary>
        internal static string FormatDicomDate(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return Missing;
            string trimmed = value.Trim();
            DateTime date;
            return DateTime.TryParseExact(trimmed, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)
                ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : trimmed;
        }

        // The scanner fills the list; the singular property survives for older consumers. Prefer
        // the list, and fall back to the singular one so an in-memory model built either way maps.
        internal static IReadOnlyList<DicomSeriesGroup> LinkedOrSingle(List<DicomSeriesGroup> list, DicomSeriesGroup single)
        {
            if (list != null && list.Count > 0)
                return list;
            return single != null ? new[] { single } : new DicomSeriesGroup[0];
        }

        internal static string DescribeObject(DicomSeriesGroup series)
        {
            if (series == null)
                return Missing;
            return string.IsNullOrWhiteSpace(series.SeriesDescription) ? "(no description)" : series.SeriesDescription.Trim();
        }

        internal static string FirstFile(DicomSeriesGroup series)
            => series?.FilePaths != null && series.FilePaths.Count > 0 ? series.FilePaths[0] : null;

        internal static string FormatNumber(double value)
            => value.ToString("0.###", CultureInfo.InvariantCulture);

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    /// <summary>One file the tag grid reads, the section its rows are labelled with, and the keywords it looks for.</summary>
    public sealed class InspectorTagSource
    {
        public InspectorTagSource(string section, string path, IReadOnlyList<string> keywords)
        {
            Section = section;
            Path = path;
            Keywords = keywords;
        }

        public string Section { get; }
        public string Path { get; }
        public IReadOnlyList<string> Keywords { get; }
    }

    /// <summary>One row of an inspector's tag grid.</summary>
    public class InspectorTagRow
    {
        public InspectorTagRow(string section, string attribute, string keyword, string value)
        {
            Section = section;
            Attribute = attribute;
            Keyword = keyword;
            Value = value;
        }

        public string Section { get; }
        public string Attribute { get; }
        public string Keyword { get; }
        public string Value { get; }
    }
}
