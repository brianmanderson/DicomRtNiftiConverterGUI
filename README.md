# DicomRtNiftiConverterGUI

A cross-platform C# **.NET 8** toolkit that converts DICOM radiotherapy data - CT/MR/PT images, RT Structures, and RT Dose - to NIfTI (`.nii.gz`) format, and performs the reverse (mask -> RTSTRUCT, NIfTI -> DICOM image series). It ships as an **Avalonia desktop GUI** (`DicomRtNifti.App`) and a **headless CLI** (`DicomRtNifti.Cli`) that share one core conversion library (`DicomRtNifti.Core`), and runs on Windows, Linux, and macOS.

The rasterization core handles the five clinically-used DICOM `ContourGeometricType` values - `CLOSED_PLANAR`, `OPEN_PLANAR`, `OPEN_NONPLANAR`, `CLOSED_NONPLANAR`, `POINT` - and exposes both forward (RTSTRUCT -> mask) and reverse (mask -> RTSTRUCT) directions through a headless CLI. The rare `CLOSED_PLANAR_XOR` type tag (DICOM 2020 supplement) is deliberately not implemented because clinical RTSTRUCTs encode hollow shapes via the multi-contour even-odd convention instead.

Methodology borrows from [Dicom_RT_and_Images_to_Mask](https://github.com/brianmanderson/Dicom_RT_and_Images_to_Mask) (DicomRTTool); this implementation extends coverage beyond `CLOSED_PLANAR`-only and adds the reverse direction.

## Start here

**[`examples/Guide.md`](examples/Guide.md)** is a worked, runnable example of the whole toolkit: it
takes a public cohort with CT, structures **and dose** from raw DICOM to an analysis-ready NIfTI
dataset, converts the masks back into a DICOM structure set, measures what the round trip cost,
and verifies the rasterizer against closed-form geometry.

It needs no .NET install — the notebook downloads a self-contained build. If you are evaluating
this tool, start there rather than here.

## GUI mode

Launch the GUI with `dotnet run --project src/DicomRtNifti.App` (or run the published `DicomRtNifti.App` executable). It opens a launcher with two buttons:

- **DICOM -> NIfTI** - opens the forward window (scan a DICOM archive, export selected patients/series to `image.nii.gz`, per-ROI masks under `masks/`, and RT-DOSE volumes under `doses/{SeriesDescription}.nii.gz`).
- **NIfTI -> DICOM** - opens the reverse window (batch-convert folders of `image.nii.gz` / `masks/` / `doses/` back into DICOM image series, RT-STRUCT, and RT-DOSE).

Each directional window has a **Help** button (top right) with the full workflow walkthrough, every control documented, output details, and example folder layouts. The same material is readable outside the app in [`examples/GUI_WALKTHROUGH.md`](examples/GUI_WALKTHROUGH.md), which also maps every control to its CLI flag. The CLI below is the alternative when scripting batch / benchmark runs.

## Features

- Recursive DICOM folder scanning with automatic Patient/Study/Series grouping
- CT/MR/PT image series export to `image.nii.gz` via SimpleITK (with optional resampling to a fixed voxel spacing)
- RT Struct contour rasterization to per-ROI binary mask `.nii.gz` files, supporting the five clinically-used `ContourGeometricType` values: `CLOSED_PLANAR`, `OPEN_PLANAR`, `OPEN_NONPLANAR`, `CLOSED_NONPLANAR`, `POINT`. Hollow shapes are handled via the multi-contour `CLOSED_PLANAR` convention with even-odd XOR fill, the dominant clinical encoding; the explicit `CLOSED_PLANAR_XOR` type tag is not dispatched separately
- Reverse direction: mask -> RTSTRUCT writer (`RtStructWriterService`) and NIfTI volume -> DICOM image series (`NiftiImageWriterService`)
- RT Dose export to `doses/{SeriesDescription}.nii.gz` (one file per dose, filename sanitized) with DoseGridScaling applied
- Optional per-series `metadata.json` sidecar (DICOM -> NIfTI): a tabbed **Images / Structures / Dose** picker selects DICOM attributes and computed values (voxel size, image dimensions, ROI names, max dose, ...), written as a sectioned, friendly-name-keyed JSON
- ROI Association editor for mapping canonical names to DICOM structure aliases
- Configurable settings with JSON persistence
- **Headless CLI** for batch and benchmark integration (see Headless mode below)
- **Per-ROI parallelization** (`Parallel.ForEach`) in the rasterizer and NIfTI writer for 2-7x speedup on multi-ROI RTSTRUCTs

## Headless mode

For batch use and benchmark integration, the `DicomRtNifti.Cli` executable runs
the same conversion services as the GUI, with no desktop required. Run the
published binary directly, or during development via
`dotnet run --project src/DicomRtNifti.Cli -- <args>`. (A leading `--headless`
flag is accepted but optional.)

```
# Forward: RTSTRUCT + image series -> per-ROI binary masks
#   --include-image (optional) also writes image.nii.gz; --rtdose (optional) writes doses/<desc>.nii.gz
DicomRtNifti.Cli --forward --rtstruct PATH --image-folder PATH --output-folder PATH \
    [--include-image] [--rtdose PATH]

# Reverse with reference DICOM: per-ROI masks -> RTSTRUCT
DicomRtNifti.Cli --reverse --image-folder PATH --masks-folder PATH --output PATH

# Reverse, NIfTI-only (no reference DICOM): synthesizes the DICOM image series
# from image.nii.gz + metadata.json so the RT-STRUCT can reference it.
#   --image-nifti         (optional, default <masks-folder>/image.nii.gz)
#   --metadata            (optional, default <masks-folder>/metadata.json;
#                          auto-generated with anonymous defaults on first run)
#   --output-image-folder (optional, persist the generated DICOM image series)
DicomRtNifti.Cli --reverse --masks-folder PATH --output PATH \
    [--image-nifti PATH] [--metadata PATH] [--output-image-folder PATH]

# Image-forward: DICOM image series -> NIfTI image volume (no RTSTRUCT needed)
DicomRtNifti.Cli --image-forward --image-folder PATH --output PATH.nii.gz \
    [--target-spacing X,Y,Z]

# Image-reverse: NIfTI image volume -> DICOM image series
#   --modality default 'auto' infers CT/MR/PT from the NIfTI's pixel values
DicomRtNifti.Cli --image-reverse --nifti-image PATH --output-folder PATH \
    [--modality CT|MR|PT|auto]

# Version + SimpleITK native-load probe
DicomRtNifti.Cli --version
```

- **Exit codes** - `0` on success, `1` on conversion failure (with stack trace on stderr), `2` on missing or invalid arguments (usage printed on stderr).
- **Stdout** - a `# rt_mask_validation <mode>` header line followed by the machine-readable results: forward writes one TSV row per ROI (`<ROIName>\t<Volume_cc>\t<mask_path>`); the reverse/image modes write the output path(s).
- **Stderr** - human-readable progress and error messages.

The CLI reuses the same services the GUI uses. See [src/DicomRtNifti.Cli/HeadlessRunner.cs](src/DicomRtNifti.Cli/HeadlessRunner.cs) (run `--help` for the full option list).

**No DICOM handy?** No test data is committed, but the conformance package generates a complete
synthetic CT + RTSTRUCT, which makes the fastest end-to-end smoke test of a fresh build:

```
pip install "git+https://github.com/brianmanderson/RTMaskConformanceTest"
rtmask-conformance generate ./fixture --n-quadrature 2

DicomRtNifti.Cli --forward \
    --rtstruct ./fixture/rtstruct/primitives_planar.dcm \
    --image-folder ./fixture/refct \
    --output-folder ./predictions
```

That writes one `.nii.gz` per primitive into `./predictions`. To score them against the analytic
ground truth exactly as the CI accuracy gate does — Dice, HD95, mean surface distance and volume
error, with this repository's documented per-primitive thresholds:

```
rtmask-conformance verify --predictions ./predictions \
    --groundtruth ./fixture/groundtruth --config ./conformance.yaml
```

The gate itself lives in
[`.github/workflows/conformance-crossplatform.yml`](.github/workflows/conformance-crossplatform.yml),
which runs the same three commands on Windows, Linux and macOS against a SHA-pinned revision of
the fixture generator.

> **Note on the forward mode's output layout.** `--forward` writes masks **flat** into
> `--output-folder`, not into a `masks/` subfolder — it is the single-series mode the conformance
> harness drives, and that contract is deliberately frozen. The hierarchical
> `<patient>/<study>/<series>/{image.nii.gz,masks/,doses/}` layout described under
> [Output structure](#output-structure-forward-dicom---nifti) is what the GUI and the cohort
> modes below produce.

**What the single-series modes expect of `--image-folder`.** These modes take one explicitly
named series, so they do no scanning: they read files matching `*.dcm` **in that folder only**,
not in subfolders. Slices with another extension (or none, as some archives ship them) are not
seen, and the run stops with `No image (.dcm) slices in <folder>`. RT-STRUCT / RT-DOSE / RT-PLAN
files sitting beside the slices are fine — they are filtered out by modality. If your archive is
nested, or its files are extensionless, use the [cohort modes](#cohort-mode) below: those scan
recursively and read every file regardless of extension.

`--reverse` writes a `metadata.json` back into `--masks-folder` when one is not already there, so
that repeat runs reuse the same UIDs rather than minting a new series each time. That is a write
into an input folder — expect it.

## Cohort mode

The modes above each convert one explicitly-named series. The cohort modes scan a tree
recursively and process everything in it, which is what the GUI's Convert Selected and Export
Manifest Only commands do — the same `CohortExportService`, driven from a script instead of a
window.

```bash
# Inventory: what is in this archive, and how confident is each RTSTRUCT/RTDOSE link?
DicomRtNifti.Cli --cohort-scan --input ARCHIVE

# Survey: one CSV row per series - spacing plus each ROI's volume in cc
DicomRtNifti.Cli --cohort-manifest --input ARCHIVE --output OUT

# Convert: image + masks + doses + metadata.json + manifest, for the whole cohort
DicomRtNifti.Cli --cohort-convert --input ARCHIVE --output OUT \
    --associations associations.json --only-associated-rois \
    --output-spacing 1.0,1.0,3.0 \
    --anonymize --salt "my-cohort-salt" \
    --metadata-tags PatientAge,KVP,@VoxelSize \
    --metadata-dose-tags DoseUnits,@MaxDose
```

Key points:

- **Stdout is exactly one JSON document** for these modes, and nothing else, so it can be piped
  straight into a parser. There is no `# rt_mask_validation` header — that belongs to the
  single-series contract above. Progress stays on stderr. Each document carries a `schema` field.
- **Series selection matters.** A study often holds several series of the same modality — a
  planning CT plus CBCTs resampled onto its grid, which share its frame of reference, spacing
  *and* slice count. Prefer `--struct-description SUBSTR`, which selects on the linked structure
  set's description and exports that set; structure sets are named for what they were drawn on
  when the images are indistinguishable. `--series-description` works when the image descriptions
  are reliable, and `--prefer-largest-series` is a last resort that ties (and then picks
  arbitrarily) exactly in the resampled-sibling case. `--require-structures` / `--require-dose`
  skip series lacking what you need. Everything excluded is reported with a reason.
- **Link confidence is reported.** `--cohort-scan` records how each RTSTRUCT and RTDOSE was
  matched to its image series — `ReferencedSeriesUid` (authoritative), `FrameOfReferenceUid`, or
  `LargestSeriesFallback` (a guess). A cohort resolved entirely by fallback deserves a look.
- **Under `--anonymize` the JSON contains hashes only**, including for skipped and unlinked
  series, so printing it in a notebook cannot leak identifiers. Re-identification lives solely in
  `AnonymizationKey.json`.
- **Volumes cost a rasterization pass.** There is no analytic contour-area shortcut;
  `--no-volumes` skips the work and writes the missing sentinel (`-1`) instead.
- **The manifest merges.** Re-running extends it — rows are keyed on the three identifier columns,
  and new ROI columns are appended without disturbing existing ones. With a stable `--salt`, the
  same patient lands in the same folder, so a cohort grows rather than duplicating. The corollary:
  **use one salt for every command touching a cohort.** Surveying with real identifiers and then
  converting with hashed ones does not update those rows, it appends a second set — leaving real
  identifiers in the cohort root next to an anonymized export.
- **Dose keeps its own extent.** It is resampled to `--output-spacing`, but its origin and size
  follow the source dose grid rather than the image, since that grid usually covers only the
  region around the target. Masks *are* on the image grid. Resample the dose onto the image before
  combining them.

Run `--help` for the full option list.

## Dependencies

- **.NET 8** - cross-platform runtime (Windows, Linux, macOS)
- **Avalonia 11** - cross-platform desktop UI (GUI only)
- **fo-dicom 5.2.5** - DICOM file parsing and metadata extraction
- **SimpleITK** - image I/O and NIfTI writing (external native library, **not** a NuGet package; see Build)
- **Newtonsoft.Json 13.0.4** - settings and ROI association persistence
- **CommunityToolkit.Mvvm / .HighPerformance** - MVVM commands (GUI) and span helpers (Core)

## Build instructions

> **Not building from source?** Prebuilt, self-contained binaries for Windows / Linux / macOS —
> SimpleITK native included, no .NET install needed — are on the
> [releases page](https://github.com/brianmanderson/DicomRtNiftiConverterGUI/releases). The
> [notebook](examples/Pancreatic_CT_CBCT_DICOM_RT_RoundTrip.ipynb) downloads one automatically.

Requires the **.NET 8 SDK**.

### Step 1 — stage SimpleITK first (do this before you build)

**SimpleITK is not a NuGet package**, and nothing restores it for you. The managed wrapper
`SimpleITKCSharpManaged.dll` is referenced by `src/SimpleITK.props`, and the matching native
library (`SimpleITKCSharpNative.dll` / `libSimpleITKCSharpNative.so` / `.dylib`) is copied into
the build output so it loads at runtime. Stage both at **`../SimpleITK/`** (one level above the
repository root; override with `-p:SitkDir=...`):

1. Download a C# release from the [SimpleITK releases](https://github.com/SimpleITK/SimpleITK/releases) (e.g. `SimpleITK-2.5.0-CSharp-win64-x64.zip`), matching your OS *and* architecture.
2. Extract so the two libraries live **directly** under `../SimpleITK/`. The archive unpacks into a version-named top-level folder — flatten it; a `../SimpleITK/SimpleITK-2.5.0-CSharp-win64-x64/` subfolder will not be found.

### Step 2 — build and test

From the repository root:

```
dotnet build DicomRtNifti.sln -c Release
dotnet test  tests/DicomRtNifti.Core.Tests/DicomRtNifti.Core.Tests.csproj -c Release
```

The built CLI lands at `src/DicomRtNifti.Cli/bin/Release/net8.0/DicomRtNifti.Cli` (`.exe` on
Windows); the GUI at `src/DicomRtNifti.App/bin/Release/net8.0/DicomRtNifti.App`.

### Step 3 — verify the native actually loaded

```
dotnet run --project src/DicomRtNifti.Cli -- --version
```

It should print `SimpleITK native: OK (1-voxel probe)`. **`--version` exits 0 either way** — it
reports a load failure in its output rather than in its exit code — so a script must grep the
text, as CI does, not just check the status.

### Troubleshooting the SimpleITK dependency

| Symptom | Cause |
|---|---|
| Build fails with a wall of `CS0246: The type or namespace name 'itk' could not be found`, preceded by one `MSB3245: Could not resolve this reference ... "SimpleITKCSharpManaged"` | Step 1 was skipped, or `SitkDir` points somewhere without the DLLs. The MSB3245 warning is the real error; the CS0246 flood is downstream noise. |
| `--version` prints `SimpleITK native: FAILED TO LOAD -- TypeInitializationException ... DllNotFoundException` | The managed wrapper resolved but the per-OS native did not. Check that the native for *this* OS/architecture is in `SitkDir` and got copied next to the output assembly. |
| Everything builds, then a conversion throws from `itk.simple` | Same as above — run `--version` first to confirm, before reading it as a conversion bug. |

### Self-contained publish

To produce a build that needs no .NET install on the target machine
(rid = `win-x64` | `linux-x64` | `osx-arm64`):

```
dotnet publish src/DicomRtNifti.App/DicomRtNifti.App.csproj -c Release -r <rid> --self-contained
dotnet publish src/DicomRtNifti.Cli/DicomRtNifti.Cli.csproj -c Release -r <rid> --self-contained
```

## RT Struct mask rasterization

The mask rasterization converts RT Structure contours from DICOM world coordinates to binary voxel masks:

1. **Coordinate transform**: Each contour point (x, y, z in mm) is converted to continuous voxel indices using `SimpleITK.Image.TransformPhysicalPointToContinuousIndex()`, which handles arbitrary image orientations (axial, coronal, sagittal, oblique) via the full direction cosine matrix.
2. **Scanline fill**: For each contour polygon on a slice, a scanline algorithm finds all edge-scanline intersections at each integer row, sorts them, and fills between pairs.
3. **Even-odd rule (XOR)**: Multiple contours on the same slice for the same ROI are handled via XOR toggling, which correctly produces hollow structures (e.g., a ring/shell where an inner contour subtracts from an outer contour).

## Output structure (forward: DICOM -> NIfTI)

Non-anonymized (one folder per patient, one subfolder per series):

```
{OutputFolder}/
  {PatientID}/
    {SeriesDate}_{SeriesDescription}/
      image.nii.gz                          # if Export Images is ON
      metadata.json                         # if Export DICOM Metadata is ON (selected tags; see below)
      doses/
        {SeriesDescription}.nii.gz          # if Export Dose is ON and a dose is linked
      masks/
        {ROI_Name}.nii.gz                   # if Export Structures is ON
  export_manifest.csv       # at the output root (or export_manifest_meta.csv for Export Manifest Only)
```

Anonymized (folders named by deterministic per-identifier hashes; a patient's datasets all nest under one patient hash, each study groups its series):

```
{OutputFolder}/
  {PatientHash}/                # e.g. P1a2b3c4d5e6f  (stable per MRN)
    {StudyHash}/                # e.g. ST9a8b7c6d5e4f (stable per StudyInstanceUID)
      {SeriesHash}/             # e.g. SE0011223344ff (stable per SeriesInstanceUID)
        image.nii.gz
        metadata.json
        doses/
          {SeriesDescription}.nii.gz
        masks/
          {ROI_Name}.nii.gz
  export_manifest.csv
  AnonymizationKey.json     # three reverse-lookup maps: MRN->PatientHash, StudyUID->StudyHash, SeriesUID->SeriesHash
```

The CSV manifest columns are `PatientID, StudyUID, SeriesUID, SpacingX, SpacingY, SpacingZ` followed by one column per unique canonical ROI name (volume in cc; `-1` where the row's series did not contain that ROI). When anonymizing, the `PatientID`/`StudyUID`/`SeriesUID` cells hold the hashes; otherwise they hold the real identifiers. Every exported folder/file segment is sanitized to be valid on Windows (forbidden characters, reserved device names, trailing dots/spaces), anonymized or not. See the in-app **Help** in the DICOM -> NIfTI window for the full per-control reference.

### `metadata.json` sidecar (selected DICOM tags)

When **Export DICOM Metadata** is enabled and at least one tag is selected, each series folder also gets a `metadata.json` holding the attributes chosen in the tabbed **Select DICOM Metadata Tags** dialog. Each of the dialog's three tabs writes its own top-level section, keyed by **friendly names**; values are typed (numbers, strings, arrays) and tags absent from the file are written as `null`:

```json
{
  "ImageAttributes":     { "Patient Name": "Doe^John", "Voxel Size": [0.98, 0.98, 3.0] },
  "StructureAttributes": { "Structure Set Label": "Plan1", "ROI Names": ["PTV", "Lung_L"] },
  "DoseAttributes":      { "Dose Units": "GY", "Max Dose": 72.4 }
}
```

Image tags are read from the series' first slice, structure tags from the linked RTSTRUCT, and dose tags from the linked RTDOSE (a section whose source file is missing is written with all-`null` values; a section with no selected tags is omitted). Besides raw DICOM attributes, each tab offers **computed** values - Voxel Size and Image Dimensions (image), ROI Names and Number of ROIs (structure), Max Dose and Dose Grid Voxel Size (dose). The picker shows a short curated list per tab by default, with a **Show all tags** toggle to browse the full DICOM dictionary.

> **Note:** this forward-export sidecar is a different file from the reverse-mode `metadata.json` described under *Reverse-mode folder layout* below - that one carries patient/study/UIDs + rescale slope/intercept to drive NIfTI -> DICOM, and is unrelated to the tag selections here.

## Reverse-mode folder layout (NIfTI -> DICOM)

Each input folder looks like one of these (every line is optional individually; the folder qualifies if it has at least one of `image.nii.gz`, `masks/*.nii.gz`, or `doses/*.nii.gz`):

```
{InputFolder}/
  image.nii.gz                  # -> DICOM CT (or MR / PT) image series, one file per slice
  metadata.json                 # patient/study/UIDs + rescale slope/intercept; auto-generated with anonymous defaults if absent
  CT.*.dcm or MR.*.dcm ...      # optional: an existing reference DICOM image series in the same folder (overrides image.nii.gz path)
  masks/
    {ROI_Name}.nii.gz           # -> one ROI in a single RT-STRUCT per folder
  doses/
    {basename}.nii.gz           # -> one RT-DOSE per file
```

You can point the **NIfTI -> DICOM** window (or the headless `--reverse` flag) at a single such folder, or at a parent folder containing many of them side-by-side - each first-level subfolder becomes its own job. See the in-app **Help** in the NIfTI -> DICOM window for the full `metadata.json` schema and a copy-pasteable sample.

## Settings

Stored in `%AppData%\DicomToNifti\`:

- `settings.json` - default output directory, auto-open after conversion, global Export Images / Export Structures / Export Dose toggles, output spacing, anonymization salt (`HashSalt`), and the persisted state of the "Limit export to selected ROIs" / "Anonymize export" / "Resample to fixed spacing" checkboxes.
- `roi_associations.json` - ROI canonical-name <-> alias-set mappings used to rename DICOM ROIs to canonical names on export.

`AnonymizationKey.json` (only present when anonymizing) lives in the **output folder** alongside the per-patient subfolders, not in `%AppData%`. It holds three reverse-lookup maps - MRN->PatientHash, StudyUID->StudyHash, SeriesUID->SeriesHash - so anonymized exports can be traced back to their original identifiers. Hashes are deterministic (SHA256 of the salted identifier), so re-running an export reuses the same hashes and folders.

## History

This project originated inside the manuscript repository [Dicom_RT_Images_Csharp](https://github.com/brianmanderson/Dicom_RT_Images_Csharp), where it serves as the rasterizer benchmarked against other tools. It has been split out so it can be released, cited, and consumed independently of the manuscript / benchmark harness. The manuscript repository continues to pin a specific commit of this repository as a git submodule.
