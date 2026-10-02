using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Core.Services;
using Newtonsoft.Json;
using Xunit;

namespace DicomRtNifti.Core.Tests
{
    /// <summary>The reverse direction's metadata.json: synthesized defaults, persistence, round trip.</summary>
    public class NiftiMetadataServiceTests : IDisposable
    {
        private readonly string _dir = DicomTestData.NewTempDir();
        private readonly NiftiMetadataService _service = new NiftiMetadataService();

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        }

        private string MetadataPath => Path.Combine(_dir, "metadata.json");

        [Fact]
        public void LoadOrSynthesize_OnAnEmptyFolder_FillsDefaults_AndPersistsThem()
        {
            var meta = _service.LoadOrSynthesize(_dir);

            Assert.Matches("^ANON_[0-9a-fA-F]{6}$", meta.PatientId);
            Assert.Equal("ANON^ANON", meta.PatientName);
            Assert.StartsWith("2.25.", meta.StudyInstanceUid);
            Assert.StartsWith("2.25.", meta.FrameOfReferenceUid);
            Assert.NotEqual(meta.StudyInstanceUid, meta.FrameOfReferenceUid);
            Assert.Matches(@"^\d{8}$", meta.StudyDate);
            Assert.Matches(@"^\d{6}$", meta.StudyTime);
            Assert.Equal("CT", meta.ImageModality);
            Assert.Equal("HFS", meta.PatientPosition);
            Assert.Equal("", meta.ImageSeriesInstanceUid);
            Assert.Empty(meta.ImageSopInstanceUids);
            Assert.Equal(1.0, meta.ImageRescaleSlope);
            Assert.Equal(0.0, meta.ImageRescaleIntercept);
            Assert.True(File.Exists(MetadataPath));
        }

        [Fact]
        public void LoadOrSynthesize_SecondCall_ReturnsTheSameIdentifiers()
        {
            var first = _service.LoadOrSynthesize(_dir);
            string bytes = File.ReadAllText(MetadataPath);
            var second = _service.LoadOrSynthesize(_dir);

            Assert.Equal(JsonConvert.SerializeObject(first), JsonConvert.SerializeObject(second));
            Assert.Equal(bytes, File.ReadAllText(MetadataPath));
        }

        [Fact]
        public void Save_ThenLoad_RoundTripsEveryField()
        {
            var meta = new NiftiPatientMetadata
            {
                PatientId = "PID-1",
                PatientName = "Doe^Jane",
                PatientBirthDate = "19700101",
                PatientSex = "F",
                StudyInstanceUid = "1.2.3.4",
                StudyDate = "20260101",
                StudyTime = "101010",
                StudyId = "S1",
                AccessionNumber = "ACC1",
                ReferringPhysicianName = "Ref^Doc",
                FrameOfReferenceUid = "1.2.3.5",
                ImageModality = "PT",
                PatientPosition = "FFS",
                ImageSeriesInstanceUid = "1.2.3.6",
                ImageSopInstanceUids = new List<string> { "1.2.3.7", "1.2.3.8" },
                ImageRescaleSlope = 0.5,
                ImageRescaleIntercept = -1024,
            };

            _service.Save(_dir, meta);
            string saved = File.ReadAllText(MetadataPath);
            var loaded = _service.LoadOrSynthesize(_dir);

            Assert.Equal(JsonConvert.SerializeObject(meta), JsonConvert.SerializeObject(loaded));
            // Nothing needed defaulting, so the file was not rewritten.
            Assert.Equal(saved, File.ReadAllText(MetadataPath));
        }

        [Fact]
        public void PartialFile_KeepsWhatItSays_AndSynthesizesTheRest()
        {
            File.WriteAllText(MetadataPath, "{\"PatientId\":\"ABC\",\"ImageModality\":\"MR\"}");

            var meta = _service.LoadOrSynthesize(_dir);

            Assert.Equal("ABC", meta.PatientId);
            Assert.Equal("MR", meta.ImageModality);
            Assert.Equal("ANON^ANON", meta.PatientName);
            Assert.StartsWith("2.25.", meta.StudyInstanceUid);
            Assert.Contains("\"StudyInstanceUid\"", File.ReadAllText(MetadataPath));
        }

        [Fact]
        public void CorruptJson_IsTreatedAsMissing_AndRegenerated()
        {
            File.WriteAllText(MetadataPath, "{ this is not json");

            var meta = _service.LoadOrSynthesize(_dir);

            Assert.Matches("^ANON_", meta.PatientId);
            Assert.NotNull(JsonConvert.DeserializeObject<NiftiPatientMetadata>(File.ReadAllText(MetadataPath)));
        }

        [Fact]
        public void NullOrEmptyFolder_IsRefused()
        {
            Assert.Throws<ArgumentException>(() => _service.LoadOrSynthesize(null));
            Assert.Throws<ArgumentException>(() => _service.LoadOrSynthesize(""));
        }

        [Fact]
        public void BuildSyntheticRefDataset_CarriesTheIdentifiers()
        {
            var meta = new NiftiPatientMetadata { PatientId = "PID-2", StudyInstanceUid = "1.2.3.9", FrameOfReferenceUid = "1.2.3.10", PatientPosition = "HFP" };

            var ds = _service.BuildSyntheticRefDataset(meta);

            Assert.Equal("PID-2", ds.GetSingleValue<string>(FellowOakDicom.DicomTag.PatientID));
            Assert.Equal("1.2.3.9", ds.GetSingleValue<string>(FellowOakDicom.DicomTag.StudyInstanceUID));
            Assert.Equal("1.2.3.10", ds.GetSingleValue<string>(FellowOakDicom.DicomTag.FrameOfReferenceUID));
            Assert.Throws<ArgumentNullException>(() => _service.BuildSyntheticRefDataset(null));
        }
    }
}
