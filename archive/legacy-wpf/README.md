# Legacy WPF (archived)

These are the retired **.NET Framework 4.8 / WPF** sources for the original Windows-only app, kept
for reference only. They are **not** part of the active build (`DicomRtNifti.sln`) and are no longer
maintained — the cross-platform Avalonia app under `src/DicomRtNifti.App` supersedes them.

## What's here

- `Dicom_RT_images_Csharp.sln` — the old WPF solution.
- `Dicom_RT_images_Csharp/` — the WPF presentation layer: `App.xaml`, `MainWindow.xaml`, `Views/`,
  `ViewModels/`, `Converters/`, `Properties/`, the old `.csproj`/`packages.config`, and a duplicate
  `Cli/HeadlessRunner.cs` (the live CLI is `src/DicomRtNifti.Cli/HeadlessRunner.cs`).

This code will **not compile** as-is: it still calls the old `AnonymizationService` API (composite
`ExportID` entries, `ManifestRow.MRN`) that was replaced by the per-identifier hash scheme
(MRN→PatientHash, StudyUID→StudyHash, SeriesUID→SeriesHash).

## What was intentionally left in place

The shared business logic still physically lives at `../../Dicom_RT_images_Csharp/Services/` and
`../../Dicom_RT_images_Csharp/Models/`, because `DicomRtNifti.Core` **links** those files into the
active build (see `src/DicomRtNifti.Core/DicomRtNifti.Core.csproj`). They were deliberately not moved.

Future cleanup: relocate `Services/` and `Models/` into `src/DicomRtNifti.Core/` and switch the Core
project to default compile items, at which point the entire legacy `Dicom_RT_images_Csharp/` tree can
be removed.
