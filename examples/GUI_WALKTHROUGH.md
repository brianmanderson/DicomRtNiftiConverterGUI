# The same workflows, in the desktop application

The [notebook](Pancreatic_CT_CBCT_DICOM_RT_RoundTrip.ipynb) drives the headless CLI because a
notebook needs a scriptable interface. Every one of those steps also exists as a point-and-click
workflow in the desktop app, and for a one-off conversion the GUI is usually the faster route.

This document mirrors the app's built-in **Help** windows (the `?` button in each direction's
window), with the equivalent CLI flag noted alongside each control so you can move between them.

> The app's Help windows remain the authoritative reference and are updated with the UI. This
> file exists so the same material is linkable, diffable, and readable without launching the
> application. If the two ever disagree, the Help window is right.

---

## Launching

```bash
dotnet run --project src/DicomRtNifti.App
```

Or download `DicomRtNifti-gui-<platform>` from the
[releases page](https://github.com/brianmanderson/DicomRtNiftiConverterGUI/releases) — those
builds are self-contained and need no .NET install.

A launcher window offers the two directions: **DICOM → NIfTI** (forward) and **NIfTI → DICOM**
(reverse).

---

# DICOM → NIfTI

## Quick start

1. Set **Input** — your DICOM archive, scanned recursively — and **Output**, where the NIfTI
   files land.
2. Click **Scan**. The Patient / Study / Series tree fills in.
3. Tick the patients, or individual series, you want.
4. In the bottom action bar, tick the artifact types: **Export Images**, **Export Structures**
   (RT-STRUCT → per-ROI masks under `masks/`), **Export Dose** (RT-DOSE → one file per dose under
   `doses/{SeriesDescription}.nii.gz`).
5. Click **Convert Selected**. A status bar shows progress; the log box records each step.

Defaults give you images, structures and dose for every selected series, with ROIs keeping their
original DICOM names, the original voxel spacing preserved, and patient identifiers preserved in
the folder names.

## Workflow walkthroughs

### A. Plain export of one cohort

The five steps above, unchanged. Equivalent to:

```bash
--cohort-convert --input ARCHIVE --output OUT
```

### B. Anonymized batch for research

Open **Export Options** and tick **Anonymize export** before Convert Selected. Each exported
identifier — MRN, Study UID, Series UID — is replaced by a deterministic per-identifier hash, and
the output layout uses those hashes in place of the patient and series folder names. An
`AnonymizationKey.json` is written at the output root mapping each hash back to its original.

Re-exporting the same series in a later session — even into a different output folder that
already has an `AnonymizationKey.json` — reuses the previously assigned hash. That is what makes
a cohort growable rather than duplicated, and it is the same property the notebook relies on in
§11.

```bash
--cohort-convert --input ARCHIVE --output OUT --anonymize --salt "your-salt"
```

### C. Restricted ROI cohort with canonical renames

Use this when different patients call the same structure different names and you want one
canonical mask per ROI.

1. Open **Edit ROI Associations…** and define alias sets — canonical `Pancreas` ← `pancreas`,
   `PANCREAS`, `Pancreas_Ant`.
2. Tick **Limit export to selected ROIs**.
3. Open **Select ROIs for Export…** and tick the canonical names you want. Patients whose series
   contain none of them are auto-deselected in the tree.
4. Convert Selected. Matched ROIs are renamed to their canonical name on disk; unmatched ROIs are
   dropped.

Without **Limit export to selected ROIs**, matched ROIs are still renamed, but unmatched ones are
also exported under their original names.

```bash
--cohort-convert --input ARCHIVE --output OUT \
    --associations associations_pancreas.json --only-associated-rois
```

The association file the GUI exports is exactly the file `--associations` reads. Curate in the
GUI, then hand it to an unattended run.

### D. Inventory pass, writing no `.nii.gz` files

Set up the export options as for a full export, then click **Export Manifest Only**. The
rasterizer runs against each selected RT-STRUCT to compute per-ROI volumes in cc, image spacings
are read, and nothing is written but the CSV manifest at the output root. Good for cohort sizing,
ROI prevalence audits, and spacing surveys.

```bash
--cohort-manifest --input ARCHIVE --output OUT          # add --no-volumes to skip rasterizing
```

Note the manifest still costs a rasterization pass, in either front-end. There is no analytic
contour-area shortcut — a volume in cc means the mask was built.

### E. Resampled to a target voxel spacing

Tick **Resample to fixed spacing**, click **Set Spacing…**, and enter X / Y / Z in mm. On Convert
Selected every output volume is resampled to that grid, and the CSV manifest reflects the
**target** spacing rather than the source spacing.

```bash
--cohort-convert --input ARCHIVE --output OUT --output-spacing 1.0,1.0,3.0
```

## Reference: the controls

| Control | What it does | CLI |
|---|---|---|
| **Input** | DICOM archive, scanned recursively — every file is opened | `--input` |
| **Output** | Where `.nii.gz` files and the manifest are written; created if absent | `--output` |
| **Scan** | Walks the input in parallel and builds the Patient → Study → Series tree | `--cohort-scan` |
| **Stop** | Aborts an in-progress scan or conversion | — |
| **Export Images / Structures / Dose** | Which artifact types to write | `--no-images` / `--no-structures` / `--no-doses` invert these |
| **Modality scope** | Which image modalities to include | — |
| **Limit export to selected ROIs** | Drop ROIs that match no association | `--only-associated-rois` |
| **Edit ROI Associations…** | Canonical-name and alias editor | `--associations FILE.json` |
| **Resample to fixed spacing** + **Set Spacing…** | Target voxel grid in mm | `--output-spacing X,Y,Z` |
| **Anonymize export** + **Edit Anonymization Key…** | Hash identifiers; write the key file | `--anonymize --salt` |
| **Convert Selected** | Run the full export | `--cohort-convert` |
| **Export Manifest Only** | Survey without writing volumes | `--cohort-manifest` |

The tree shows a checkbox at the patient and series level — studies are a structural grouping
only — and a modality badge in brackets on each series row.

### Selecting among several image series

The GUI shows slice counts in the tree, so you can see at a glance which series is the planning
CT and which are the shorter aligned CBCTs, and tick accordingly. The CLI has no tree to look at,
which is why it has `--series-description` and `--prefer-largest-series` instead. Same decision,
made once in advance rather than interactively.

## Output structure

**Non-anonymized:**

```
{Output}/{PatientID}/{SeriesDate}_{SeriesDescription}/
    image.nii.gz
    masks/{ROIName}.nii.gz
    doses/{SeriesDescription}.nii.gz
    metadata.json
```

**Anonymized** — the same tree with a three-level hash triple in place of the patient and series
folders, plus `AnonymizationKey.json` at the output root.

The CSV manifest sits at the output root either way. Its first six columns are fixed —
`PatientID, StudyUID, SeriesUID, SpacingX, SpacingY, SpacingZ` — and every column after them is
one ROI's volume in cc, with `-1` marking a structure that series does not have. Re-running merges
into an existing manifest rather than regenerating it: rows are matched on the three identifier
columns, and new ROI columns are appended without disturbing the existing ones.

---

# NIfTI → DICOM

## Quick start

1. Point **Browse…** at a folder of per-ROI masks, then **Scan**.
2. The discovered-jobs grid lists what was found and what each job will produce.
3. Tick **Convert Images / Convert Structures / Convert Dose** as needed.
4. **Convert All**.

## Workflow walkthroughs

### A. Round-trip from a prior forward export

Point the reverse window at a case folder produced by the forward direction. `metadata.json` is
already there, so patient, study and frame-of-reference identifiers carry across and the
regenerated RT-STRUCT overlays the original study.

```bash
--reverse --masks-folder CASE/masks --image-nifti CASE/image.nii.gz \
          --metadata CASE/metadata.json --output regenerated.dcm
```

### B. Masks only, attaching to an existing DICOM image series

When you have a real reference series, point at it and the writer reuses its per-slice SOP
Instance UIDs, so the structure set references the actual images rather than a synthesized copy.

```bash
--reverse --image-folder REFERENCE_DICOM --masks-folder MASKS --output regenerated.dcm
```

### C. Drop-folder / watch mode

**Run Server** watches a folder and converts new cases as they appear — the shape of an
inference-service integration, where a model writes masks and the toolkit turns them into
structure sets without anyone clicking anything. There is no CLI equivalent; run the app.

### D. Batch over many patient folders

Point at a parent directory and the scan discovers each case beneath it. Scripted, this is a loop
over `--reverse`.

## `metadata.json`, and what it is for

A NIfTI carries a grid and an affine. It does not carry a patient, a study, a frame of reference,
or a rescale slope. `metadata.json` is where those live, and it is what lets the reverse direction
produce a DICOM object that belongs to a specific patient rather than a free-floating one.

When it is absent the app synthesizes one, generating fresh UIDs. That is fine for a standalone
export and wrong if you intended the result to attach to an existing study — so if a regenerated
structure set does not show up alongside the original images, this file is the first thing to check.

It is also persisted back next to the masks after a run, so repeated conversions reuse the same
UIDs instead of creating a new series each time.

## Reference: status values and output

The jobs grid reports per-case status as the run proceeds. On completion:

- The **RT-STRUCT writer** emits `CLOSED_PLANAR` contours for every structure. This is a real
  limitation and worth understanding: a binary mask does not record the original
  `ContourGeometricType`, so an open non-planar applicator track round-trips into a filled
  polygon. That is a property of the mask representation, not of this implementation, and it
  applies to every tool that goes through one.
- The **RT-DOSE writer** applies the rescale slope and intercept from `metadata.json`.
- **Image idempotency:** re-running does not regenerate an image series that already matches.

---

## Which should you use?

Use the **GUI** for one-off conversions, for exploring an unfamiliar archive, and for curating
ROI associations — the tree view answers "what is actually in here?" far faster than reading JSON.

Use the **CLI** when the run must be repeatable: a cohort you will re-export as it grows, a
pipeline step, or anything that belongs in CI. Both front-ends call the same conversion services,
so the outputs agree.
