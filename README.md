# DicomRtNiftiConverterGUI

A C# .NET 4.8 WPF application (with a headless CLI mode) that converts DICOM radiotherapy data — CT/MR images, RT Structures, and RT Dose — to NIfTI (`.nii.gz`) format, and also performs the reverse mask → RTSTRUCT operation.

The rasterization core handles the five clinically-used DICOM `ContourGeometricType` values — `CLOSED_PLANAR`, `OPEN_PLANAR`, `OPEN_NONPLANAR`, `CLOSED_NONPLANAR`, `POINT` — and exposes both forward (RTSTRUCT → mask) and reverse (mask → RTSTRUCT) directions through a headless CLI. The rare `CLOSED_PLANAR_XOR` type tag (DICOM 2020 supplement) is deliberately not implemented because clinical RTSTRUCTs encode hollow shapes via the multi-contour even-odd convention instead.

Methodology borrows from [Dicom_RT_and_Images_to_Mask](https://github.com/brianmanderson/Dicom_RT_and_Images_to_Mask) (DicomRTTool); this implementation extends coverage beyond `CLOSED_PLANAR`-only and adds the reverse direction.

## GUI mode

Running `Dicom_RT_images_Csharp.exe` with no arguments opens a 480×320 launcher with two buttons:

- **DICOM → NIfTI** — opens the forward window (scan a DICOM archive, export selected patients/series to `image.nii.gz`, per-ROI masks under `masks/`, and `dose.nii.gz`).
- **NIfTI → DICOM** — opens the reverse window (batch-convert folders of `image.nii.gz` / `masks/` / `doses/` back into DICOM image series, RT-STRUCT, and RT-DOSE).

Each directional window has a **Help** button (top right) with the full workflow walkthrough, every control documented, output details, and example folder layouts. The CLI below is the alternative when scripting batch / benchmark runs.

## Features

- Recursive DICOM folder scanning with automatic Patient/Study/Series grouping
- CT/MR image series export to `image.nii.gz` via SimpleITK
- RT Struct contour rasterization to per-ROI binary mask `.nii.gz` files, supporting the five clinically-used `ContourGeometricType` values: `CLOSED_PLANAR`, `OPEN_PLANAR`, `OPEN_NONPLANAR`, `CLOSED_NONPLANAR`, `POINT`. Hollow shapes are handled via the multi-contour `CLOSED_PLANAR` convention with even-odd XOR fill, the dominant clinical encoding; the explicit `CLOSED_PLANAR_XOR` type tag is not dispatched separately
- Reverse direction: mask → RTSTRUCT writer (`RtStructWriterService.cs`)
- RT Dose export to `dose.nii.gz` with DoseGridScaling applied
- ROI Association editor for mapping canonical names to DICOM structure aliases
- Configurable settings with JSON persistence
- **Headless CLI** for batch and benchmark integration (see Headless mode below)
- **Per-ROI parallelization** (`Parallel.ForEach`) in the rasterizer and NIfTI writer for 2-7× speedup on multi-ROI RTSTRUCTs

## Headless mode

For batch use and benchmark integration, the application exposes a CLI that
bypasses the WPF UI:

```
# Forward: RTSTRUCT + image series → per-ROI binary masks
#   --include-image (optional) also writes image.nii.gz alongside the masks.
Dicom_RT_images_Csharp.exe --headless --forward ^
    --rtstruct PATH --image-folder PATH --output-folder PATH ^
    [--include-image]

# Reverse with reference DICOM: per-ROI binary masks → RTSTRUCT
Dicom_RT_images_Csharp.exe --headless --reverse ^
    --image-folder PATH --masks-folder PATH --output PATH

# Reverse, NIfTI-only (no reference DICOM): synthesizes the DICOM image series
# from image.nii.gz + metadata.json so the RT-STRUCT can reference it.
#   --image-nifti         (optional, default <masks-folder>/image.nii.gz)
#   --metadata            (optional, default <masks-folder>/metadata.json;
#                          auto-generated with anonymous defaults on first run)
#   --output-image-folder (optional, persist the generated DICOM image series
#                          alongside the RT-STRUCT for inspection)
Dicom_RT_images_Csharp.exe --headless --reverse ^
    --masks-folder PATH --output PATH ^
    [--image-nifti PATH] [--metadata PATH] [--output-image-folder PATH]
```

- **Exit codes** — `0` on success, `1` on conversion failure (with stack trace on stderr), `2` on missing or invalid arguments (usage printed on stderr).
- **Stdout (forward)** — header line `# rt_mask_validation forward`, then one TSV row per ROI: `<ROIName>\t<Volume_cc>\t<mask_path>`.
- **Stdout (reverse)** — header line `# rt_mask_validation reverse` (or `# rt_mask_validation reverse (nifti-only)` when no reference DICOM was supplied), then a single line with the output RT-STRUCT path.
- **Stderr** — human-readable progress and error messages.

The CLI reuses the same services the GUI uses. See [Dicom_RT_images_Csharp/Cli/HeadlessRunner.cs](Dicom_RT_images_Csharp/Cli/HeadlessRunner.cs).

## Dependencies

- **.NET Framework 4.8** (WPF)
- **fo-dicom 5.2.5** — DICOM file parsing and metadata extraction
- **SimpleITK** — Image I/O and NIfTI writing (external DLL, not NuGet)
- **Newtonsoft.Json 13.0.4** — Settings and ROI association persistence

## Build instructions

1. Open `Dicom_RT_images_Csharp.sln` in Visual Studio 2022.
2. Ensure NuGet packages are restored (right-click solution → Restore NuGet Packages).
3. SimpleITK DLLs must be present at `../SimpleITK/` relative to this repository root (equivalently, `../../SimpleITK/` relative to the `Dicom_RT_images_Csharp/` project folder):
   - `SimpleITKCSharpManaged.dll` (managed wrapper, referenced by the project)
   - `SimpleITKCSharpNative.dll` (native, auto-copied to output)

   Download from the [SimpleITK GitHub releases](https://github.com/SimpleITK/SimpleITK/releases) (e.g. `SimpleITK-2.5.0-CSharp-win64-x64.zip`). Extract so that the two DLLs live directly under `../SimpleITK/` (no version subfolder).
4. Build in Debug or Release configuration (target: Any CPU).

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
      image.nii.gz          # if Export Images is ON
      dose.nii.gz           # if Include Dose is ON and a dose is linked
      masks/
        {ROI_Name}.nii.gz   # if Include Structures is ON
  export_manifest.csv       # at the output root (or export_manifest_meta.csv for Export MetaData)
```

Anonymized (one flat folder per series, named by integer Export ID; no `{PatientID}/{Date}_...` nesting):

```
{OutputFolder}/
  {ExportID}/
    image.nii.gz
    dose.nii.gz
    masks/
      {ROI_Name}.nii.gz
  export_manifest.csv
  AnonymizationKey.json     # deterministic ExportID ↔ MRN/StudyUID/SeriesUID mapping
```

The CSV manifest columns are `MRN, StudyUID, SeriesUID, ExportID, SpacingX, SpacingY, SpacingZ` followed by one column per unique canonical ROI name (volume in cc; `-1` where the row's series did not contain that ROI). `ExportID` is `-1` for non-anonymized rows. See the in-app **Help** in the DICOM → NIfTI window for the full per-control reference.

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

- `settings.json` — default output directory, auto-open after conversion, global Export Images / Include Structures / Include Dose toggles, output spacing, anonymization salt (`HashSalt`), and the persisted state of the "Only export specific ROIs" / "Anonymize export" / "Specify Output Spacing" checkboxes.
- `roi_associations.json` — ROI canonical-name ↔ alias-set mappings used to rename DICOM ROIs to canonical names on export.

`AnonymizationKey.json` (only present when anonymizing) lives in the **output folder** alongside the per-series subfolders, not in `%AppData%`. If the **Edit Anonymization Key...** window is opened without an output folder set, it falls back to `%AppData%\DicomToNifti\AnonymizationKey.json` for inspection only.

## History

This project originated inside the manuscript repository [Dicom_RT_Images_Csharp](https://github.com/brianmanderson/Dicom_RT_Images_Csharp), where it serves as the rasterizer benchmarked against other tools. It has been split out so it can be released, cited, and consumed independently of the manuscript / benchmark harness. The manuscript repository continues to pin a specific commit of this repository as a git submodule.
