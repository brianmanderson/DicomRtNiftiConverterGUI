using System.IO;
using System.Linq;
using DicomRtNifti.Core.Services;
using FellowOakDicom;
using Xunit;

namespace DicomRtNifti.Core.Tests
{
    /// <summary>
    /// Tests the DICOM metadata extractor: value typing (numbers vs strings vs arrays vs null),
    /// the metadata.json round-trip, and the selectable-tag filtering.
    /// </summary>
    public class DicomMetadataExtractorTests
    {
        private static DicomDataset SampleDataset()
        {
            var ds = new DicomDataset(DicomTransferSyntax.ExplicitVRLittleEndian)
            {
                { DicomTag.SOPClassUID, DicomUID.CTImageStorage },
                { DicomTag.SOPInstanceUID, DicomUIDGenerator.GenerateDerivedFromUUID() },
                { DicomTag.PatientName, "Test^Patient" }, // PN -> string
                { DicomTag.PatientID, "PID-1" },          // LO -> string
                { DicomTag.PatientAge, "035Y" },          // AS -> string (NOT a number)
                { DicomTag.SeriesNumber, "3" },           // IS -> integer
                { DicomTag.SliceThickness, "1.25" },      // DS -> real
                { DicomTag.Rows, (ushort)512 },           // US -> integer
                { DicomTag.StudyDate, "20240101" },       // DA -> string
            };
            ds.Add(DicomTag.ImageOrientationPatient, "1", "0", "0", "0", "1", "0"); // DS x6 -> double[]
            return ds;
        }

        [Fact]
        public void Extract_TypesValuesByVr()
        {
            var ds = SampleDataset();
            var keywords = new[]
            {
                "PatientName", "PatientAge", "SeriesNumber", "SliceThickness",
                "Rows", "ImageOrientationPatient", "PatientBirthDate"
            };

            var result = DicomMetadataExtractor.Extract(ds, keywords);

            Assert.Equal("Test^Patient", Assert.IsType<string>(result["PatientName"]));
            Assert.Equal("035Y", Assert.IsType<string>(result["PatientAge"]));      // AS stays a string
            Assert.Equal(3L, Assert.IsType<long>(result["SeriesNumber"]));          // IS -> integer
            Assert.Equal(1.25d, Assert.IsType<double>(result["SliceThickness"]));   // DS -> real
            Assert.Equal(512L, Assert.IsType<long>(result["Rows"]));                // US -> integer

            var orientation = Assert.IsType<double[]>(result["ImageOrientationPatient"]);
            Assert.Equal(new[] { 1d, 0d, 0d, 0d, 1d, 0d }, orientation);            // multi-valued -> array

            // Absent tag: key is present (stable schema) with a null value.
            Assert.True(result.ContainsKey("PatientBirthDate"));
            Assert.Null(result["PatientBirthDate"]);
        }

        [Fact]
        public void Extract_PreservesKeywordOrder_AndIgnoresUnknownKeywords()
        {
            var ds = SampleDataset();
            var result = DicomMetadataExtractor.Extract(ds, new[] { "PatientID", "NotARealKeyword", "PatientName" });

            Assert.Equal(new[] { "PatientID", "NotARealKeyword", "PatientName" }, result.Keys.ToArray());
            Assert.Null(result["NotARealKeyword"]); // unknown keyword -> null, key retained
        }

        [Fact]
        public void WriteMetadataJson_RoundTrips_WithJsonNumberTypes()
        {
            string dir = DicomTestData.NewTempDir();
            try
            {
                string dcmPath = Path.Combine(dir, "slice.dcm");
                new DicomFile(SampleDataset()).Save(dcmPath);

                string jsonPath = Path.Combine(dir, "metadata.json");
                DicomMetadataExtractor.WriteMetadataJson(
                    dcmPath,
                    new[] { "PatientName", "SeriesNumber", "SliceThickness", "PatientBirthDate" },
                    jsonPath);

                string json = File.ReadAllText(jsonPath);

                Assert.Contains("\"PatientName\": \"Test^Patient\"", json);
                Assert.Contains("\"SeriesNumber\": 3", json);        // number, not "3"
                Assert.Contains("\"SliceThickness\": 1.25", json);   // number, not "1.25"
                Assert.Contains("\"PatientBirthDate\": null", json); // absent -> null
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public void GetSelectableTags_IncludesScalars_ExcludesSequencesAndBulk()
        {
            var tags = DicomMetadataExtractor.GetSelectableTags();
            var keywords = tags.Select(t => t.Keyword).ToHashSet();

            Assert.Contains("PatientName", keywords);
            Assert.Contains("SliceThickness", keywords);

            // Sequence (SQ) and bulk/binary (OW/OB) tags are not human-readable scalars.
            Assert.DoesNotContain("ReferencedImageSequence", keywords);
            Assert.DoesNotContain("PixelData", keywords);

            var patientName = tags.First(t => t.Keyword == "PatientName");
            Assert.Equal("PN", patientName.VrCode);
            Assert.Equal("(0010,0010)", patientName.TagDisplay);
        }
    }
}
