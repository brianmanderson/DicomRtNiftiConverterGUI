# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Cross-platform (.NET 8) toolkit that converts DICOM radiotherapy data — CT/MR/PT image series, RT Structures, and RT Dose — to NIfTI (`.nii.gz`), and performs the reverse (mask → RTSTRUCT, NIfTI → DICOM image series). Ships as a headless CLI (`DicomRtNifti.Cli`) and an Avalonia desktop GUI (`DicomRtNifti.App`) that share one Core library. The rasterizer backs a CMPB software-publication paper, so analytical accuracy is gated in CI (see Conformance below).

This repo is also mounted as a submodule of the research repo
[Dicom_RT_Images_Csharp](https://github.com/brianmanderson/Dicom_RT_Images_Csharp), which holds the
cross-tool benchmark and the manuscripts. A rasterization change here moves numbers there — say so
when you make one.

## Skills

`.claude/skills/` is tracked; the descriptions say when to load each one.

- `conformance-gate` — load before editing `RtStructMaskService` or any rasterization path, before
  touching `conformance.yaml` thresholds, and to reproduce the three-OS gate locally.

## Source layout

The solution is **`DicomRtNifti.sln`** at the repo root. Each project owns its source:
- `src/DicomRtNifti.Core` — UI-agnostic conversion library (`Services/`, `Models/`); the heart of the toolkit, consumed by both front-ends via `<ProjectReference>`.
- `src/DicomRtNifti.Cli` — headless CLI (assembly `DicomRtNifti.Cli`).
- `src/DicomRtNifti.App` — Avalonia desktop GUI (assembly `DicomRtNifti.App`).
- `tests/DicomRtNifti.Core.Tests` — xUnit tests for Core.
- `examples/` — `Guide.md` (the map), a runnable notebook that drives the CLI end-to-end over a
  public cohort, and `GUI_WALKTHROUGH.md`. Start here when onboarding. It is *not* the fast smoke
  test — the notebook pulls tens of GB from TCIA. For that, use the `rtmask-conformance`
  generate → `--forward` → verify sequence in README's Headless mode section (~1 min).

Two other trees you will see — don't mistake them for live code:
- `archive/legacy-wpf/` — the retired .NET Framework 4.8 WPF app. It keeps its own copies of the old service/model files. **Do not edit; it is not built by `DicomRtNifti.sln`.**
- `.claude/worktrees/`, `packages/` — agent worktrees and legacy NuGet restore. Ignore.

(History note: Core source used to be `<Compile Include>`-linked from a repo-root `Dicom_RT_images_Csharp/` folder during the net48→.NET 8 migration. That scaffold is gone — the files now physically live under `src/DicomRtNifti.Core/`, and the namespaces match the assemblies. If you find a stale reference to that old path, it's wrong.)

## Build / test / run

```powershell
# Build everything (Release; matches CI)
dotnet build DicomRtNifti.sln -c Release

# Run all Core unit tests
dotnet test tests/DicomRtNifti.Core.Tests/DicomRtNifti.Core.Tests.csproj -c Release

# Run a single test (xunit filter on fully-qualified name)
dotnet test tests/DicomRtNifti.Core.Tests/DicomRtNifti.Core.Tests.csproj --filter "FullyQualifiedName~WindowsPathSanitizerTests"
dotnet test tests/DicomRtNifti.Core.Tests/DicomRtNifti.Core.Tests.csproj --filter "FullyQualifiedName~DicomScannerModalityTests.SeriesModality_ResolvesToMajority_WhenOneInstanceIsMistagged"

# Run the GUI
dotnet run --project src/DicomRtNifti.App

# Run the CLI (leading --headless is optional, kept for back-compat)
dotnet run --project src/DicomRtNifti.Cli -c Release -- --help

# Confirm the SimpleITK native actually loaded on this machine (prints "SimpleITK native: OK")
dotnet run --project src/DicomRtNifti.Cli -- --version

# Self-contained publish (no .NET install needed on target); rid = win-x64 | linux-x64 | osx-arm64
dotnet publish src/DicomRtNifti.Cli/DicomRtNifti.Cli.csproj -c Release -r win-x64 --self-contained -o publish-cli
dotnet publish src/DicomRtNifti.App/DicomRtNifti.App.csproj -c Release -r win-x64 --self-contained -o publish-app
```

There is no separate lint step; `dotnet build` warnings are the bar. `Nullable` and `ImplicitUsings` are **deliberately disabled** (in `src/Directory.Build.props`) because the service/model files were written for C# 7.3 / net48 and are unannotated — don't enable them globally to "clean up," it floods the build.

## SimpleITK native dependency (the #1 setup gotcha)

SimpleITK is **not** a NuGet package. The managed wrapper `SimpleITKCSharpManaged.dll` is referenced via `src/SimpleITK.props`, and the per-OS native (`SimpleITKCSharpNative.dll` / `libSimpleITKCSharpNative.so` / `.dylib`) is copied flat into the output dir so `DllImport` resolves it.

- Default location: **`../SimpleITK/` one level above the repo root** (`SitkDir = <repoRoot>/../SimpleITK`). Drop the two DLLs there (no version subfolder). Download from [SimpleITK releases](https://github.com/SimpleITK/SimpleITK/releases), e.g. `SimpleITK-2.5.0-CSharp-win64-x64.zip`.
- Override the location with `-p:SitkDir=...` on the build command.
- If a run fails with a `TypeInitializationException` / `DllNotFoundException` from `itk.simple`, the native didn't stage — check `--version` output and `SitkDir`.
- CI stages it per-OS via `.github/scripts/stage_simpleitk.py`; `SimpleITK.props` is imported by Core, Cli, and App.

## Architecture

**One Core, two front-ends.** All conversion logic is UI-agnostic and lives in `src/DicomRtNifti.Core/Services`. Both front-ends build the same `DicomSeriesGroup` model objects and call the same services, so CLI and GUI behavior stay identical:
- `src/DicomRtNifti.Cli/HeadlessRunner.cs` — parses args, constructs series groups from explicit paths, calls the services. Modes: `--forward` (RTSTRUCT → per-ROI masks), `--reverse` (masks → RTSTRUCT, with or without a reference DICOM series), `--image-reverse` (NIfTI → DICOM image series), `--image-forward` (DICOM series → NIfTI), plus `--cohort-scan` / `--cohort-manifest` / `--cohort-convert` (see `CohortRunner.cs`; one JSON document on stdout, no `# rt_mask_validation` header). Machine-readable summary on **stdout**, human progress/errors on **stderr**. Exit codes: 0 ok, 1 conversion failure, 2 bad args — *intended* contract; today only zero-args and an unknown mode actually return 2, missing/invalid args fall through the blanket catch to 1. Mask → RT-DOSE and the drop-folder watch (server) mode are **GUI-only**; there is no CLI path to either.
- `src/DicomRtNifti.App` — Avalonia 11 + Fluent theme + `CommunityToolkit.Mvvm`, MVVM. A launcher window opens the forward (DICOM→NIfTI) or reverse (NIfTI→DICOM) window. View-models call Core services directly.

**Namespaces match assemblies.** Core types live in `DicomRtNifti.Core.Services` / `DicomRtNifti.Core.Models`, the CLI in `DicomRtNifti.Cli`, and the GUI in `DicomRtNifti.App` / `.ViewModels` / `.Views`. The App's own platform helpers (`IFolderPicker`, `AppWindows`) sit in `DicomRtNifti.App.Services` — deliberately split from `DicomRtNifti.Core.Services` — so a view-model that uses both a Core service and a folder picker carries both usings. The GUI abstracts platform dialogs behind `IFolderPicker` (Avalonia `IStorageProvider`) rather than referencing WinForms.

**Key Core services** (in `src/DicomRtNifti.Core/Services/`):
- `DicomScannerService` — recursive folder scan → Patient/Study/Series hierarchy; reconciles stray per-instance Modality to the series majority; counts unreadable files instead of silently reporting "0 patients."
- `NiftiConversionService` — orchestrates image / dose / struct → NIfTI; optional resample to fixed spacing; parallelizes per-ROI.
- `RtStructMaskService` — **the rasterization core.** Contour world-coords → continuous voxel indices via `TransformPhysicalPointToContinuousIndex` (handles arbitrary/oblique orientation), scanline polygon fill, even-odd XOR across contours on a slice for hollow shapes. Supports the five clinical `ContourGeometricType` values; `CLOSED_PLANAR_XOR` is deliberately not dispatched.
- `RtStructWriterService`, `RtDoseWriterService`, `NiftiImageWriterService` — the reverse direction (mask/NIfTI → DICOM).
- `NiftiMetadataService` — loads/synthesizes the `metadata.json` that drives NIfTI-only reverse runs (patient/study/UIDs, rescale slope/intercept).
- `AnonymizationService` + `HashNaming` — deterministic SHA256-of-salted-identifier hashing (stable folder names across re-runs); `WindowsPathSanitizer` makes every path segment valid on Windows.
- `CohortExportService` — whole-cohort orchestration shared by the CLI's `--cohort-*` modes and (eventually) the GUI's Convert Selected. Deliberately split: `BuildPlan` is **pure** — it resolves series selection, output naming and anonymization with no I/O, so those rules are unit-tested without the SimpleITK native — while `ExecuteAsync` / `ComputeManifestAsync` do the conversion by calling the services above. `ExportManifestService` owns the manifest CSV's merge semantics (upsert by the three identifier columns, union of ROI columns); its first six columns are a frozen schema, so **new per-series fields belong in the cohort JSON, never as new CSV columns**, or previously written manifests stop merging.
- `SeriesGeometryProbe` — slice spacing and uniformity from headers the scanner already read. The uniformity flag is the point: a CT with mixed slice gaps reads back one averaged spacing from any reader assuming a regular grid, which shifts contour planes during rasterization.
- `SettingsService` (JSON in `%AppData%\DicomToNifti\`), `NiftiModalityInferenceService` (infers CT/MR/PT from pixel value range).
- `DicomMetadataExtractor` (+ `MetadataTagCatalog`, `MetadataComputedValues`) — writes the optional **forward-export** `metadata.json` sidecar, one per series. Driven by a `MetadataExportRequest`, it emits a sectioned JSON — `ImageAttributes` / `StructureAttributes` / `DoseAttributes` — keyed by friendly names (PascalCase keyword split, e.g. `PatientName` → `"Patient Name"`). Image tags read from the series' first slice, structure tags from the linked RTSTRUCT, dose tags from the linked RTDOSE; each tab also offers computed values selected as `@`-prefixed pseudo-keywords (`@VoxelSize`, `@ImageDimensions`, `@RoiNames`, `@RoiCount`, `@MaxDose`, `@DoseVoxelSize`). The GUI tag picker persists three per-section keyword lists in `settings.json` (legacy flat `MetadataTagKeywords` is migrated into the image list on load). **Distinct from `NiftiMetadataService`'s reverse-direction `metadata.json` above** — same filename, different schema and purpose.

Dependencies: **fo-dicom 5.2.5** (DICOM parsing), **SimpleITK** (image I/O + NIfTI), **Newtonsoft.Json 13.0.4**, **CommunityToolkit.HighPerformance/Mvvm**.

## Conformance gate (don't break the science)

`.github/workflows/conformance-crossplatform.yml` runs the external `rtmask-conformance` tool on Windows/Linux/macOS: it generates a synthetic CT+RTSTRUCT fixture with analytic ground-truth NIfTIs, runs `DicomRtNifti.Cli --forward`, and verifies Dice/HD95/MSD/volume-error against thresholds. **No test data is committed** — the fixture is generated at job time, and the `rtmask-conformance` commit is SHA-pinned for reproducibility.

`conformance.yaml` holds per-primitive threshold overrides on top of the package defaults (`dice >= 0.95`, `surface_dice_1mm >= 0.95`, HD95 <= 2 mm, MSD <= 0.5 mm, vol_err <= 3%), each documented with why. Note the direction: the `cube` entry sets `dice >= 0.98`, which is **stricter** than the 0.95 default, not a relaxation — it pins the gate just under the measured 0.9833 so a regression in the scanline fill fails rather than sliding to the looser default. The residual gap to 1.0 is a ~half-voxel scanline boundary convention difference vs the partial-volume ground truth. **If you change `RtStructMaskService`'s rasterization, expect these metrics to move** — raise the number if accuracy improves; investigate before loosening it.

The unit tests synthesize minimal DICOM datasets in-memory with fo-dicom (`tests/.../DicomTestData.cs`) — also no committed fixtures.

**`conformance.yaml` is duplicated byte-for-byte in the parent research repo**, which runs its own
windows-latest gate against the same thresholds. Editing one copy and not the other silently splits
the two gates — change both. See the `conformance-gate` skill for the local reproduction and the
full threshold policy.
