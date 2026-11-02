using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DicomRtNifti.Core.Services;
using FellowOakDicom;
using Xunit;

namespace DicomRtNifti.Core.Tests
{
    /// <summary>
    /// The scanner reads files in parallel but must build the same hierarchy the sequential scan
    /// did, for any input: every file in its series, and every "first file wins" attribute (series
    /// description and date, study description, patient name, in-plane spacing) taken from the
    /// first file in directory-walk order, not from whichever read finished first.
    /// </summary>
    public class DicomScannerOrderingTests
    {
        /// <summary>
        /// Many short series read at once. Grouping inside the parallel reads used to let two
        /// readers each create the same series; one object went into the study, the other into the
        /// lookup that received every file, and the series showed up with no instances.
        /// </summary>
        [Fact]
        public async Task Every_series_keeps_all_of_its_files_when_read_in_parallel()
        {
            string dir = DicomTestData.NewTempDir();
            try
            {
                string study = DicomTestData.NewUid(), frame = DicomTestData.NewUid();
                var seriesUids = Enumerable.Range(0, 300).Select(_ => DicomTestData.NewUid()).ToList();
                for (int s = 0; s < seriesUids.Count; s++)
                    for (int i = 0; i < 3; i++)
                        DicomTestData.WriteImageSlice(dir, $"s{s:000}_{i}.dcm", "CT", "P1", study, seriesUids[s], frame, i * 2.0);

                for (int rep = 0; rep < 5; rep++)
                {
                    var result = await new DicomScannerService().ScanFolderAsync(dir, null, CancellationToken.None);
                    var st = Assert.Single(Assert.Single(result.Patients).Studies);
                    Assert.Equal(seriesUids.Count, st.Series.Count);
                    Assert.All(st.Series, s =>
                    {
                        Assert.Equal(3, s.FilePaths.Count);
                        Assert.Equal(new[] { 0.0, 2.0, 4.0 }, s.SlicePositions.ToArray());
                    });
                }
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        /// <summary>
        /// The file the directory walk returns first is made slow to read (a large private
        /// sequence), so every other file of its series finishes first. Its attributes must still
        /// be the ones the series, study and patient carry.
        /// </summary>
        [Fact]
        public async Task Series_study_and_patient_attributes_come_from_the_first_file_in_walk_order()
        {
            string dir = DicomTestData.NewTempDir();
            try
            {
                string study = DicomTestData.NewUid(), series = DicomTestData.NewUid(), frame = DicomTestData.NewUid();
                for (int i = 0; i < 200; i++)
                    File.WriteAllText(Path.Combine(dir, $"f{i:000}.dcm"), "");
                var walkOrder = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToList();

                for (int i = 0; i < walkOrder.Count; i++)
                {
                    bool first = i == 0;
                    var ds = new DicomDataset(DicomTransferSyntax.ExplicitVRLittleEndian)
                    {
                        { DicomTag.SOPClassUID, DicomUID.CTImageStorage },
                        { DicomTag.SOPInstanceUID, DicomUIDGenerator.GenerateDerivedFromUUID() },
                        { DicomTag.PatientID, "P1" },
                        { DicomTag.PatientName, first ? "First^Reader" : "Later^Reader" },
                        { DicomTag.StudyInstanceUID, study },
                        { DicomTag.StudyDescription, first ? "first study description" : "later study description" },
                        { DicomTag.SeriesInstanceUID, series },
                        { DicomTag.SeriesDescription, first ? "first" : "later" },
                        { DicomTag.SeriesDate, first ? "20200101" : "20200202" },
                        { DicomTag.Modality, "CT" },
                        { DicomTag.FrameOfReferenceUID, frame },
                    };
                    ds.Add(DicomTag.ImagePositionPatient, "0", "0", i.ToString(CultureInfo.InvariantCulture));
                    ds.Add(DicomTag.PixelSpacing, first ? "0.5" : "1", first ? "0.5" : "1");
                    if (first)
                    {
                        // Tens of thousands of small items: far slower to parse than any other file.
                        var items = new DicomDataset[40000];
                        for (int k = 0; k < items.Length; k++)
                            items[k] = new DicomDataset { { DicomTag.TextValue, "x" } };
                        ds.Add(new DicomSequence(DicomTag.ContentSequence, items));
                    }
                    new DicomFile(ds).Save(walkOrder[i]);
                }

                for (int rep = 0; rep < 3; rep++)
                {
                    var result = await new DicomScannerService().ScanFolderAsync(dir, null, CancellationToken.None);
                    var patient = Assert.Single(result.Patients);
                    var st = Assert.Single(patient.Studies);
                    var s = Assert.Single(st.Series);

                    Assert.Equal("First^Reader", patient.PatientName);
                    Assert.Equal("first study description", st.StudyDescription);
                    Assert.Equal("first", s.SeriesDescription);
                    Assert.Equal("20200101", s.SeriesDate);
                    Assert.Equal(new[] { 0.5, 0.5 }, s.PixelSpacing);
                    Assert.Equal(200, s.FilePaths.Count);
                }
            }
            finally { Directory.Delete(dir, recursive: true); }
        }
    }
}
