# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Cross-platform (.NET 10) toolkit that converts DICOM radiotherapy data — CT/MR/PT image series, RT Structures, and RT Dose — to NIfTI (`.nii.gz`), and performs the reverse (mask → RTSTRUCT, NIfTI → DICOM image series). Ships as a headless CLI (`DicomRtNifti.Cli`) and an Avalonia desktop GUI (`DicomRtNifti.App`) that share one Core library. The rasterizer backs a CMPB software-publication paper, so analytical accuracy is gated in CI (see Conformance below).

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
- `src/DicomRtNifti.Web` — web front-end (preview): Blazor Server over Core, loopback only. `Hosting/` holds the data-root resolver (the only filesystem access), the path redactor and the binding guard; each page is a `Features/<Name>/` folder with an `IWebFeature` the shell discovers. `Nullable` and `ImplicitUsings` are on in this project only.
- `tests/DicomRtNifti.Core.Tests` — xUnit tests for Core.
- `tests/DicomRtNifti.Web.Tests` — web tests without a browser: bUnit, `WebApplicationFactory`, Kestrel on loopback; the path-traversal table and the architecture tests (no fo-dicom or SimpleITK type, no filesystem call outside the resolver). Its own workflow, `web-crossplatform.yml`.
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
- `src/DicomRtNifti.Cli/` — one file per mode under `Modes/`, each declaring its flag, its `--help` block and its entry point; `HeadlessRunner.cs` discovers them at startup and dispatches in their `Order` (single-series modes first, so a cohort flag never shadows them), and holds the helpers several modes share. The modes construct series groups from explicit paths and call the services. The cohort modes' JSON documents are built in Core (`src/DicomRtNifti.Core/Documents/`) and their command line is parsed by Core's `CohortCommandLine` (file under `Core/Cli/`, namespace `DicomRtNifti.Core.CommandLine`, since a `Core.Cli` namespace would shadow `DicomRtNifti.Cli`), whose `Render` turns options back into the command line; `--help` and the documents are pinned by goldens in `tests/DicomRtNifti.Core.Tests/Goldens/`. `--dry-run` on `--cohort-manifest` / `--cohort-convert` prints the plan (`dicomrtnifti.cohort-plan/1`) and writes nothing, not even the anonymization key; `--cohort-scan --anonymize` hashes identifiers as the export does. Modes: `--forward` (RTSTRUCT → per-ROI masks), `--reverse` (masks → RTSTRUCT, with or without a reference DICOM series), `--image-reverse` (NIfTI → DICOM image series), `--image-forward` (DICOM series → NIfTI), plus `--cohort-scan` / `--cohort-manifest` / `--cohort-convert` (their shared plumbing is `CohortRunner.cs`; one JSON document on stdout, no `# rt_mask_validation` header). Machine-readable summary on **stdout**, human progress/errors on **stderr**. Exit codes: 0 ok; 2 when the command line cannot be acted on (no or unknown mode, a missing or malformed flag, an input path that does not exist), reported without a stack trace. Only argument checks raise the `CommandLineUsageException` that maps to 2 (the CLI's own as its subclass `CliUsageException`, and Core's cohort grammar), so an exception from inside a conversion (a malformed input file, a missing assembly) keeps exit 1 and its stack trace whatever its type. 1 also covers two checks the caller opted into: `--strict-reference` on `--forward` (the image series is none of the series the RTSTRUCT references, or, when it references none, its frame of reference is none of the RTSTRUCT's; warned about by default) and `--only-associated-rois` on the cohort modes when the associations match no ROI in any structure set that converted (the manifest and the JSON, whose `errors` carry the explanation, are still written). Residual exposure of the cohort routes: a structure set whose referenced series is absent from the study and whose frame of reference matches no image series is linked to the study's largest series (`RtLinkMatchRule.LargestSeriesFallback`) and reported as such in the cohort JSON, not refused. The scanner reads a structure set's frame of reference from `ReferencedFrameOfReferenceSequence` and the per-ROI tag as well as the top-level tag, which RTSTRUCTs normally omit. Mask → RT-DOSE and the drop-folder watch (server) mode are **GUI-only**; there is no CLI path to either.
- `src/DicomRtNifti.App` — Avalonia 11 + Fluent theme + `CommunityToolkit.Mvvm`, MVVM. A launcher window opens the forward (DICOM→NIfTI) or reverse (NIfTI→DICOM) window. View-models call Core services directly. The forward window owns no export logic: `MainViewModel.ExportOptionsFor` maps its controls (ticked patients and series, the per-series structure-set picker, the Export Options toggles and filters, the ROI selection) to a `CohortExportOptions`; `CohortExportService.BuildPlan` decides what is written where; `ExportPlanViewModel` shows that plan, read from Core's `dicomrtnifti.cohort-plan/1` document, before `ExecuteAsync` / `ComputeManifestAsync` run it; the progress bar follows `IProgress<CohortProgress>` and `IProgress<ScanProgress>`. Don't add naming, hashing, selection or manifest code to a view-model: `tests/DicomRtNifti.App.Tests/CohortExportParityTests.cs` asserts that Convert Selected writes the same bytes as the CLI's options for the same export.

**Namespaces match assemblies.** Core types live in `DicomRtNifti.Core.Services` / `DicomRtNifti.Core.Models`, the CLI in `DicomRtNifti.Cli`, and the GUI in `DicomRtNifti.App` / `.ViewModels` / `.Views`. The App's own platform helpers (`IFolderPicker`, `AppWindows`) sit in `DicomRtNifti.App.Services` — deliberately split from `DicomRtNifti.Core.Services` — so a view-model that uses both a Core service and a folder picker carries both usings. The GUI abstracts platform dialogs behind `IFolderPicker` (Avalonia `IStorageProvider`) rather than referencing WinForms.

**Key Core services** (in `src/DicomRtNifti.Core/Services/`):
- `DicomScannerService` — recursive folder scan → Patient/Study/Series hierarchy; reconciles stray per-instance Modality to the series majority; counts unreadable files instead of silently reporting "0 patients." The directory walk streams paths through a channel into 2×cores parallel **synchronous** header reads (`DicomFile.Open`, `SkipLargeTags`). Don't "restore" `OpenAsync`: fo-dicom reads a synchronous `FileStream` either way, and the old task-per-file `OpenAsync` scan therefore ran on one thread. Results are made deterministic by `SortHierarchy`, never by read order.
- `NiftiConversionService` — orchestrates image / dose / struct → NIfTI; optional resample to fixed spacing; parallelizes per-ROI.
- `RtStructMaskService` — **the rasterization core.** Contour world-coords → continuous voxel indices via `TransformPhysicalPointToContinuousIndex` (handles arbitrary/oblique orientation), scanline polygon fill, even-odd XOR across contours on a slice for hollow shapes. Supports the five clinical `ContourGeometricType` values; `CLOSED_PLANAR_XOR` is deliberately not dispatched.
- `RtStructWriterService`, `RtDoseWriterService`, `NiftiImageWriterService` — the reverse direction (mask/NIfTI → DICOM).
- `NiftiMetadataService` — loads/synthesizes the `metadata.json` that drives NIfTI-only reverse runs (patient/study/UIDs, rescale slope/intercept).
- `AnonymizationService` + `HashNaming` — deterministic SHA256-of-salted-identifier hashing (stable folder names across re-runs); `WindowsPathSanitizer` makes every path segment valid on Windows. `AnonymizationKeyLocation` is the only way a front-end places the key: always written, never inside the export (default `<output>.key/AnonymizationKey.json`; `--key-path` / the GUI's Key location override it; an in-root key from an older export is merged in and reported, never deleted).
- `CohortExportService` — whole-cohort orchestration shared by the CLI's `--cohort-*` modes and the GUI's Convert Selected and Export Manifest Only. Deliberately split: `BuildPlan` is **pure** — it resolves series selection, output naming and anonymization with no I/O, so those rules are unit-tested without the SimpleITK native — while `ExecuteAsync` / `ComputeManifestAsync` do the conversion by calling the services above. It owns every selection a front-end makes, so none re-implements one: `SeriesInstanceUids` (after the patient filter, before the description filters, each exclusion in `Skipped`), `StructureSetUids` (where a series has several structure sets: a listed one, then `StructDescriptionFilter`'s match, then the first), and `RoiSelection`, which `ResolveRois` turns into the associations and unmatched-ROI rule every series uses through `Models/RoiSelection.cs` (the GUI's Select ROIs rule; the plan document goes through it too). Progress has two channels: the string log, which the CLI prints and nothing else may change, and an optional `IProgress<CohortProgress>` (`SeriesStarted` / `SeriesFinished` per series, then `Completed`, with running counts) sent in plan order where the run records each series, never from a worker, so it is ordered under `SeriesParallelism` too. `DicomScannerService.ScanFolderAsync` has the scan's equivalent, `IProgress<ScanProgress>`. `ExportManifestService` owns the manifest CSV's merge semantics (upsert by the three identifier columns, union of ROI columns); its first six columns are a frozen schema, so **new per-series fields belong in the cohort JSON, never as new CSV columns**, or previously written manifests stop merging.
- `SeriesGeometryProbe` — slice spacing and uniformity from headers the scanner already read. The uniformity flag is the point: a CT with mixed slice gaps reads back one averaged spacing from any reader assuming a regular grid, which shifts contour planes during rasterization. `SeriesGeometry.Spacing` is `[x, y, z]`; `PixelSpacing` stays DICOM's `[row, column]` (issue 70).
- `Analysis/` (namespace `DicomRtNifti.Core.Analysis`) — the cohort analysis behind `--cohort-analyze` and the web's Analysis pages, pure over a scan: `CohortAnalysisService.Analyze` (scan → `CohortAnalysis`, identifiers hashed when given a key), `LinkageAudit`, `ImageGeometry` (per-series facts and cohort distributions), `SeriesOrientation` (plane and slice-normal tilt from IOP), `RoiNamingAnalysis` (counts as the GUI's ROI browser and Select ROIs count, drops from `ResolveRoiNames`), `SeriesFlags` (the GUI's badge predicates, copied verbatim, plus the analysis' own), `FlagCodes` (the fixed code list; a new code needs README's table and a test), `CohortAnalysisDocument` (`dicomrtnifti.cohort-analysis/1`; the model types are the document's shape, so a property added is a field published) and `CohortAnalysisCsv`. Only `--tier header` exists; contours and raster are refused (exit 2) until W4-I and W4-K. The scanner keeps what it needs (`DicomSeriesGroup`'s orientation, grid, rescale, frames, modality tally, a structure set's frames and repeated ROI names), first-file values with order-independent "varies" flags so documents do not depend on the OS's walk order.
- `SettingsService` (JSON in `%AppData%\DicomToNifti\`), `NiftiModalityInferenceService` (infers CT/MR/PT from pixel value range).
- `DicomMetadataExtractor` (+ `MetadataTagCatalog`, `MetadataComputedValues`) — writes the optional **forward-export** `metadata.json` sidecar, one per series. Driven by a `MetadataExportRequest`, it emits a sectioned JSON — `ImageAttributes` / `StructureAttributes` / `DoseAttributes` — keyed by friendly names (PascalCase keyword split, e.g. `PatientName` → `"Patient Name"`). Image tags read from the series' first slice, structure tags from the linked RTSTRUCT, dose tags from the linked RTDOSE; each tab also offers computed values selected as `@`-prefixed pseudo-keywords (`@VoxelSize`, `@ImageDimensions`, `@RoiNames`, `@RoiCount`, `@MaxDose`, `@DoseVoxelSize`). The GUI tag picker persists three per-section keyword lists in `settings.json` (legacy flat `MetadataTagKeywords` is migrated into the image list on load). **Distinct from `NiftiMetadataService`'s reverse-direction `metadata.json` above** — same filename, different schema and purpose.

Dependencies: **fo-dicom 5.2.5** (DICOM parsing), **SimpleITK** (image I/O + NIfTI), **Newtonsoft.Json 13.0.4**, **CommunityToolkit.HighPerformance/Mvvm**.

## Conformance gate (don't break the science)

`.github/workflows/conformance-crossplatform.yml` runs the external `rtmask-conformance` tool on Windows/Linux/macOS: it generates a synthetic CT+RTSTRUCT fixture with analytic ground-truth NIfTIs, runs `DicomRtNifti.Cli --forward`, and verifies Dice/HD95/MSD/volume-error against thresholds. **No test data is committed** — the fixture is generated at job time, and the `rtmask-conformance` commit is SHA-pinned for reproducibility.

`conformance.yaml` holds per-primitive threshold overrides on top of the package defaults (`dice >= 0.95`, `surface_dice_1mm >= 0.95`, HD95 <= 2 mm, MSD <= 0.5 mm, vol_err <= 3%), each documented with why. Note the direction: the `cube` entry sets `dice >= 0.99`, which is **stricter** than the 0.95 default, not a relaxation — it pins the gate just under the measured 1.0000 so a regression in the scanline fill fails rather than sliding to the looser default. **If you change `RtStructMaskService`'s rasterization, expect these metrics to move** — raise the number if accuracy improves; investigate before loosening it.

This file previously said the cube's residual gap to 1.0 was "a ~half-voxel scanline boundary convention difference vs the partial-volume ground truth", i.e. intentional. That was wrong, and it is worth knowing why it survived so long. The scanline fill sampled each row at `y + 0.5` while the X fill sampled at the voxel centre, so every mask came out displaced −0.5 voxels in y. On the cube, whose faces land on voxel boundaries, the shift rounded to a whole voxel: the shape stayed exact and the volume error was therefore exactly 0.00, which is precisely what made it look like a benign convention choice. The HD95 of 1.0 mm and MSD of 0.33 mm that were read as evidence the boundary was "in the right place" were the signature of the displacement. Fixed in `ScanlineFillPolygon` (`scanY = y`); cube is now an exact match.

The unit tests synthesize minimal DICOM datasets in-memory with fo-dicom (`tests/.../DicomTestData.cs`) — also no committed fixtures.

**`conformance.yaml` is duplicated byte-for-byte in the parent research repo**, which runs its own
windows-latest gate against the same thresholds. Editing one copy and not the other silently splits
the two gates — change both. See the `conformance-gate` skill for the local reproduction and the
full threshold policy.
