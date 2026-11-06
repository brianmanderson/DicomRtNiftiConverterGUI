using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Core.Services;
using FellowOakDicom;
using Xunit;

namespace DicomRtNifti.Core.Tests
{
    /// <summary>
    /// The scan's grouping and linking, as one fingerprint of a tree that exercises every rule:
    /// the three link rules for structure sets and doses, a dose shared by two series of one
    /// frame, unlinked RT objects, a stray-modality instance, an unpositioned slice, a duplicated
    /// ROI name and a file that is not DICOM. Recorded against the scanner before W4-F read more
    /// of each header; it pins that reading more changed nothing the hierarchy is built from.
    /// </summary>
    public class DicomScannerGroupingFingerprintTests
    {
        private const string Expected =
            "patient P1\n" +
            " study 1.9.1\n" +
            "  series 1.9.1.1 CT 'Planning CT' files=4 z=[0,2,4,6] unpositioned=0 frame=1.9.1.90 ref='' rule=None spacing=[0.9,0.7] thickness=2 rois=[] structs=[1.9.1.4:FrameOfReferenceUid] doses=[1.9.1.5:FrameOfReferenceUid]\n" +
            "  series 1.9.1.2 MR 'MR' files=3 z=[0,3,6] unpositioned=0 frame=1.9.1.90 ref='' rule=None spacing=[0.9,0.7] thickness=2 rois=[] structs=[1.9.1.3:ReferencedSeriesUid] doses=[1.9.1.5:FrameOfReferenceUid]\n" +
            "  series 1.9.1.3 RTSTRUCT 'structures' files=1 z=[] unpositioned=0 frame=1.9.1.90 ref='1.9.1.2' rule=ReferencedSeriesUid spacing=null thickness=null rois=[GTV,Lung_L] structs=[] doses=[]\n" +
            "  series 1.9.1.4 RTSTRUCT 'structures' files=1 z=[] unpositioned=0 frame=1.9.1.90 ref='' rule=FrameOfReferenceUid spacing=null thickness=null rois=[Body] structs=[] doses=[]\n" +
            "  series 1.9.1.5 RTDOSE 'dose' files=1 z=[] unpositioned=0 frame=1.9.1.90 ref='' rule=FrameOfReferenceUid spacing=null thickness=null rois=[] structs=[] doses=[]\n" +
            " study 1.9.2\n" +
            "  series 1.9.2.1 CT 'CT' files=3 z=[0,5,10] unpositioned=0 frame=1.9.2.90 ref='' rule=None spacing=[0.9,0.7] thickness=2 rois=[] structs=[1.9.2.3:LargestSeriesFallback] doses=[1.9.2.5:LargestSeriesFallback]\n" +
            "  series 1.9.2.3 RTSTRUCT 'structures' files=1 z=[] unpositioned=0 frame=1.9.2.99 ref='' rule=LargestSeriesFallback spacing=null thickness=null rois=[Target] structs=[] doses=[]\n" +
            "  series 1.9.2.5 RTDOSE 'dose' files=1 z=[] unpositioned=0 frame=1.9.2.99 ref='' rule=LargestSeriesFallback spacing=null thickness=null rois=[] structs=[] doses=[]\n" +
            "patient P2\n" +
            " study 1.9.3\n" +
            "  series 1.9.3.3 RTSTRUCT 'structures' files=1 z=[] unpositioned=0 frame=1.9.3.90 ref='' rule=None spacing=null thickness=null rois=[Orphan] structs=[] doses=[]\n" +
            "  series 1.9.3.5 RTDOSE 'dose' files=1 z=[] unpositioned=0 frame=1.9.3.90 ref='' rule=None spacing=null thickness=null rois=[] structs=[] doses=[]\n" +
            " study 1.9.4\n" +
            "  series 1.9.4.1 CT 'CT' files=3 z=[0,1,2] unpositioned=1 frame=1.9.4.90 ref='' rule=None spacing=[0.9,0.7] thickness=2 rois=[] structs=[] doses=[]\n";

        [Fact]
        public async Task GroupingAndLinking_MatchTheRecordedFingerprint()
        {
            string root = DicomTestData.NewTempDir();
            try
            {
                WriteTree(root);

                var scan = await new DicomScannerService().ScanFolderAsync(root, null, CancellationToken.None);

                Assert.Equal(Expected, Fingerprint(scan));
                Assert.Equal(0, scan.SkippedErrorCount);
            }
            finally
            {
                try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
            }
        }

        internal static void WriteTree(string root)
        {
            // P1 / study 1.9.1: a CT and an MR sharing one frame; one structure set references the
            // MR, one names only the frame (so the fuller CT wins), and a dose of that frame.
            string s1 = Path.Combine(root, "P1", "s1");
            for (int i = 0; i < 4; i++)
                WriteSlice(s1, $"ct_{i}.dcm", "CT", "P1", "1.9.1", "1.9.1.1", "1.9.1.90", "Planning CT", i * 2.0);
            for (int i = 0; i < 3; i++)
                WriteSlice(s1, $"mr_{i}.dcm", "MR", "P1", "1.9.1", "1.9.1.2", "1.9.1.90", "MR", i * 3.0);
            DicomTestData.WriteRtStruct(s1, "rs_ref.dcm", "P1", "1.9.1", "1.9.1.3", "1.9.1.90", "REF",
                new[] { "GTV", "Lung_L", "GTV" }, referencedSeriesUid: "1.9.1.2");
            DicomTestData.WriteRtStruct(s1, "rs_frame.dcm", "P1", "1.9.1", "1.9.1.4", "1.9.1.90", "FRAME",
                new[] { "Body" });
            DicomTestData.WriteRtDose(s1, "dose.dcm", "P1", "1.9.1", "1.9.1.5", "1.9.1.90");

            // P1 / study 1.9.2: a CT with one stray OT instance, and a structure set and dose whose
            // frame matches nothing (the largest-series fallback).
            string s2 = Path.Combine(root, "P1", "s2");
            WriteSlice(s2, "ct_0.dcm", "CT", "P1", "1.9.2", "1.9.2.1", "1.9.2.90", "CT", 0.0);
            WriteSlice(s2, "ct_1.dcm", "CT", "P1", "1.9.2", "1.9.2.1", "1.9.2.90", "CT", 5.0);
            WriteSlice(s2, "ot_2.dcm", "OT", "P1", "1.9.2", "1.9.2.1", "1.9.2.90", "CT", 10.0);
            DicomTestData.WriteRtStruct(s2, "rs.dcm", "P1", "1.9.2", "1.9.2.3", "1.9.2.99", "GUESS",
                new[] { "Target" });
            DicomTestData.WriteRtDose(s2, "dose.dcm", "P1", "1.9.2", "1.9.2.5", "1.9.2.99");

            // P2 / study 1.9.3: RT objects in a study with no image series (unlinked).
            string s3 = Path.Combine(root, "P2", "s3");
            Directory.CreateDirectory(s3);
            DicomTestData.WriteRtStruct(s3, "rs.dcm", "P2", "1.9.3", "1.9.3.3", "1.9.3.90", "ORPHAN",
                new[] { "Orphan" });
            DicomTestData.WriteRtDose(s3, "dose.dcm", "P2", "1.9.3", "1.9.3.5", "1.9.3.90");

            // P2 / study 1.9.4: a CT with one slice that carries no ImagePositionPatient, and a
            // file that is not DICOM at all.
            string s4 = Path.Combine(root, "P2", "s4");
            for (int i = 0; i < 3; i++)
                WriteSlice(s4, $"ct_{i}.dcm", "CT", "P2", "1.9.4", "1.9.4.1", "1.9.4.90", "CT", i * 1.0);
            WriteSlice(s4, "ct_x.dcm", "CT", "P2", "1.9.4", "1.9.4.1", "1.9.4.90", "CT", null);
            File.WriteAllText(Path.Combine(s4, "notes.txt"), "not DICOM");
        }

        /// <summary>
        /// One image slice with PixelSpacing [0.9, 0.7] (row, column) and SliceThickness 2; no
        /// ImagePositionPatient when <paramref name="z"/> is null.
        /// </summary>
        private static void WriteSlice(string dir, string fileName, string modality, string patientId,
            string studyUid, string seriesUid, string frameUid, string description, double? z)
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
            };
            if (z.HasValue)
                ds.Add(DicomTag.ImagePositionPatient, "0", "0", z.Value.ToString(CultureInfo.InvariantCulture));
            ds.Add(DicomTag.PixelSpacing, "0.9", "0.7");
            ds.Add(DicomTag.SliceThickness, "2");
            new DicomFile(ds).Save(Path.Combine(dir, fileName));
        }

        private static string Fingerprint(DicomScanResult scan)
        {
            var sb = new StringBuilder();
            foreach (var patient in scan.Patients)
            {
                sb.Append("patient ").Append(patient.PatientID).Append('\n');
                foreach (var study in patient.Studies)
                {
                    sb.Append(" study ").Append(study.StudyInstanceUID).Append('\n');
                    foreach (var s in study.Series)
                    {
                        sb.Append("  series ").Append(s.SeriesInstanceUID)
                          .Append(' ').Append(s.Modality)
                          .Append(" '").Append(s.SeriesDescription).Append('\'')
                          .Append(" files=").Append(s.FilePaths.Count)
                          .Append(" z=[").Append(string.Join(",", s.SlicePositions.Select(Number))).Append(']')
                          .Append(" unpositioned=").Append(s.UnpositionedSlices.Count)
                          .Append(" frame=").Append(s.FrameOfReferenceUID)
                          .Append(" ref='").Append(s.ReferencedSeriesUID).Append('\'')
                          .Append(" rule=").Append(s.LinkMatchRule)
                          .Append(" spacing=").Append(s.PixelSpacing == null ? "null" : "[" + string.Join(",", s.PixelSpacing.Select(Number)) + "]")
                          .Append(" thickness=").Append(s.SliceThickness.HasValue ? Number(s.SliceThickness.Value) : "null")
                          .Append(" rois=[").Append(string.Join(",", s.RoiNames)).Append(']')
                          .Append(" structs=[").Append(Links(s.LinkedRtStructs)).Append(']')
                          .Append(" doses=[").Append(Links(s.LinkedRtDoses)).Append(']')
                          .Append('\n');

                        // The legacy singular links stay the first of each list.
                        Assert.Same(s.LinkedRtStructs.FirstOrDefault(), s.LinkedRtStruct);
                        Assert.Same(s.LinkedRtDoses.FirstOrDefault(), s.LinkedRtDose);
                    }
                }
            }
            return sb.ToString();
        }

        private static string Links(IEnumerable<DicomSeriesGroup> linked) =>
            string.Join(",", linked.Select(l => l.SeriesInstanceUID + ":" + l.LinkMatchRule));

        private static string Number(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
