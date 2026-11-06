using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Core.Services;
using FellowOakDicom;

namespace DicomRtNifti.Core.Tests
{
    /// <summary>
    /// A small cohort written with fixed identifiers that raises every flag code of the header-tier
    /// analysis once or more, so its document is the checked-in example of
    /// dicomrtnifti.cohort-analysis/1. Headers only: the analysis reads no pixel data.
    ///
    ///   AN_01  study 2.1.1: a CT with non-square pixels (PixelSpacing 0.7 \ 0.9) and an MR tilted
    ///          10 degrees about x (GEOM_OBLIQUE) in another frame (LINK_MULTIPLE_FRAMES). Two
    ///          structure sets reference the CT by UID (RT_MULTIPLE_STRUCTS): one lists GTV twice
    ///          (ROI_DUPLICATE_NAME), the other names a frame that is not the CT's
    ///          (LINK_FOR_MISMATCH). A dose of the CT's frame.
    ///   AN_02  study 2.2.1: a CT with mixed gaps (GEOM_NONUNIFORM_Z), one slice without a position
    ///          (GEOM_UNPOSITIONED) and one instance tagged OT (GEOM_MODALITY_MINORITY); a structure
    ///          set referencing a series the study does not hold (LINK_REFERENCED_SERIES_ABSENT)
    ///          whose frame matches nothing either (LINK_FALLBACK).
    ///   AN_03  study 2.3.1: a CT one of whose slices is rotated (GEOM_MIXED_ORIENTATION) and whose
    ///          rescale differs per slice; study 2.3.2: a dose with no image series (LINK_UNLINKED).
    ///
    /// With <see cref="Associations"/>, GTV, Heart and PTV 70 match no association (ROI_UNASSOCIATED);
    /// "Spinal-Cord" / "spinal cord" and "Lung_L" / "Lung-L" are the normalize clusters.
    /// </summary>
    internal static class AnalysisCohort
    {
        public const string Salt = "ANALYSIS";

        public static readonly double[] Axial = { 1, 0, 0, 0, 1, 0 };

        /// <summary>Rows along x, columns tilted 10 degrees from y toward z: a slice normal 10 degrees off z.</summary>
        public static readonly double[] Tilted10 = { 1, 0, 0, 0, 0.984808, 0.173648 };

        /// <summary>An axial plane rotated 30 degrees in-plane: the same normal, other row and column directions.</summary>
        public static readonly double[] InPlaneRotated30 = { 0.866025, 0.5, 0, -0.5, 0.866025, 0 };

        public static List<RoiAssociation> Associations() => new List<RoiAssociation>
        {
            new RoiAssociation { CanonicalName = "SpinalCord", Aliases = { "Spinal-Cord", "spinal cord" } },
            new RoiAssociation { CanonicalName = "Lung_L", Aliases = { "Lung-L" } },
        };

        /// <summary>Writes <see cref="Associations"/> as the GUI exports them; returns the path.</summary>
        public static string WriteAssociations(string path)
        {
            new SettingsService().ExportAssociations(Associations(), path);
            return path;
        }

        public static void Write(string input)
        {
            // AN_01
            string a = Path.Combine(input, "AN_01");
            for (int i = 0; i < 5; i++)
                Slice(Path.Combine(a, "ct"), $"ct_{i}.dcm", "CT", "AN_01", "2.1.1", "2.1.1.1", "2.1.1.90", "Planning CT", i * 2.0, Axial);
            for (int i = 0; i < 4; i++)
                Slice(Path.Combine(a, "mr"), $"mr_{i}.dcm", "MR", "AN_01", "2.1.1", "2.1.1.2", "2.1.1.91", "T1 tilted", i * 3.0, Tilted10);
            Directory.CreateDirectory(Path.Combine(a, "rt"));
            DicomTestData.WriteRtStruct(Path.Combine(a, "rt"), "rs_plan.dcm", "AN_01", "2.1.1", "2.1.1.3", "2.1.1.90", "PLAN",
                new[] { "GTV", "Spinal-Cord", "Lung_L", "GTV" }, referencedSeriesUid: "2.1.1.1", seriesDescription: "Plan structures");
            DicomTestData.WriteRtStruct(Path.Combine(a, "rt"), "rs_other.dcm", "AN_01", "2.1.1", "2.1.1.4", "2.1.1.99", "OTHER",
                new[] { "Heart" }, referencedSeriesUid: "2.1.1.1", seriesDescription: "Other frame");
            DicomTestData.WriteRtDose(Path.Combine(a, "rt"), "dose.dcm", "AN_01", "2.1.1", "2.1.1.5", "2.1.1.90");

            // AN_02: z 0, 3, 6, 12, 15 plus the stray OT instance at 18; slice "ct_x" has no position.
            string b = Path.Combine(input, "AN_02");
            var zs = new[] { 0.0, 3.0, 6.0, 12.0, 15.0 };
            for (int i = 0; i < zs.Length; i++)
                Slice(Path.Combine(b, "ct"), $"ct_{i}.dcm", "CT", "AN_02", "2.2.1", "2.2.1.1", "2.2.1.90", "CT", zs[i], Axial);
            Slice(Path.Combine(b, "ct"), "ct_x.dcm", "CT", "AN_02", "2.2.1", "2.2.1.1", "2.2.1.90", "CT", null, Axial);
            Slice(Path.Combine(b, "ct"), "ot_5.dcm", "OT", "AN_02", "2.2.1", "2.2.1.1", "2.2.1.90", "CT", 18.0, Axial);
            Directory.CreateDirectory(Path.Combine(b, "rt"));
            DicomTestData.WriteRtStruct(Path.Combine(b, "rt"), "rs.dcm", "AN_02", "2.2.1", "2.2.1.3", "2.2.1.77", "GUESS",
                new[] { "spinal cord", "Lung-L", "PTV 70" }, referencedSeriesUid: "2.2.1.8", seriesDescription: "Unmatched frame");

            // AN_03
            string c = Path.Combine(input, "AN_03");
            Slice(Path.Combine(c, "ct"), "ct_0.dcm", "CT", "AN_03", "2.3.1", "2.3.1.1", "2.3.1.90", "CT", 0.0, Axial, rescaleSlope: 1.0);
            Slice(Path.Combine(c, "ct"), "ct_1.dcm", "CT", "AN_03", "2.3.1", "2.3.1.1", "2.3.1.90", "CT", 2.0, Axial, rescaleSlope: 1.0);
            Slice(Path.Combine(c, "ct"), "ct_2.dcm", "CT", "AN_03", "2.3.1", "2.3.1.1", "2.3.1.90", "CT", 4.0, InPlaneRotated30, rescaleSlope: 2.0);
            Directory.CreateDirectory(Path.Combine(c, "dose"));
            DicomTestData.WriteRtDose(Path.Combine(c, "dose"), "dose.dcm", "AN_03", "2.3.2", "2.3.2.5", "2.3.2.90");
        }

        /// <summary>
        /// One image slice's header: PixelSpacing 0.7 \ 0.9 (row, column), 16 rows by 20 columns,
        /// SliceThickness 2, rescale slope and intercept. No ImagePositionPatient when
        /// <paramref name="z"/> is null.
        /// </summary>
        public static void Slice(string dir, string fileName, string modality, string patientId, string studyUid,
            string seriesUid, string frameUid, string description, double? z, double[] orientation,
            double rescaleSlope = 1.0)
        {
            Directory.CreateDirectory(dir);
            var ds = new DicomDataset(DicomTransferSyntax.ExplicitVRLittleEndian)
            {
                { DicomTag.SOPClassUID, DicomUID.CTImageStorage },
                { DicomTag.SOPInstanceUID, DicomUIDGenerator.GenerateDerivedFromUUID() },
                { DicomTag.PatientID, patientId },
                { DicomTag.PatientName, "Test^Patient" },
                { DicomTag.StudyInstanceUID, studyUid },
                { DicomTag.SeriesInstanceUID, seriesUid },
                { DicomTag.Modality, modality },
                { DicomTag.FrameOfReferenceUID, frameUid },
                { DicomTag.SeriesDescription, description },
                { DicomTag.InstanceNumber, "1" },
                { DicomTag.Rows, (ushort)16 },
                { DicomTag.Columns, (ushort)20 },
            };
            if (z.HasValue)
                ds.Add(DicomTag.ImagePositionPatient, "0", "0", z.Value.ToString(CultureInfo.InvariantCulture));
            ds.Add(DicomTag.ImageOrientationPatient,
                orientation.Select(v => v.ToString("0.######", CultureInfo.InvariantCulture)).ToArray());
            ds.Add(DicomTag.PixelSpacing, "0.7", "0.9");
            ds.Add(DicomTag.SliceThickness, "2");
            ds.Add(DicomTag.RescaleSlope, rescaleSlope.ToString(CultureInfo.InvariantCulture));
            ds.Add(DicomTag.RescaleIntercept, "-1024");
            new DicomFile(ds).Save(Path.Combine(dir, fileName));
        }
    }
}
