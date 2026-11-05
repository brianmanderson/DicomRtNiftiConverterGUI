using System;
using DicomRtNifti.Core.Documents;

namespace DicomRtNifti.Core.Analysis
{
    /// <summary>
    /// The cohort analysis as a published document, <c>dicomrtnifti.cohort-analysis/1</c>
    /// (decision D-34: a mode and a document of its own, so <c>--cohort-scan</c> stays the cheap
    /// inventory and the three older schemas do not change). The CLI prints it and the web's
    /// Analysis pages read it, so both build it here and a front-end only chooses how the input
    /// root is shown (<see cref="PathPresenter"/>).
    ///
    /// Anonymized, it is held to the rule of the other cohort documents: every PatientID, study
    /// and series UID is the hash an export with the same key would use, and what has no hash is
    /// withheld — the input root, file names, frames of reference, dates. Descriptions, ROI names,
    /// counts and geometry stay: they carry no identifier and they are what the analysis is for.
    /// The tables of <see cref="CohortAnalysisCsv"/> are the same content, flattened.
    /// </summary>
    public static class CohortAnalysisDocument
    {
        public const string Schema = "dicomrtnifti.cohort-analysis/1";

        public static object Build(CohortAnalysis analysis, PathPresenter presenter)
        {
            if (analysis == null) throw new ArgumentNullException(nameof(analysis));

            return new
            {
                schema = Schema,
                tier = analysis.Tier,
                anonymized = analysis.Anonymized,
                input_root = analysis.Anonymized ? null : CohortDocuments.Present(presenter, analysis.InputRoot),
                associations = analysis.Associations,
                // The raster tier's resample target (W4-K); no tier below it resamples.
                output_spacing = (double[])null,
                summary = analysis.Summary,
                geometry = analysis.Geometry,
                series = analysis.Series,
                roi_naming = analysis.RoiNaming,
                linkage = analysis.Linkage,
                flags = analysis.Flags,
            };
        }
    }
}
