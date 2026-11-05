using System.IO;
using DicomRtNifti.Core.Tests;

namespace DicomRtNifti.Web.Tests.Support
{
    /// <summary>
    /// A synthesized cohort that exercises every part of the Inventory and every alias a scan
    /// snapshot must keep. PAT_A's first study holds two CTs in two frames of reference: a uniform
    /// one with a structure set that names it by series UID and a dose linked by frame, and a
    /// non-uniform one with a structure set linked by frame. Its second study holds a structure set
    /// and no image series, so it is unlinked. PAT_B is a plain CT with a structure set.
    /// </summary>
    internal sealed class InventoryCohort
    {
        public const string PatientA = "PAT_A";
        public const string PatientB = "PAT_B";

        private InventoryCohort(string root)
        {
            Root = root;
        }

        /// <summary>The cohort folder.</summary>
        public string Root { get; }

        public string UniformCt { get; private set; }

        public string NonUniformCt { get; private set; }

        public string StructByUid { get; private set; }

        public string StructByFrame { get; private set; }

        public string Dose { get; private set; }

        public string UnlinkedStruct { get; private set; }

        /// <summary>Writes the cohort under <paramref name="root"/>.</summary>
        public static InventoryCohort Write(string root)
        {
            var cohort = new InventoryCohort(root);
            string a = Path.Combine(root, PatientA);
            string study = DicomTestData.NewUid(), frame1 = DicomTestData.NewUid(), frame2 = DicomTestData.NewUid();
            cohort.UniformCt = DicomTestData.NewUid();
            cohort.NonUniformCt = DicomTestData.NewUid();
            cohort.StructByUid = DicomTestData.NewUid();
            cohort.StructByFrame = DicomTestData.NewUid();
            cohort.Dose = DicomTestData.NewUid();
            cohort.UnlinkedStruct = DicomTestData.NewUid();

            string uniform = Path.Combine(a, "ct1");
            Directory.CreateDirectory(uniform);
            for (int i = 0; i < 4; i++)
                DicomTestData.WriteImageSlice(uniform, $"s{i}.dcm", "CT", PatientA, study, cohort.UniformCt, frame1, i * 2.0);

            // Gaps of 1 and 3 mm: the conversions warn, so the series is badged non-uniform Z.
            string nonUniform = Path.Combine(a, "ct2");
            Directory.CreateDirectory(nonUniform);
            double[] zs = { 0, 1, 2, 5, 6 };
            for (int i = 0; i < zs.Length; i++)
                DicomTestData.WriteImageSlice(nonUniform, $"s{i}.dcm", "CT", PatientA, study, cohort.NonUniformCt, frame2, zs[i]);

            string rt = Path.Combine(a, "rt");
            Directory.CreateDirectory(rt);
            DicomTestData.WriteRtStruct(rt, "rs1.dcm", PatientA, study, cohort.StructByUid, frame1, "PLAN",
                new[] { "GTV", "Lung_L" }, referencedSeriesUid: cohort.UniformCt, seriesDescription: "Planning structures");
            DicomTestData.WriteRtStruct(rt, "rs2.dcm", PatientA, study, cohort.StructByFrame, frame2, "REPLAN",
                new[] { "CTV" }, seriesDescription: "Replan structures");
            DicomTestData.WriteRtDose(rt, "rd1.dcm", PatientA, study, cohort.Dose, frame1);

            string orphan = Path.Combine(a, "orphan");
            Directory.CreateDirectory(orphan);
            DicomTestData.WriteRtStruct(orphan, "rs.dcm", PatientA, DicomTestData.NewUid(), cohort.UnlinkedStruct, DicomTestData.NewUid(),
                "ORPHAN", new[] { "Body" }, seriesDescription: "Orphan structures");

            DicomTestData.WritePatientWithStructureSet(root, PatientB, damagedContours: false);
            return cohort;
        }
    }
}
