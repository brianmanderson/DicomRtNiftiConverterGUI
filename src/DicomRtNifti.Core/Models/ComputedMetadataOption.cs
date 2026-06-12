namespace DicomRtNifti.Core.Models
{
    /// <summary>
    /// A selectable, derived (non-DICOM-tag) metadata value presented in the metadata-tag picker
    /// alongside the raw DICOM attributes. Computed values — e.g. the export voxel size, image
    /// dimensions, ROI names — are identified by a pseudo-keyword starting with '@' (see
    /// <see cref="Services.MetadataTagCatalog.ComputedPrefix"/>) so they flow through the same
    /// selection, persistence, and serialization paths as ordinary tags without colliding with a
    /// real dictionary keyword. Plain data object (no fo-dicom types) for the Avalonia view-models.
    /// </summary>
    public class ComputedMetadataOption
    {
        /// <summary>Pseudo-keyword, e.g. "@VoxelSize". Persisted in settings like a real keyword.</summary>
        public string Key { get; set; } = "";

        /// <summary>Friendly name used as the JSON key on export, e.g. "Voxel Size".</summary>
        public string FriendlyName { get; set; } = "";

        /// <summary>Short description of what the value is and how it is derived.</summary>
        public string Description { get; set; } = "";
    }
}
