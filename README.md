# DicomRtNiftiConverterGUI

A cross-platform C# **.NET 8** toolkit that converts DICOM radiotherapy data — CT/MR/PT images, RT Structures, and RT Dose — to NIfTI (`.nii.gz`) format, and performs the reverse (mask → RTSTRUCT, NIfTI → DICOM image series). It ships as an **Avalonia desktop GUI** (`DicomRtNifti.App`) and a **headless CLI** (`DicomRtNifti.Cli`) that share one core conversion library (`DicomRtNifti.Core`), and runs on Windows, Linux, and macOS.

The rasterization core handles the five clinically-used DICOM `ContourGeometricType` values — `CLOSED_PLANAR`, `OPEN_PLANAR`, `OPEN_NONPLANAR`, `CLOSED_NONPLANAR`, `POINT` — and exposes both forward (RTSTRUCT → mask) and reverse (mask → RTSTRUCT) directions through a headless CLI. The rare `CLOSED_PLANAR_XOR` type tag (DICOM 2020 supplement) is deliberately not implemented because clinical RTSTRUCTs encode hollow shapes via the multi-contour even-odd convention instead.

Methodology borrows from [Dicom_RT_and_Images_to_Mask](https://github.com/brianmanderson/Dicom_RT_and_Images_to_Mask) (DicomRTTool); this implementation extends coverage beyond `CLOSED_PLANAR`-only and adds the reverse direction.

## GUI mode

Launch the GUI with `dotnet run --project src/DicomRtNifti.App` (or run the published `DicomRtNifti.App` executable). It opens a launcher with two buttons:

- **DICOM → NIfTI** — opens the forward window (scan a DICOM archive, export selected patients/series to `image.nii.gz`, per-ROI masks under `masks/`, and RT-DOSE volumes under `doses/{SeriesDescription}.nii.gz`).
- **NIfTI → DICOM** — opens the reverse window (batch-convert folders of `image.nii.gz` / `masks/` / `doses/` back into DICOM image series, RT-STRUCT, and RT-DOSE).

Each directional window has a **Help** button (top right) with the full workflow walkthrough, every control documented, output details, and example folder layouts. The CLI below is the alternative when scripting batch / benchmark runs.

## Features

- Recursive DICOM folder scanning with automatic Patient/Study/Series grouping
- CT/MR/PT image series export to `image.nii.gz` via SimpleITK (with optional resampling to a fixed voxel spacing)
- RT Struct contour rasterization to per-ROI binary mask `.nii.gz` files, supporting the five clinically-used `ContourGeometricType` values: `CLOSED_PLANAR`, `OPEN_PLANAR`, `OPEN_NONPLANAR`, `CLOSED_NONPLANAR`, `POINT`. Hollow shapes are handled via the multi-contour `CLOSED_PLANAR` convention with even-odd XOR fill, the dominant clinical encoding; the explicit `CLOSED_PLANAR_XOR` type tag is not dispatched separately
- Reverse direction: mask → RTSTRUCT writer (`RtStructWriterService`) and NIfTI volume → DICOM image series (`NiftiImageWriterService`)
- RT Dose export to `doses/{SeriesDescription}.nii.gz` (one file per dose, filename sanitized) with DoseGridScaling applied
- Optional per-series `metadata.json` sidecar (DICOM → NIfTI): a tabbed **Images / Structures / Dose** picker selects DICOM attributes and computed values (voxel size, image dimensions, ROI names, max dose, …), written as a sectioned, friendly-name-keyed JSON
- ROI Association editor for mapping canonical names to DICOM structure aliases
- Configurable settings with JSON persistence
- **Headless CLI** for batch and benchmark integration (see Headless mode below)
- **Per-ROI parallelization** (`Parallel.ForEach`) in the rasterizer and NIfTI writer for 2-7× speedup on multi-ROI RTSTRUCTs

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

- **Exit codes** — `0` on success, `1` on conversion failure (with stack trace on stderr), `2` on missing or invalid arguments (usage printed on stderr).
- **Stdout** — a `# rt_mask_validation <mode>` header line followed by the machine-readable results: forward writes one TSV row per ROI (`<ROIName>\t<Volume_cc>\t<mask_path>`); the reverse/image modes write the output path(s).
- **Stderr** — human-readable progress and error messages.

The CLI reuses the same services the GUI uses. See [src/DicomRtNifti.Cli/HeadlessRunner.cs](src/DicomRtNifti.Cli/HeadlessRunner.cs) (run `--help` for the full option list).

## Dependencies

- **.NET 8** — cross-platform runtime (Windows, Linux, macOS)
- **Avalonia 11** — cross-platform desktop UI (GUI only)
- **fo-dicom 5.2.5** — DICOM file parsing and metadata extraction
- **SimpleITK** — image I/O and NIfTI writing (external native library, **not** a NuGet package; see Build)
- **Newtonsoft.Json 13.0.4** — settings and ROI association persistence
- **CommunityToolkit.Mvvm / .HighPerformance** — MVVM commands (GUI) and span helpers (Core)

## Build instructions

Requires the **.NET 8 SDK**. From the repository root:

```
dotnet build DicomRtNifti.sln -c Release
dotnet test  tests/DicomRtNifti.Core.Tests/DicomRtNifti.Core.Tests.csproj -c Release
```

**SimpleITK** is not a NuGet package. The managed wrapper `SimpleITKCSharpManaged.dll`
is referenced by `src/SimpleITK.props`, and the matching native library
(`SimpleITKCSharpNative.dll` / `libSimpleITKCSharpNative.so` / `.dylib`) is copied
into the build output so it loads at runtime. Stage both at **`../SimpleITK/`**
(one level above the repository root; override with `-p:SitkDir=...`):

1. Download a C# release from the [SimpleITK releases](https://github.com/SimpleITK/SimpleITK/releases) (e.g. `SimpleITK-2.5.0-CSharp-win64-x64.zip`).
2. Extract so the two DLLs live directly under `../SimpleITK/` (no version subfolder).
3. Verify the native loaded: `dotnet run --project src/DicomRtNifti.Cli -- --version` prints `SimpleITK native: OK`.

To produce a self-contained build that needs no .NET install on the target
machine (rid = `win-x64` | `linux-x64` | `osx-arm64`):

```
dotnet publish src/DicomRtNifti.App/DicomRtNifti.App.csproj -c Release -r <rid> --self-contained
dotnet publish src/DicomRtNifti.Cli/DicomRtNifti.Cli.csproj -c Release -r <rid> --self-contained
```

## RT Struct mask rasterization

The mask rasterization converts RT Structure contours from DICOM world coordinates to binary voxel masks:

1. **Coordinate transform**: Each contour point (x, y, z in mm) is converted to continuous voxel indices using `SimpleITK.Image.TransformPhysicalPointToContinuousIndex()`, which handles arbitrary image orientations (axial, coronal, sagittal, oblique) via the full direction cosine matrix.
2. **Scanline fill**: For each contour polygon on a slice, a scanline algorithm finds all edge-scanline intersections at each integer row, sorts them, and fills between pairs.
3. **Even-odd rule (XOR)**: Multiple contours on the same slice for the same ROI are handled via XOR toggling, which correctly produces hollow structures (e.g., a ring/shell where an inner contour subtracts from an outer contour).

## Output structure (forward: DICOM → NIfTI)

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
  AnonymizationKey.json     # three reverse-lookup maps: MRN→PatientHash, StudyUID→StudyHash, SeriesUID→SeriesHash
```

The CSV manifest columns are `PatientID, StudyUID, SeriesUID, SpacingX, SpacingY, SpacingZ` followed by one column per unique canonical ROI name (volume in cc; `-1` where the row's series did not contain that ROI). When anonymizing, the `PatientID`/`StudyUID`/`SeriesUID` cells hold the hashes; otherwise they hold the real identifiers. Every exported folder/file segment is sanitized to be valid on Windows (forbidden characters, reserved device names, trailing dots/spaces), anonymized or not. See the in-app **Help** in the DICOM → NIfTI window for the full per-control reference.

### `metadata.json` sidecar (selected DICOM tags)

When **Export DICOM Metadata** is enabled and at least one tag is selected, each series folder also gets a `metadata.json` holding the attributes chosen in the tabbed **Select DICOM Metadata Tags** dialog. Each of the dialog's three tabs writes its own top-level section, keyed by **friendly names**; values are typed (numbers, strings, arrays) and tags absent from the file are written as `null`:

```json
{
  "ImageAttributes":     { "Patient Name": "Doe^John", "Voxel Size": [0.98, 0.98, 3.0] },
  "StructureAttributes": { "Structure Set Label": "Plan1", "ROI Names": ["PTV", "Lung_L"] },
  "DoseAttributes":      { "Dose Units": "GY", "Max Dose": 72.4 }
}
```

Image tags are read from the series' first slice, structure tags from the linked RTSTRUCT, and dose tags from the linked RTDOSE (a section whose source file is missing is written with all-`null` values; a section with no selected tags is omitted). Besides raw DICOM attributes, each tab offers **computed** values — Voxel Size and Image Dimensions (image), ROI Names and Number of ROIs (structure), Max Dose and Dose Grid Voxel Size (dose). The picker shows a short curated list per tab by default, with a **Show all tags** toggle to browse the full DICOM dictionary.

> **Note:** this forward-export sidecar is a different file from the reverse-mode `metadata.json` described under *Reverse-mode folder layout* below — that one carries patient/study/UIDs + rescale slope/intercept to drive NIfTI → DICOM, and is unrelated to the tag selections here.

## Reverse-mode folder layout (NIfTI → DICOM)

Each input folder looks like one of these (every line is optional individually; the folder qualifies if it has at least one of `image.nii.gz`, `masks/*.nii.gz`, or `doses/*.nii.gz`):

```
{InputFolder}/
  image.nii.gz                  # → DICOM CT (or MR / PT) image series, one file per slice
  metadata.json                 # patient/study/UIDs + rescale slope/intercept; auto-generated with anonymous defaults if absent
  CT.*.dcm or MR.*.dcm ...      # optional: an existing reference DICOM image series in the same folder (overrides image.nii.gz path)
  masks/
    {ROI_Name}.nii.gz           # → one ROI in a single RT-STRUCT per folder
  doses/
    {basename}.nii.gz           # → one RT-DOSE per file
```

You can point the **NIfTI → DICOM** window (or the headless `--reverse` flag) at a single such folder, or at a parent folder containing many of them side-by-side — each first-level subfolder becomes its own job. See the in-app **Help** in the NIfTI → DICOM window for the full `metadata.json` schema and a copy-pasteable sample.

## Settings

Stored in `%AppData%\DicomToNifti\`:

- `settings.json` — default output directory, auto-open after conversion, global Export Images / Export Structures / Export Dose toggles, output spacing, anonymization salt (`HashSalt`), and the persisted state of the "Limit export to selected ROIs" / "Anonymize export" / "Resample to fixed spacing" checkboxes.
- `roi_associations.json` — ROI canonical-name ↔ alias-set mappings used to rename DICOM ROIs to canonical names on export.

`AnonymizationKey.json` (only present when anonymizing) lives in the **output folder** alongside the per-patient subfolders, not in `%AppData%`. It holds three reverse-lookup maps — MRN→PatientHash, StudyUID→StudyHash, SeriesUID→SeriesHash — so anonymized exports can be traced back to their original identifiers. Hashes are deterministic (SHA256 of the salted identifier), so re-running an export reuses the same hashes and folders.

## History

This project originated inside the manuscript repository [Dicom_RT_Images_Csharp](https://github.com/brianmanderson/Dicom_RT_Images_Csharp), where it serves as the rasterizer benchmarked against other tools. It has been split out so it can be released, cited, and consumed independently of the manuscript / benchmark harness. The manuscript repository continues to pin a specific commit of this repository as a git submodule.
