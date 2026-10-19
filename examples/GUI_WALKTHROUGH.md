# The same workflows, in the desktop application

The [notebook](Pancreatic_CT_CBCT_DICOM_RT_RoundTrip.ipynb) drives the headless CLI because a
notebook needs a scriptable interface. Every one of those steps also exists as a point-and-click
workflow in the desktop app, and for a one-off conversion the GUI is usually the faster route.

This document mirrors the app's built-in **Help** windows (the **Help** button at the top right of
each direction's window), with the equivalent CLI flag noted alongside each control so you can
move between them.

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
(reverse). The app follows the operating system's light or dark theme. Each window remembers its
folders and checkboxes across launches: they are saved the moment you change them, the export
toggles to `settings.json` and the folders (plus the reverse window's Convert toggles) to
`gui_state.json`, both under `%AppData%\DicomToNifti\` (XDG config on Linux and macOS).

---

# DICOM → NIfTI

## Quick start

1. Set **Input** — your DICOM archive, scanned recursively — and **Output**, where the NIfTI
   files land.
2. Click **Scan**. The Patient / Study / Series tree fills in. Rows badged **non-uniform Z** or
   **link guessed** deserve a look first; click a series to see it in the **Series inspector**
   (below).
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
`AnonymizationKey.json` mapping each hash back to its original is written beside the export, in
`<output folder>.key\`, never inside it, so the export folder can be shared on its own. **Key
location** in Export Options puts it elsewhere; a location inside the export is refused.

Re-exporting the same series in a later session reuses the previously assigned hash. That is what
makes a cohort growable rather than duplicated, and it is the same property the notebook relies on
in §11.

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

The survey reads each series on the same flattened grid a conversion writes, so it logs the same
slice-spacing and missing-position warnings Convert Selected does, and a series with mixed slice
gaps carries the same volume error in its manifest row.

### E. Resampled to a target voxel spacing

Tick **Resample to fixed spacing**, click **Set Spacing…**, and enter X / Y / Z in mm. On Convert
Selected every output volume is resampled to that grid, and the CSV manifest reflects the
**target** spacing rather than the source spacing.

```bash
--cohort-convert --input ARCHIVE --output OUT --output-spacing 1.0,1.0,3.0
```

## Reference: the controls

Main window:

| Control | What it does | CLI |
|---|---|---|
| **Input** | DICOM archive, scanned recursively — every file is opened | `--input` |
| **Output** | Where `.nii.gz` files and the manifest are written; created if absent | `--output` |
| **Scan** | Walks the input in parallel and builds the Patient → Study → Series tree | `--cohort-scan` |
| **Stop** | Aborts an in-progress scan or conversion | — |
| **Select all** | Ticks every patient in the tree | (the CLI converts everything it plans; narrow it with `--patients`) |
| **Series inspector** | Geometry, RT links and curated tags of the series selected in the tree | `--cohort-scan` (per-series spacing, uniformity and link rule in the JSON) |
| **Export Images / Structures / Dose** | Which artifact types to write | `--no-images` / `--no-structures` / `--no-doses` invert these |
| **Export Options…** | Opens the panel below; stays open while you work | — |
| **Convert Selected** | Run the full export | `--cohort-convert` |
| **Export Manifest Only** | Survey without writing volumes | `--cohort-manifest` |
| **Settings…** | Default output directory (used until another output folder has been used; the last one is restored after that); open the output folder after a conversion | — (`%AppData%\DicomToNifti\settings.json`) |

Export Options panel:

| Control | What it does | CLI |
|---|---|---|
| **Edit ROI Associations…** | Canonical-name and alias editor; always applies | `--associations FILE.json` |
| **Limit export to selected ROIs** + **Select ROIs for Export…** | Drop ROIs that match no association; choose which canonical names to keep | `--only-associated-rois` |
| **Resample to fixed spacing** + **Set Spacing…** | Target voxel grid in mm | `--output-spacing X,Y,Z` |
| **Anonymize export** + **Edit Anonymization Key…** | Hash identifiers; write (and hand-override) the key file | `--anonymize --salt` |
| **Key location** + **Browse…** | Where the key file goes; empty for `<output folder>.key\` beside the export; refused inside the export | `--key-path FILE\|FOLDER` |
| **Write DICOM tag sidecar (metadata.json)** + **Select Metadata Tags…** | Per-series `metadata.json` of chosen DICOM attributes and computed values, in three sections | `--metadata-tags` / `--metadata-structure-tags` / `--metadata-dose-tags` |

**The key editor refuses a key file whose recorded salt is not the one in Settings**, and says so
on open rather than after you have edited it. The salt is half of every hash in that file, so
rewriting it under a different one would give already-exported patients a second, unlinkable
pseudonym. Change `HashSalt` back, or point the export at a fresh key location. The CLI refuses the
same mismatch, before it walks the input tree.

The tree shows a checkbox at the patient and series level — studies are a structural grouping
only — and a modality badge in brackets on each series row. Two amber badges flag what the scan
found, on the series row and, counted, on its patient row so they show while the tree is collapsed;
the scan log counts them too:

- **non-uniform Z** — the series' slice gaps are mixed. NIfTI stores one spacing per axis, so the
  export flattens them and every volume computed from the output is off. The badge appears exactly
  when Convert Selected and Export Manifest Only would log the warning (hover it to read it).
- **link guessed** — an RT-STRUCT or RT-DOSE was attached by the largest-series fallback: neither
  its referenced series UID nor its frame of reference matched an image series in the study.

**Modality scope is fixed, not a control.** Only `CT`, `MR` and `PT` series are linked to
RT-STRUCT and RT-DOSE during the scan. Other modalities still appear in the tree and still export
as `image.nii.gz`, but no structures or dose are associated with them.

**Selected tags are de-identified under Anonymize export.** Patient ID and the Study and Series
Instance UIDs carry the export's hashes, and names, dates, times, ages, instance UIDs and
identifying attributes are written as null. Without Anonymize export they are written as read.

### The series inspector

Click a series row and the panel to the right of the tree describes it from the headers the scan
already read; nothing is converted.

![The forward window after scanning a synthetic phantom CT with ten slices removed: the patient and
series rows carry the non-uniform Z badge, and the series inspector shows gaps of 1 and 2 mm, the
RTSTRUCT linked by referenced series UID, and the conversion warning](images/gui-series-inspector.png)

| Field | What it shows |
|---|---|
| **Slice spacing** | The median gap between consecutive slices (a regular-grid reader reports first-to-last distance over slices − 1 instead) |
| **Distinct gaps** | Every different slice gap. One value is a regular grid; two values without a badge differ only by rounding (0.62 / 0.63 mm on a 0.625 mm grid), under the 5% volume effect the warning starts at |
| **Pixel spacing**, **Z extent** | In-plane spacing; first-to-last slice distance with its z range |
| **Frame of reference**, **Series UID** | Selectable, for copying |
| **RTSTRUCT**, **RTDOSE** | How many are linked, and by which rule: referenced series UID (authoritative), frame of reference UID (ties go to the series with most slices), or largest-series fallback (a guess) |

Amber notes repeat, word for word, the warnings a conversion of the series logs. Below them, every
linked RT object is listed with its ROI or file count and link rule. The **DICOM tags** tab is a
read-only grid of curated attributes from the series' first slice and its first linked RT-STRUCT and
RT-DOSE: the lists **Select Metadata Tags…** offers, under the names the `metadata.json` sidecar
uses.

![The DICOM tags tab of the series inspector: section, attribute and value for the curated image
tags of the same phantom series](images/gui-series-inspector-tags.png)

`--cohort-scan` reports the same geometry per series in its JSON (`slice_spacing`,
`slice_spacing_values`, and the link rule as `match_rule`). Its `slice_spacing_uniform` is the strict
test, false for rounding-only differences too, where the GUI badge uses the conversion warning's 5%
threshold.

### Selecting among several image series

The GUI shows slice counts in the tree, so you can see at a glance which series is the planning
CT and which are the shorter aligned CBCTs, and tick accordingly; the inspector shows which rule
linked each structure set, so a set attached by frame of reference alone (the CBCT case) is visible
before export. The CLI has no tree to look at,
which is why it has filters instead — `--struct-description` first (structure sets are named for
what they were drawn on, and CBCTs resampled onto the planning grid are otherwise
indistinguishable), then `--series-description`, with `--prefer-largest-series` as a last resort
that ties exactly in that resampled case. Same decision, made once in advance rather than
interactively.

## Output structure

**Non-anonymized:**

```
{Output}/{PatientID}/{SeriesDate}_{SeriesDescription}/
    image.nii.gz                     # Export Images
    masks/{ROIName}.nii.gz           # Export Structures
    doses/{SeriesDescription}.nii.gz # Export Dose, and a dose is linked
    metadata.json                    # Write DICOM tag sidecar, with tags selected
```

Each line appears only when its toggle is on, as noted. `{ROIName}` is the ROI's own name when
that is already a valid file name; one that had to be sanitized for Windows gets a short hash of
the exact ROI name appended, so two ROIs differing only in stripped characters never land on one
file — see [Read the mask path from stdout](../README.md#headless-mode) for the rule and why it
does not depend on which ROIs you selected.

**Anonymized** — the same tree with a three-level hash triple in place of the patient and series
folders. `AnonymizationKey.json` is not in the tree: it is written beside it, in
`{Output}.key\` (or the Key location), so the export can be shared without it. That key file holds
the salt as well as the hash→identifier maps: it is re-identification data, so keep it out of
version control.

The CSV manifest sits at the output root either way — `export_manifest.csv` from Convert Selected,
`export_manifest_meta.csv` from Export Manifest Only (the CLI writes `export_manifest.csv` for
both, so a scripted survey and conversion into one root merge into a single file). Its first six columns are fixed —
`PatientID, StudyUID, SeriesUID, SpacingX, SpacingY, SpacingZ` — and every column after them is
one ROI's volume in cc, with `-1` marking a structure that series does not have. Re-running merges
into an existing manifest rather than regenerating it: rows are matched on the three identifier
columns, and new ROI columns are appended without disturbing the existing ones.

---

# NIfTI → DICOM

## Quick start

1. Set **Folder** (via **Browse…**) to a case folder — or to a parent holding several of them —
   then **Scan**. A case folder is one holding at least one of `image.nii.gz`, `masks/*.nii.gz`,
   or `doses/*.nii.gz` — see [Reverse-mode folder layout](../README.md#reverse-mode-folder-layout-nifti---dicom).
2. The discovered-jobs grid lists what was found (Folder, Image, Masks, ROI Names, Doses, Dose
   Files) and a per-case Status.
3. Tick **Convert Images / Convert Structures / Convert Dose** as needed.
4. **Convert All** — or **Run Server** to watch the folder instead (workflow C). As in the forward
   window, a progress bar counts finished cases and a log box records each case's outcome, with the
   full text of any error (the grid's Status cell is trimmed; hover it for the rest). **Stop**
   cancels whatever is running: a scan, a Convert All, or the case a server tick is converting.

## Workflow walkthroughs

### A. Round-trip from a prior forward export

Point the reverse window at a case folder produced by the forward direction and it rebuilds a
DICOM image series from `image.nii.gz`, then writes an RT-STRUCT against it.

```bash
--reverse --masks-folder CASE/masks --image-nifti CASE/image.nii.gz --output regenerated.dcm
```

Note `--masks-folder` is `CASE/masks`, not `CASE`: the CLI reads that folder top-level only and
converts exactly one job, where the GUI takes a case folder or a parent of many.

> **The result is a new study, not the original one.** The `metadata.json` the forward direction
> writes is the DICOM-**tag** sidecar (`ImageAttributes` / …); the reverse direction's
> `metadata.json` is a different schema entirely — patient, study, frame of reference, rescale
> slope. Nothing in the forward direction writes that one, and passing the tag sidecar to
> `--metadata` is silently ignored, leaving freshly minted anonymous UIDs. **If the regenerated
> structure set has to land on the original images, use workflow B** — point at the source DICOM
> series and the identifiers come from it.

### B. Masks only, attaching to an existing DICOM image series

When you have a real reference series, point at it and the writer reuses its per-slice SOP
Instance UIDs, so the structure set references the actual images rather than a synthesized copy.

```bash
--reverse --image-folder REFERENCE_DICOM --masks-folder MASKS --output regenerated.dcm
```

### C. Drop-folder / watch mode

**Run Server** watches a folder (every 10 s) and converts each case once its fingerprint — file
count, total bytes, latest write time across the folder plus `masks/` and `doses/` — is unchanged
from the previous tick, i.e. the upload has settled. There is no CLI equivalent; run the app.

Server mode names its outputs `RTSTRUCT_<hash12>.dcm` / `RTDOSE_<hash12>.dcm`, where
**`hash12` is derived from the input file *names* only** — lowercased, sorted, joined, SHA-256'd.
Adding or removing a mask changes the name and produces a new file alongside the old one. A name
cannot see content, so server mode also compares times: an existing `RTSTRUCT_<hash12>.dcm` is kept
only while it is newer than every mask, and rebuilt in place as soon as one is not. A
`masks/PTV.nii.gz` replaced by a corrected prediction is therefore picked up on the next settled
tick (the job's status reads "RT-STRUCT rebuilt, a mask was newer"). The RT-DOSE files follow the
same rule, per dose. Delete the existing `RTSTRUCT_*.dcm` / `RTDOSE_*.dcm` to force a rebuild
without touching the inputs. (Convert All timestamps every output, `RTSTRUCT_YYYYMMDD_HHmmss.dcm`,
and is therefore never idempotent.)

While a tick converts, **Stop** cancels the case in progress. The server keeps watching and leaves
that case alone until one of its files changes, so a case you stopped is not simply restarted ten
seconds later. **Stop Server** stays clickable during a conversion and cancels it too.

![Server mode on the phantom case exported by the forward window: converted on the first settled
tick, then a mask overwritten in place and the RT-STRUCT rebuilt at the same path, recorded in the
log](images/gui-nifti-to-dicom-rebuilt.png)

![The same case after Stop was pressed during a rebuild: the job reads cancelled, the server keeps
watching and leaves it alone until its files change](images/gui-nifti-to-dicom-stopped.png)

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

The jobs grid reports per-case status as the run proceeds, and the log box below it keeps the
history, including each failure's full error text and every cancellation. On completion:

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
