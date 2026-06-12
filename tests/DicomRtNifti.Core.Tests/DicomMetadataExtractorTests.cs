using System.Globalization;
using System.IO;
using System.Linq;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Core.Services;
using FellowOakDicom;
using FellowOakDicom.IO.Buffer;
using Newtonsoft.Json.Linq;
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
        public void WriteMetadataJson_WritesSections_WithFriendlyKeys()
        {
            string dir = DicomTestData.NewTempDir();
            try
            {
                string patientId = "PID-1";
                string studyUid = DicomTestData.NewUid();
                string seriesUid = DicomTestData.NewUid();
                string frameUid = DicomTestData.NewUid();

                DicomTestData.WriteImageSlice(dir, "slice.dcm", "CT", patientId, studyUid, seriesUid, frameUid, 0.0);
                string imagePath = Path.Combine(dir, "slice.dcm");

                string structPath = DicomTestData.WriteRtStruct(
                    dir, "struct.dcm", patientId, studyUid, DicomTestData.NewUid(), frameUid,
                    "Plan1", new[] { "PTV", "Lung_L" });

                string dosePath = DicomTestData.WriteRtDose32Bit(
                    dir, "dose.dcm", patientId, studyUid, DicomTestData.NewUid(), frameUid,
                    doseGridScaling: 0.5, storedValues: new uint[] { 0, 3, 7, 4 });

                var request = new MetadataExportRequest
                {
                    ImageFilePaths = new[] { imagePath },
                    StructureFilePath = structPath,
                    DoseFilePath = dosePath,
                    ImageKeywords = new[] { "PatientName", "@VoxelSize" },
                    StructureKeywords = new[] { "StructureSetLabel", "@RoiNames" },
                    DoseKeywords = new[] { "DoseUnits", "@MaxDose" },
                    ImageVoxelSpacing = new[] { 0.98, 0.98, 3.0 },
                };

                string jsonPath = Path.Combine(dir, "metadata.json");
                DicomMetadataExtractor.WriteMetadataJson(request, jsonPath);

                var root = JObject.Parse(File.ReadAllText(jsonPath));

                var image = (JObject)root["ImageAttributes"];
                Assert.Equal("Test^Patient", (string)image["Patient Name"]);
                Assert.Equal(new[] { 0.98, 0.98, 3.0 }, image["Voxel Size"].Select(t => (double)t).ToArray());

                var structure = (JObject)root["StructureAttributes"];
                Assert.Equal("Plan1", (string)structure["Structure Set Label"]);
                Assert.Equal(new[] { "PTV", "Lung_L" }, structure["ROI Names"].Select(t => (string)t).ToArray());

                var dose = (JObject)root["DoseAttributes"];
                Assert.Equal("GY", (string)dose["Dose Units"]);
                Assert.Equal(3.5, (double)dose["Max Dose"]); // max stored 7 × scaling 0.5
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public void WriteMetadataJson_OmitsSection_WhenNoKeywordsSelected()
        {
            string dir = DicomTestData.NewTempDir();
            try
            {
                DicomTestData.WriteImageSlice(dir, "slice.dcm", "CT", "PID-1",
                    DicomTestData.NewUid(), DicomTestData.NewUid(), DicomTestData.NewUid(), 0.0);
                string imagePath = Path.Combine(dir, "slice.dcm");

                var request = new MetadataExportRequest
                {
                    ImageFilePaths = new[] { imagePath },
                    ImageKeywords = new[] { "PatientName" },
                    // Structure/Dose keyword lists left null -> sections omitted.
                };

                string jsonPath = Path.Combine(dir, "metadata.json");
                DicomMetadataExtractor.WriteMetadataJson(request, jsonPath);

                var root = JObject.Parse(File.ReadAllText(jsonPath));
                Assert.True(root.ContainsKey("ImageAttributes"));
                Assert.False(root.ContainsKey("StructureAttributes"));
                Assert.False(root.ContainsKey("DoseAttributes"));
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public void WriteMetadataJson_WritesNulls_WhenSectionSourceMissing()
        {
            string dir = DicomTestData.NewTempDir();
            try
            {
                DicomTestData.WriteImageSlice(dir, "slice.dcm", "CT", "PID-1",
                    DicomTestData.NewUid(), DicomTestData.NewUid(), DicomTestData.NewUid(), 0.0);
                string imagePath = Path.Combine(dir, "slice.dcm");

                var request = new MetadataExportRequest
                {
                    ImageFilePaths = new[] { imagePath },
                    StructureFilePath = null, // selected but no linked RTSTRUCT
                    ImageKeywords = new[] { "PatientName" },
                    StructureKeywords = new[] { "StructureSetLabel", "@RoiNames" },
                };

                string jsonPath = Path.Combine(dir, "metadata.json");
                DicomMetadataExtractor.WriteMetadataJson(request, jsonPath);

                var root = JObject.Parse(File.ReadAllText(jsonPath));
                var structure = (JObject)root["StructureAttributes"];
                Assert.True(structure.ContainsKey("Structure Set Label"));
                Assert.Equal(JTokenType.Null, structure["Structure Set Label"].Type);
                Assert.Equal(JTokenType.Null, structure["ROI Names"].Type);
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public void MaxDose_AppliesDoseGridScaling()
        {
            var ds = DoseDataset(scaling: 0.5, stored: new uint[] { 0, 1, 7, 3 });
            Assert.Equal(3.5, Assert.IsType<double>(MetadataComputedValues.MaxDose(ds)));
        }

        [Fact]
        public void RoiNames_And_Count_FromStructureSetRoiSequence()
        {
            string dir = DicomTestData.NewTempDir();
            try
            {
                string frameUid = DicomTestData.NewUid();
                string structPath = DicomTestData.WriteRtStruct(
                    dir, "struct.dcm", "PID-1", DicomTestData.NewUid(), DicomTestData.NewUid(), frameUid,
                    "Plan1", new[] { "PTV", "Lung_L", "Cord" });

                var ds = DicomFile.Open(structPath).Dataset;
                Assert.Equal(new[] { "PTV", "Lung_L", "Cord" }, Assert.IsType<string[]>(MetadataComputedValues.RoiNames(ds)));
                Assert.Equal(3L, Assert.IsType<long>(MetadataComputedValues.RoiCount(ds)));
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public void DoseVoxelSize_FromGridFrameOffsetVector()
        {
            // PixelSpacing is [row(y), column(x)] = [2.5, 3.0]; GFOV step is 4 -> [x, y, z] = [3.0, 2.5, 4.0].
            var ds = new DicomDataset(DicomTransferSyntax.ExplicitVRLittleEndian);
            ds.Add(DicomTag.PixelSpacing, "2.5", "3.0");
            ds.Add(DicomTag.GridFrameOffsetVector, "0", "4", "8");

            var result = Assert.IsType<double[]>(MetadataComputedValues.DoseVoxelSize(ds));
            Assert.Equal(new[] { 3.0, 2.5, 4.0 }, result);
        }

        [Fact]
        public void ImageDimensions_FromRowsColumnsAndFileCount()
        {
            var ds = new DicomDataset(DicomTransferSyntax.ExplicitVRLittleEndian)
            {
                { DicomTag.Rows, (ushort)512 },
                { DicomTag.Columns, (ushort)256 },
            };
            var result = Assert.IsType<long[]>(MetadataComputedValues.ImageDimensions(ds, sliceFileCount: 10));
            Assert.Equal(new long[] { 256, 512, 10 }, result); // [columns, rows, slices]
        }

        [Fact]
        public void ImageVoxelSize_PrefersPrecomputedSpacing()
        {
            // Precomputed spacing wins even when the dataset has a different PixelSpacing.
            var ds = new DicomDataset(DicomTransferSyntax.ExplicitVRLittleEndian);
            ds.Add(DicomTag.PixelSpacing, "9.0", "9.0");
            ds.Add(DicomTag.SliceThickness, "9.0");

            var result = Assert.IsType<double[]>(
                MetadataComputedValues.ImageVoxelSize(ds, new[] { 0.98, 0.98, 3.0 }));
            Assert.Equal(new[] { 0.98, 0.98, 3.0 }, result);
        }

        private static DicomDataset DoseDataset(double scaling, uint[] stored)
        {
            var ds = new DicomDataset(DicomTransferSyntax.ExplicitVRLittleEndian)
            {
                { DicomTag.BitsAllocated, (ushort)32 },
                { DicomTag.BitsStored, (ushort)32 },
                { DicomTag.HighBit, (ushort)31 },
                { DicomTag.PixelRepresentation, (ushort)0 },
                { DicomTag.DoseGridScaling, scaling.ToString(CultureInfo.InvariantCulture) },
            };
            byte[] bytes = new byte[stored.Length * 4];
            System.Buffer.BlockCopy(stored, 0, bytes, 0, bytes.Length);
            ds.Add(new DicomOtherWord(DicomTag.PixelData, new MemoryByteBuffer(bytes)));
            return ds;
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
