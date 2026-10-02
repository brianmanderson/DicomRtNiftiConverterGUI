using System;
using System.Globalization;
using System.IO;
using System.Linq;
using DicomRtNifti.Core.Models;
using DicomRtNifti.Core.Services;
using FellowOakDicom;
using itk.simple;
using Newtonsoft.Json;
using Xunit;

namespace DicomRtNifti.Core.Tests
{
    /// <summary>
    /// NIfTI image volume -> DICOM image series, and back through the series loader: the pixel
    /// values and the geometry survive the trip, and the per-slice tags say what the volume said.
    /// </summary>
    public class NiftiImageWriterServiceTests
    {
        private static Image MakeCtVolume()
        {
            var img = new Image(8, 8, 5, PixelIDValueEnum.sitkInt16);
            img.SetSpacing(new VectorDouble(new[] { 0.8, 1.2, 2.5 }));
            img.SetOrigin(new VectorDouble(new[] { -10.0, -20.0, 30.0 }));
            for (uint z = 0; z < 5; z++)
                for (uint y = 0; y < 8; y++)
                    for (uint x = 0; x < 8; x++)
                        img.SetPixelAsInt16(new VectorUInt32(new[] { x, y, z }), (short)(-1000 + x + 10 * y + 100 * z));
            return img;
        }

        private static NiftiPatientMetadata Meta(string modality = "CT") => new NiftiPatientMetadata
        {
            PatientId = "P1",
            PatientName = "Test^Patient",
            StudyInstanceUid = DicomTestData.NewUid(),
            FrameOfReferenceUid = DicomTestData.NewUid(),
            ImageModality = modality,
        };

        [Fact]
        public void CtVolume_RoundTrips_PixelsAndGeometry()
        {
            string dir = DicomTestData.NewTempDir();
            try
            {
                using (var volume = MakeCtVolume())
                    SimpleITK.WriteImage(volume, Path.Combine(dir, "image.nii.gz"));

                var meta = Meta();
                var service = new NiftiImageWriterService(new NiftiMetadataService());
                var written = service.ConvertImageNiftiToDicomSeries(dir, meta, null, System.Threading.CancellationToken.None);

                Assert.Equal(5, written.Count);
                Assert.Equal(Enumerable.Range(1, 5).Select(i => $"image_{i:D4}.dcm"), written.Select(Path.GetFileName));
                Assert.Equal(5, meta.ImageSopInstanceUids.Distinct().Count());
                Assert.False(string.IsNullOrEmpty(meta.ImageSeriesInstanceUid));

                var persisted = JsonConvert.DeserializeObject<NiftiPatientMetadata>(File.ReadAllText(Path.Combine(dir, "metadata.json")));
                Assert.Equal(meta.ImageSeriesInstanceUid, persisted.ImageSeriesInstanceUid);

                for (int i = 0; i < written.Count; i++)
                {
                    var ds = DicomFile.Open(written[i]).Dataset;
                    Assert.Equal("CT", ds.GetSingleValue<string>(DicomTag.Modality));
                    Assert.Equal("ISO_IR 192", ds.GetSingleValue<string>(DicomTag.SpecificCharacterSet));
                    Assert.Equal(8, ds.GetSingleValue<int>(DicomTag.Rows));
                    Assert.Equal(8, ds.GetSingleValue<int>(DicomTag.Columns));
                    Assert.Equal(i + 1, ds.GetSingleValue<int>(DicomTag.InstanceNumber));
                    Assert.Equal(meta.ImageSeriesInstanceUid, ds.GetSingleValue<string>(DicomTag.SeriesInstanceUID));
                    Assert.Equal(meta.ImageSopInstanceUids[i], ds.GetSingleValue<string>(DicomTag.SOPInstanceUID));
                    Assert.Equal(meta.FrameOfReferenceUid, ds.GetSingleValue<string>(DicomTag.FrameOfReferenceUID));

                    // PixelSpacing is row spacing then column spacing: y, then x.
                    var ps = ds.GetValues<double>(DicomTag.PixelSpacing);
                    Assert.Equal(1.2, ps[0], 9);
                    Assert.Equal(0.8, ps[1], 9);
                    Assert.Equal(2.5, ds.GetSingleValue<double>(DicomTag.SliceThickness), 9);
                    var ipp = ds.GetValues<double>(DicomTag.ImagePositionPatient);
                    Assert.Equal(-10.0, ipp[0], 9);
                    Assert.Equal(-20.0, ipp[1], 9);
                    Assert.Equal(30.0 + 2.5 * i, ipp[2], 9);
                    Assert.Equal(new[] { 1.0, 0, 0, 0, 1, 0 }, ds.GetValues<double>(DicomTag.ImageOrientationPatient));
                }

                // Back through the loader the forward direction uses: same grid, same voxels.
                using (var original = MakeCtVolume())
                using (var loaded = DicomImageSeriesLoader.LoadCorrected(written.OrderBy(p => p, StringComparer.Ordinal).ToList()))
                using (var asInt16 = SimpleITK.Cast(loaded, PixelIDValueEnum.sitkInt16))
                {
                    Assert.Equal(original.GetSize().ToArray(), loaded.GetSize().ToArray());
                    for (int k = 0; k < 3; k++)
                    {
                        Assert.Equal(original.GetSpacing()[k], loaded.GetSpacing()[k], 6);
                        Assert.Equal(original.GetOrigin()[k], loaded.GetOrigin()[k], 6);
                    }
                    for (int k = 0; k < 9; k++)
                        Assert.Equal(original.GetDirection()[k], loaded.GetDirection()[k], 6);

                    for (uint z = 0; z < 5; z++)
                        for (uint y = 0; y < 8; y++)
                            for (uint x = 0; x < 8; x++)
                            {
                                var idx = new VectorUInt32(new[] { x, y, z });
                                Assert.Equal(original.GetPixelAsInt16(idx), asInt16.GetPixelAsInt16(idx));
                            }
                }
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void PtVolume_GetsAdaptiveSlope_AndUnsignedPixels()
        {
            string dir = DicomTestData.NewTempDir();
            try
            {
                using (var img = new Image(8, 8, 3, PixelIDValueEnum.sitkFloat32))
                {
                    img.SetSpacing(new VectorDouble(new[] { 1.0, 1.0, 1.0 }));
                    for (uint z = 0; z < 3; z++)
                        for (uint y = 0; y < 8; y++)
                            for (uint x = 0; x < 8; x++)
                                img.SetPixelAsFloat(new VectorUInt32(new[] { x, y, z }), (x + 8 * y + 64 * z) * 20.0f / 191.0f);
                    SimpleITK.WriteImage(img, Path.Combine(dir, "image.nii.gz"));
                }

                var meta = Meta("PT");
                var written = new NiftiImageWriterService(new NiftiMetadataService())
                    .ConvertImageNiftiToDicomSeries(dir, meta, null, System.Threading.CancellationToken.None);

                Assert.Equal(3, written.Count);
                Assert.Equal(20.0 / 65535.0, meta.ImageRescaleSlope, 12);
                var ds = DicomFile.Open(written[0]).Dataset;
                Assert.Equal("PT", ds.GetSingleValue<string>(DicomTag.Modality));
                Assert.Equal(0, ds.GetSingleValue<int>(DicomTag.PixelRepresentation));
                Assert.Equal("BQML", ds.GetSingleValue<string>(DicomTag.Units));
                // RescaleSlope is VR DS: the tag carries the slope at the writer's decimal precision.
                Assert.Equal(meta.ImageRescaleSlope, ds.GetSingleValue<double>(DicomTag.RescaleSlope), 6);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void SecondRun_SkipsWhenTheSeriesIsAlreadyPresent()
        {
            string dir = DicomTestData.NewTempDir();
            try
            {
                using (var volume = MakeCtVolume())
                    SimpleITK.WriteImage(volume, Path.Combine(dir, "image.nii.gz"));
                var meta = Meta();
                var service = new NiftiImageWriterService(new NiftiMetadataService());

                var first = service.ConvertImageNiftiToDicomSeries(dir, meta, null, System.Threading.CancellationToken.None);
                var log = new CollectingProgress();
                var second = service.ConvertImageNiftiToDicomSeries(dir, meta, log, System.Threading.CancellationToken.None);

                Assert.Equal(5, first.Count);
                Assert.Empty(second);
                Assert.Equal(5, Directory.GetFiles(dir, "image_*.dcm").Length);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void NoImageFile_ReturnsEmpty_AndWritesNothing()
        {
            string dir = DicomTestData.NewTempDir();
            try
            {
                var written = new NiftiImageWriterService(new NiftiMetadataService())
                    .ConvertImageNiftiToDicomSeries(dir, Meta(), null, System.Threading.CancellationToken.None);
                Assert.Empty(written);
                Assert.Empty(Directory.GetFiles(dir));
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void TwoDimensionalImage_IsRefused()
        {
            string dir = DicomTestData.NewTempDir();
            try
            {
                using (var flat = new Image(8, 8, PixelIDValueEnum.sitkInt16))
                    SimpleITK.WriteImage(flat, Path.Combine(dir, "image.nii.gz"));
                var ex = Assert.Throws<InvalidOperationException>(() =>
                    new NiftiImageWriterService(new NiftiMetadataService())
                        .ConvertImageNiftiToDicomSeries(dir, Meta(), null, System.Threading.CancellationToken.None));
                Assert.Contains("3D", ex.Message);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }
    }
}
