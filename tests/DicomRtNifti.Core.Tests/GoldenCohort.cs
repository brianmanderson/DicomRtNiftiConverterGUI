using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Xunit;

namespace DicomRtNifti.Core.Tests
{
    /// <summary>
    /// A small cohort written with fixed identifiers, so the documents the cohort modes print for
    /// it are the same bytes on every run and every machine once the temp root is masked. Every
    /// field of the three documents gets a value somewhere in it:
    ///
    ///   PAT_A  two CTs of one study, same description and no date, so their output folders
    ///          collide and both are suffixed; a structure set with two ROIs referencing the
    ///          larger CT by series UID; a dose on its own grid, linked by frame of reference.
    ///   PAT_B  a mixed-gap CT with one slice that has no ImagePositionPatient (the geometry
    ///          warnings, and F-12's unpositioned_slices); a structure set linked by frame.
    ///   PAT_C  a structure set in a study with no image series: unlinked.
    ///   PAT_D  a structure set whose frame matches nothing, linked to the largest series.
    ///   PAT_E  a structure set whose contour data is malformed: it scans, and fails to convert
    ///          (the errors list).
    ///
    /// SOPInstanceUIDs are random, but no document carries them.
    /// </summary>
    internal static class GoldenCohort
    {
        public const string Salt = "GOLDEN";

        public static void Write(string input)
        {
            // PAT_A
            string studyA = "1.2.3.100.1", frameA = "1.2.3.100.9";
            DicomTestData.WriteCtSeriesWithPixels(Path.Combine(input, "PAT_A", "ct_main"), "1.2.3.100.2", frameA,
                sliceCount: 4, size: 16, studyUid: studyA, patientId: "PAT_A");
            DicomTestData.WriteCtSeriesWithPixels(Path.Combine(input, "PAT_A", "ct_short"), "1.2.3.100.5", frameA,
                sliceCount: 3, size: 16, studyUid: studyA, patientId: "PAT_A");
            DicomTestData.WriteRtStructWithContours(Path.Combine(input, "PAT_A", "rt"), "rs.dcm",
                studyA, "1.2.3.100.3", frameA, new[]
                {
                    new KeyValuePair<string, double[]>("Target", DicomTestData.SquareContour(3, 10, 1)),
                    new KeyValuePair<string, double[]>("Ring", DicomTestData.SquareContour(5, 12, 2)),
                },
                patientId: "PAT_A", referencedSeriesUid: "1.2.3.100.2");
            var grid = new DoseGrid
            {
                Columns = 5, Rows = 3, Frames = 2,
                Origin = new[] { 1.75, 3.5, 0.5 },
                ColumnSpacing = 2.5, RowSpacing = 4.0, FrameSpacing = 2.0,
            };
            SyntheticRtDose.Write(Path.Combine(input, "PAT_A", "dose"), "dose.dcm", grid,
                SyntheticRtDose.FullRangeValues(grid, 16), 16, 0.0005, "GY",
                "PAT_A", studyA, "1.2.3.100.4", frameA, seriesDescription: "Plan dose");

            // PAT_B: slice index 6 (InstanceNumber 7) is written without ImagePositionPatient.
            string studyB = "1.2.3.200.1", frameB = "1.2.3.200.9";
            DicomTestData.WriteCtSeriesWithPixels(Path.Combine(input, "PAT_B", "ct"), "1.2.3.200.2", frameB,
                size: 16, studyUid: studyB, patientId: "PAT_B",
                sliceZsMm: new[] { 0.0, 2.0, 4.0, 10.0, 12.0, 20.0, 22.0 },
                ippOverrides: new Dictionary<int, string[]> { { 6, null } });
            DicomTestData.WriteRtStructWithContours(Path.Combine(input, "PAT_B", "rt"), "rs.dcm",
                studyB, "1.2.3.200.3", frameB,
                new[] { new KeyValuePair<string, double[]>("Target", DicomTestData.SquareContour(3, 10, 2)) },
                patientId: "PAT_B");

            // PAT_C
            DicomTestData.WriteRtStruct(CreateDir(input, "PAT_C"), "rs.dcm",
                "PAT_C", "1.2.3.300.1", "1.2.3.300.3", "1.2.3.300.9", "ORPHAN", new[] { "Orphan" },
                seriesDescription: "orphan structures");

            // PAT_D
            DicomTestData.WriteCtSeriesWithPixels(Path.Combine(input, "PAT_D", "ct"), "1.2.3.400.2", "1.2.3.400.9",
                sliceCount: 4, size: 16, studyUid: "1.2.3.400.1", patientId: "PAT_D");
            DicomTestData.WriteRtStructWithContours(Path.Combine(input, "PAT_D", "rt"), "rs.dcm",
                "1.2.3.400.1", "1.2.3.400.3", "1.2.3.400.8",
                new[] { new KeyValuePair<string, double[]>("Body", DicomTestData.SquareContour(2, 13, 0)) },
                patientId: "PAT_D");

            // PAT_E
            string dirE = Path.Combine(input, "PAT_E");
            DicomTestData.WriteCtSeriesWithPixels(dirE, "1.2.3.500.2", "1.2.3.500.9",
                sliceCount: 4, size: 16, studyUid: "1.2.3.500.1", patientId: "PAT_E");
            DicomTestData.AppendMalformedRoiContourSequence(DicomTestData.WriteRtStruct(dirE, "rs.dcm",
                "PAT_E", "1.2.3.500.1", "1.2.3.500.3", "1.2.3.500.9", "SET", new[] { "Target" }));
        }

        private static string CreateDir(string root, string name)
        {
            string dir = Path.Combine(root, name);
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>
    /// Expected outputs kept as files under <c>Goldens/</c> beside this source, compared after the
    /// two things that legitimately vary by machine are masked: the temp root (and the separators
    /// of the absolute paths under it) and line endings. Anything else that changes is a change to
    /// a published output and fails.
    ///
    /// To re-record one deliberately, set DICOMRTNIFTI_UPDATE_GOLDENS=1: the test writes what it
    /// got over the golden and still fails, so the diff has to be read and committed by hand.
    /// </summary>
    internal static class Goldens
    {
        public static string Normalize(string text, string root)
        {
            text = text.Replace("\r\n", "\n");
            string escaped = JsonConvert.ToString(root);
            escaped = escaped.Substring(1, escaped.Length - 2);
            text = text.Replace(escaped, "<ROOT>").Replace(root, "<ROOT>");
            // JSON-escaped Windows separators: only the absolute paths under the root carry them.
            return text.Replace("\\\\", "/");
        }

        public static void AssertMatches(string name, string actual)
        {
            string path = Path.Combine(Directory(), name);
            if (Environment.GetEnvironmentVariable("DICOMRTNIFTI_UPDATE_GOLDENS") == "1")
            {
                File.WriteAllText(path, actual);
                Assert.Fail($"Golden {name} re-recorded at {path}; review the diff and commit it.");
            }

            Assert.True(File.Exists(path), $"Golden {name} not found at {path}.");
            string expected = File.ReadAllText(path).Replace("\r\n", "\n");
            Assert.Equal(expected, actual);
        }

        /// <summary>The Goldens folder of the test project, found by walking up from the test binaries.</summary>
        private static string Directory()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "DicomRtNifti.Core.Tests.csproj")))
                    return Path.Combine(dir.FullName, "Goldens");
            }
            throw new DirectoryNotFoundException(
                "DicomRtNifti.Core.Tests.csproj not found above " + AppContext.BaseDirectory);
        }
    }
}
