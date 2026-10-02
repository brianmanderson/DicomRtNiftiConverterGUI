using System;
using DicomRtNifti.Core.Services;
using itk.simple;
using Xunit;

namespace DicomRtNifti.Core.Tests
{
    /// <summary>
    /// Modality inference from a NIfTI's pixel type and value range (the --modality auto path).
    /// Volumes stay tiny so the sampler sees every voxel and no case depends on stride luck.
    /// </summary>
    public class NiftiModalityInferenceTests
    {
        private static Image Int16Volume(short fill, short? oneVoxel = null)
        {
            var img = new Image(4, 4, 4, PixelIDValueEnum.sitkInt16);
            for (uint z = 0; z < 4; z++)
                for (uint y = 0; y < 4; y++)
                    for (uint x = 0; x < 4; x++)
                        img.SetPixelAsInt16(new VectorUInt32(new[] { x, y, z }), fill);
            if (oneVoxel.HasValue)
                img.SetPixelAsInt16(new VectorUInt32(new uint[] { 1, 2, 3 }), oneVoxel.Value);
            return img;
        }

        private static Image Float32Volume(float min, float max)
        {
            var img = new Image(4, 4, 4, PixelIDValueEnum.sitkFloat32);
            int n = 0;
            for (uint z = 0; z < 4; z++)
                for (uint y = 0; y < 4; y++)
                    for (uint x = 0; x < 4; x++, n++)
                        img.SetPixelAsFloat(new VectorUInt32(new[] { x, y, z }), min + (max - min) * n / 63f);
            return img;
        }

        private static Image UInt16Volume(ushort min, ushort max)
        {
            var img = new Image(4, 4, 4, PixelIDValueEnum.sitkUInt16);
            int n = 0;
            for (uint z = 0; z < 4; z++)
                for (uint y = 0; y < 4; y++)
                    for (uint x = 0; x < 4; x++, n++)
                        img.SetPixelAsUInt16(new VectorUInt32(new[] { x, y, z }), (ushort)(min + (max - min) * n / 63));
            return img;
        }

        [Fact]
        public void Int16WithAirValue_IsCt()
        {
            using (var img = Int16Volume(40, -1000)) Assert.Equal("CT", NiftiModalityInferenceService.Infer(img));
        }

        [Fact]
        public void Int16SmallNegativeMinimum_IsCt()
        {
            using (var img = Int16Volume(200, -50)) Assert.Equal("CT", NiftiModalityInferenceService.Infer(img));
        }

        [Fact]
        public void UInt16LargePositiveRange_IsMr()
        {
            using (var img = UInt16Volume(0, 3000)) Assert.Equal("MR", NiftiModalityInferenceService.Infer(img));
        }

        [Fact]
        public void Float32SmallPositiveRange_IsPt()
        {
            using (var img = Float32Volume(0f, 20f)) Assert.Equal("PT", NiftiModalityInferenceService.Infer(img));
        }

        [Fact]
        public void Float32LargeRange_IsMr()
        {
            using (var img = Float32Volume(0f, 500f)) Assert.Equal("MR", NiftiModalityInferenceService.Infer(img));
        }

        [Fact]
        public void Float32WithAirValue_IsCt()
        {
            using (var img = Float32Volume(-1000f, 500f)) Assert.Equal("CT", NiftiModalityInferenceService.Infer(img));
        }

        [Fact]
        public void Null_IsRefused()
        {
            Assert.Throws<ArgumentNullException>(() => NiftiModalityInferenceService.Infer(null));
        }
    }
}
