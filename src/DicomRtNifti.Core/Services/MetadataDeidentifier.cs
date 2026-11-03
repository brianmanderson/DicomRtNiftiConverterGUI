using System;
using System.Collections.Generic;
using System.Linq;
using FellowOakDicom;

namespace DicomRtNifti.Core.Services
{
    /// <summary>
    /// Decides what an anonymized export writes into the forward <c>metadata.json</c> sidecar in
    /// place of each selected DICOM attribute. The sidecar used to write every selected tag
    /// verbatim, so ticking Patient Name or Study Date in the picker put them, unhashed, into
    /// an export whose folders and manifest carried only pseudonyms (W2-E's PHI audit found 16
    /// identifiers per series this way, and nowhere else).
    ///
    /// The rule, per attribute:
    /// <list type="bullet">
    ///   <item>PatientID, StudyInstanceUID and SeriesInstanceUID are replaced by the export's own
    ///   pseudonyms from <see cref="AnonymizationService"/>, so the sidecar still joins to the
    ///   folder names and the manifest.</item>
    ///   <item>Person names (PN), dates and times (DA, DT, TM) and ages (AS) are removed.</item>
    ///   <item>Every other UID (UI) is removed unless it is a standard UID under the DICOM root
    ///   1.2.840.10008 — a SOP class or transfer syntax identifies no one, an instance or frame
    ///   of reference UID does.</item>
    ///   <item>The attributes in <see cref="IdentifyingKeywords"/> — identification numbers,
    ///   addresses and contact details, institution and device identity, and the free-text
    ///   fields that habitually carry these — are removed.</item>
    ///   <item>Everything else is written as read.</item>
    /// </list>
    /// "Removed" means written as null, the value an absent tag already gets, so every export
    /// folder keeps the same key set. A VR rule applies when the VR the file declares or the
    /// dictionary's VR for the tag matches it: fo-dicom keeps whatever VR an explicit-VR file
    /// declares (UN aside, which it reads as the dictionary's), so a study time or a referring
    /// physician's name a file declares LO is still removed.
    ///
    /// The scope is HIPAA's Safe Harbor identifiers as DICOM carries them, read alongside the
    /// identifying rows of PS3.15 Annex E's Basic Profile. Clinical descriptors that identify no
    /// one — sex, size, weight, series and structure descriptions, ROI names — are kept, the
    /// same position <see cref="CohortExportService"/> already takes on SeriesDescription. A
    /// description that a site fills with a patient's name is outside what any tag rule can see.
    /// </summary>
    public sealed class MetadataDeidentifier
    {
        private readonly AnonymizationService _anon;

        /// <summary>Value representations whose every value identifies a person or a time.</summary>
        internal static readonly IReadOnlyCollection<string> RemovedVrCodes =
            new HashSet<string>(StringComparer.Ordinal) { "PN", "DA", "DT", "TM", "AS" };

        /// <summary>The DICOM UID root; UIDs below it are standard (classes, syntaxes), never instances.</summary>
        internal const string DicomUidRoot = "1.2.840.10008.";

        /// <summary>
        /// Identifying attributes whose VR does not already mark them (LO/SH/LT/ST/UT/CS...).
        /// Keywords as fo-dicom's dictionary spells them; a test pins that every one resolves.
        /// </summary>
        internal static readonly IReadOnlyCollection<string> IdentifyingKeywords =
            new HashSet<string>(StringComparer.Ordinal)
            {
                // Patient identification, residence and contact
                "OtherPatientIDs", "IssuerOfPatientID", "PatientAddress", "PatientTelephoneNumbers",
                "PatientTelecomInformation", "CountryOfResidence", "RegionOfResidence",
                "MilitaryRank", "BranchOfService", "MedicalRecordLocator", "ResponsibleOrganization",
                // Free text that habitually carries names, dates and numbers
                "PatientComments", "AdditionalPatientHistory", "ImageComments",
                // Visit, order and study numbers
                "AccessionNumber", "StudyID", "AdmissionID", "RequestedProcedureID",
                "ScheduledProcedureStepID", "PerformedProcedureStepID",
                "PlacerOrderNumberImagingServiceRequest", "FillerOrderNumberImagingServiceRequest",
                "ClinicalTrialSubjectID", "ClinicalTrialSubjectReadingID",
                // Institution and staff contact
                "InstitutionName", "InstitutionAddress", "InstitutionalDepartmentName",
                "ReferringPhysicianAddress", "ReferringPhysicianTelephoneNumbers",
                "PersonAddress", "PersonTelephoneNumbers", "PersonTelecomInformation",
                // Device identity
                "DeviceSerialNumber", "DeviceID", "StationName", "DetectorID", "PlateID", "GantryID",
            };

        public MetadataDeidentifier(AnonymizationService anon)
        {
            _anon = anon ?? throw new ArgumentNullException(nameof(anon));
        }

        /// <summary>
        /// The value to write for <paramref name="tag"/>, given the <paramref name="value"/>
        /// read from the file (a string, number, or array of them; null when absent) and the
        /// element's <paramref name="vrCode"/>.
        /// </summary>
        public object Apply(DicomTag tag, string vrCode, object value)
        {
            if (value == null || tag == null)
                return value;

            if (tag == DicomTag.PatientID)
                return _anon.GetPatientHash(FirstString(value));
            if (tag == DicomTag.StudyInstanceUID)
                return _anon.GetStudyHash(FirstString(value));
            if (tag == DicomTag.SeriesInstanceUID)
                return _anon.GetSeriesHash(FirstString(value));

            if (JudgedVrCodes(tag, vrCode).Contains("UI"))
                return AllStandardUids(value) ? value : null;

            return IsIdentifying(tag, vrCode) ? null : value;
        }

        /// <summary>True when the attribute is removed outright (see the class summary).</summary>
        public static bool IsIdentifying(DicomTag tag, string vrCode)
        {
            if (JudgedVrCodes(tag, vrCode).Any(RemovedVrCodes.Contains))
                return true;
            string keyword = tag?.DictionaryEntry?.Keyword;
            return keyword != null && IdentifyingKeywords.Contains(keyword);
        }

        /// <summary>The VRs the rules judge an attribute by: the one the file declares, then the dictionary's.</summary>
        private static IEnumerable<string> JudgedVrCodes(DicomTag tag, string vrCode)
        {
            if (!string.IsNullOrEmpty(vrCode))
                yield return vrCode;
            var vrs = tag?.DictionaryEntry?.ValueRepresentations;
            if (vrs != null)
                foreach (var vr in vrs)
                    yield return vr.Code;
        }

        private static bool AllStandardUids(object value)
        {
            var values = value is string[] many ? many : new[] { Convert.ToString(value) };
            return values.Length > 0 && values.All(v =>
                v != null && v.Trim().StartsWith(DicomUidRoot, StringComparison.Ordinal));
        }

        private static string FirstString(object value)
        {
            if (value is string[] many)
                return many.Length > 0 ? many[0] : "";
            return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "";
        }
    }
}
