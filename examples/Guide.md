# Examples — from DICOM-RT to a research dataset, and back

This folder holds a worked, runnable example of the whole toolkit. The notebook is the example;
this guide is the map.

---

## What you will build

A public radiotherapy cohort — CT, structure sets **and dose** — converted to an analysis-ready
NIfTI dataset on a single voxel grid, with normalized structure names, a clinical-metadata
sidecar, a growable manifest, and an anonymization key. Then the reverse: masks back into a
DICOM RT structure set, with the cost of that round trip measured. Finally, an accuracy check
against analytically known geometry.

By the end you should be able to:

1. **Identify** what a real DICOM-RT study actually contains, and which of it you want.
2. **Choose** an output grid and structure naming that make a cohort comparable across patients.
3. **Carry** the clinical metadata that a NIfTI cannot hold on its own — dose included.
4. **Return** masks to DICOM, and say what that costs.

---

## What's in this folder

| File | What it is |
|---|---|
| [`Pancreatic_CT_CBCT_DICOM_RT_RoundTrip.ipynb`](Pancreatic_CT_CBCT_DICOM_RT_RoundTrip.ipynb) | The runnable example. Downloads the cohort and executes the whole pipeline. |
| `associations_pancreas.json` | Structure-name mappings for this cohort. The same format the desktop app imports and exports. |
| [`GUI_WALKTHROUGH.md`](GUI_WALKTHROUGH.md) | The same workflows done by hand in the desktop application. |
| `Guide.md` | This document. |

---

## Quick start

1. **Open the notebook** and run it top to bottom.
2. **You do not need .NET installed.** Section 0 downloads a self-contained CLI build for your
   platform, with the SimpleITK native already inside it. Set `USE_LOCAL_BUILD = True` only if
   you are developing the toolkit itself.
3. **Start small.** `N_PATIENTS` defaults to 30 of the collection's 40. Drop it to 2 or 3 for a
   first pass — everything downstream behaves identically, just faster.

> **Windows path length.** `WORK_DIR` defaults to `C:/rt_ex` rather than somewhere under this
> repository, deliberately. The downloaded tree nests a 64-character `SeriesInstanceUID` below
> it, and a deep base path pushes the result past the 260-character limit — which surfaces as
> unreadable files rather than as an obvious error. Keep it short.

---

## The dataset

[**Pancreatic-CT-CBCT-SEG**](https://www.cancerimagingarchive.net/collection/pancreatic-ct-cbct-seg/)
— 40 patients, planning CT plus aligned cone-beam CTs, structure sets, and dose.
DOI [10.7937/TCIA.ESHQ-4D90](https://doi.org/10.7937/TCIA.ESHQ-4D90) · **CC BY 4.0** · no login required.

Chosen over the more common lung cohorts for two reasons:

- **Every patient has an RTDOSE.** Most public segmentation collections ship images and contours
  only, which makes the dose half of a radiotherapy pipeline impossible to demonstrate.
- **Every patient is a realistically awkward study.** Five series all reporting `Modality=CT`
  (one planning CT, several CBCTs resampled onto its grid), three structure sets, one dose — all
  under a single study, all sharing a frame of reference. Picking the right one is a real
  decision, not a formality, and §3 is about making it deliberately.

Despite the name, the segmentations are **RT structure sets, not DICOM SEG objects**. See
`DicomSegOverview.md` in the parent repository for how the two formats differ and when you would
want each.

---

## The pipeline, section by section

| § | Step | What it does |
|---|---|---|
| 0 | **Setup** | Resolve a CLI binary; assert the SimpleITK native loads. |
| 1 | **Download** | Pull the first *N* patients that have image, structures and dose. |
| 2 | **Discover** | `--cohort-scan` builds the patient/study/series tree and links each structure set and dose to its image series, recording *how confident* each link is. |
| 3 | **Choose** | Resolve the planning-CT-versus-CBCT ambiguity with `--struct-description BSPC`. Slice count cannot do it — the CBCTs are resampled onto the planning grid and tie exactly. |
| 4 | **Survey** | `--cohort-manifest` writes one CSV row per series: spacing plus per-ROI volume in cc. |
| 5 | **Spot outliers** | Read the manifest as a QC instrument — odd spacing, odd volumes, missing structures, non-uniform slice gaps. |
| 6 | **Normalize** | Map inconsistent structure names onto canonical labels. |
| 7 | **Set the grid** | One output voxel size for the whole cohort. Linear for image and dose, nearest-neighbour for masks. |
| 8 | **Keep metadata** | Choose the DICOM tags and computed values to carry, in three sections — image, structures, **dose**. |
| 9 | **Convert** | One call: image, masks, doses, `metadata.json`, manifest, anonymization key. |
| 10 | **Verify** | Confirm image and masks share a grid; read the sidecar; look at a slice with the structure and the dose overlaid. Dose keeps its own extent, so it is resampled onto the image before overlaying. |
| 11 | **Grow** | Re-run and confirm the deterministic salt and merging manifest extend the dataset rather than duplicating it. |
| 12 | **Reverse** | Masks back to an RT structure set; measure the round-trip Dice and volume drift. |
| 13 | **Prove it** | Convert analytically-known shapes and verify against closed-form ground truth — the same gate the project runs in CI. |

Sections 8–10, 12 and 13 have no counterpart in the pure-Python Session 1 workflow this example
otherwise mirrors, because that toolchain does not carry dose, does not write DICOM, and has no
analytic conformance suite.

---

## Output layout

```
nifti/
  <patient>/<study>/<series>/          # hashed identifiers when --anonymize is set
    image.nii.gz
    masks/
      Pancreas.nii.gz  Duodenum.nii.gz  SpinalCord.nii.gz  ...
    doses/
      Eclipse Doses.nii.gz             # every linked dose, not just the first
    metadata.json                      # ImageAttributes / StructureAttributes / DoseAttributes
  export_manifest.csv                  # identifiers, spacing, per-ROI volume (cc)
  AnonymizationKey.json                # reverse lookup — keep this OUT of git
```

Masks are rasterized onto the image grid, so they are voxel-aligned with `image.nii.gz`. **Dose
is not**: it is resampled to the same *spacing* but keeps its own origin and extent, because a
dose grid usually covers only the region around the target. Resample it onto the image before
combining the two — the notebook's §10 shows the one-line `sitk.Resample` that does it.

> **Use one `--salt` for a cohort, on every command.** The manifest merges on its identifier
> columns, so surveying with real identifiers and converting with hashed ones appends a second
> set of rows rather than updating the first — leaving real patient identifiers in the cohort
> root beside an anonymized export.

---

## A note on privacy

`AnonymizationKey.json` maps each hash back to the source identifier. **It is re-identification
data.** Store it access-controlled, keep it out of version control, and delete it when you no
longer need it. This folder's `.gitignore` already excludes it along with all imaging data.

Two related traps the notebook calls out:

- **Metadata tags are copied verbatim.** Requesting `PatientName` or `PatientID` writes the
  identifiers straight back into a series whose folder name you just hashed.
- **Notebook output gets committed.** The cohort JSON contains only hashes under `--anonymize`,
  including for skipped and unlinked series, so printing it is safe. That is a deliberate
  property, not an accident — but it only protects the JSON, not any cell where you print the
  source DICOM yourself.

---

## Links

- **Tool:** [DicomRtNiftiConverterGUI](https://github.com/brianmanderson/DicomRtNiftiConverterGUI) — GUI + headless CLI, .NET 8, Windows/Linux/macOS
- **Data:** [Pancreatic-CT-CBCT-SEG](https://www.cancerimagingarchive.net/collection/pancreatic-ct-cbct-seg/) · CC BY 4.0
- **Conformance suite:** [RTMaskConformanceTest](https://github.com/brianmanderson/RTMaskConformanceTest)
- **Benchmark harness:** `PythonCode/README.md` in the parent repository — the six-tool comparison behind the methodology paper
- **Related teaching material:** [AAPM 2026 *From Pixels to Patients*](https://github.com/brianmanderson/AAPM2026_PixelsToPatients), which teaches the forward pipeline in pure Python
