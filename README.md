# DicomRtNiftiConverterGUI

A C# .NET 4.8 WPF application (with a headless CLI mode) that converts DICOM radiotherapy data — CT/MR images, RT Structures, and RT Dose — to NIfTI (`.nii.gz`) format, and also performs the reverse mask → RTSTRUCT operation.

The rasterization core handles the five clinically-used DICOM `ContourGeometricType` values — `CLOSED_PLANAR`, `OPEN_PLANAR`, `OPEN_NONPLANAR`, `CLOSED_NONPLANAR`, `POINT` — and exposes both forward (RTSTRUCT → mask) and reverse (mask → RTSTRUCT) directions through a headless CLI. The rare `CLOSED_PLANAR_XOR` type tag (DICOM 2020 supplement) is deliberately not implemented because clinical RTSTRUCTs encode hollow shapes via the multi-contour even-odd convention instead.

Methodology borrows from [Dicom_RT_and_Images_to_Mask](https://github.com/brianmanderson/Dicom_RT_and_Images_to_Mask) (DicomRTTool); this implementation extends coverage beyond `CLOSED_PLANAR`-only and adds the reverse direction.

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
Dicom_RT_images_Csharp.exe --headless --forward \
    --rtstruct PATH --image-folder PATH --output-folder PATH

# Reverse: per-ROI binary masks → RTSTRUCT
Dicom_RT_images_Csharp.exe --headless --reverse \
    --image-folder PATH --masks-folder PATH --output PATH
```

Reuses the same services the GUI uses. See `Dicom_RT_images_Csharp/Cli/HeadlessRunner.cs`.

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

## Output structure

```
{OutputFolder}/
  {PatientID}/
    {SeriesDate}_{SeriesDescription}/
      image.nii.gz          # CT/MR volume
      dose.nii.gz           # RT Dose (if selected)
      masks/
        {ROI_Name}.nii.gz   # Binary mask per structure
```

## Settings

Stored in `%AppData%\DicomToNifti\`:

- `settings.json` — Default output directory, auto-open, export unmatched ROIs
- `roi_associations.json` — ROI canonical name to alias mappings

## History

This project originated inside the manuscript repository [Dicom_RT_Images_Csharp](https://github.com/brianmanderson/Dicom_RT_Images_Csharp), where it serves as the rasterizer benchmarked against other tools. It has been split out so it can be released, cited, and consumed independently of the manuscript / benchmark harness. The manuscript repository continues to pin a specific commit of this repository as a git submodule.
