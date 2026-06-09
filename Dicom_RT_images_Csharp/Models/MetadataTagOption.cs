namespace Dicom_RT_images_Csharp.Models
{
    /// <summary>
    /// A single selectable DICOM attribute presented in the metadata-tag picker. This is a
    /// plain data object (no fo-dicom types) so the Avalonia view-models can list and filter
    /// tags without referencing FellowOakDicom directly. Produced by
    /// <see cref="Services.DicomMetadataExtractor.GetSelectableTags"/>.
    /// </summary>
    public class MetadataTagOption
    {
        /// <summary>PascalCase dictionary keyword, e.g. "PatientName". Used as the JSON key on export.</summary>
        public string Keyword { get; set; } = "";

        /// <summary>Human-readable name, e.g. "Patient's Name".</summary>
        public string Name { get; set; } = "";

        /// <summary>DICOM tag group (e.g. 0x0010 for the patient group).</summary>
        public ushort Group { get; set; }

        /// <summary>DICOM tag element (e.g. 0x0010 for Patient's Name).</summary>
        public ushort Element { get; set; }

        /// <summary>Value-representation code, e.g. "PN", "DS", "US".</summary>
        public string VrCode { get; set; } = "";

        /// <summary>Tag rendered as "(gggg,eeee)", e.g. "(0010,0010)".</summary>
        public string TagDisplay => string.Format("({0:x4},{1:x4})", Group, Element);
    }
}
