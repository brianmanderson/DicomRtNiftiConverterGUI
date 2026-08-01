---
name: conformance-gate
description: How to reproduce the three-OS analytic accuracy gate locally, and the required procedure after any change to RtStructMaskService or the rasterization path. Load this BEFORE editing the rasterizer, before editing conformance.yaml thresholds, and before claiming a rasterization change is done. Skipping it means shipping a scanline regression that the gate exists to catch, or "fixing" a red build by loosening a threshold that is deliberately pinned tighter than the package default.
---

# The conformance gate

`.github/workflows/conformance-crossplatform.yml` proves the rasterizer against closed-form
geometry on Windows, Linux and macOS. A platform is only claimed as supported once this is green
on it. The gate backs a CMPB software publication — its thresholds are published claims, not
build settings.

No test data is committed: the fixture (synthetic CT + RTSTRUCT + analytic ground-truth NIfTIs)
is generated at job time, and the `rtmask-conformance` commit is SHA-pinned via
`RTMASK_CONFORMANCE_REF` for reproducibility.

## Reproduce it locally

Install the harness at the pinned SHA (check `RTMASK_CONFORMANCE_REF` in the workflow for the
current value; when working inside the parent research checkout, the harness is already installed
in `../PythonCode/.venv` from `../external/RTMaskConformanceTest`):

```bash
python -m pip install "git+https://github.com/brianmanderson/RTMaskConformanceTest@<RTMASK_CONFORMANCE_REF>"
```

Then, from the repo root — `_conformance/` is gitignored, so put artifacts there:

```bash
dotnet build DicomRtNifti.sln -c Release
```

```bash
dotnet run --project src/DicomRtNifti.Cli -c Release --no-build -- --version
```

That must print `SimpleITK native: OK`. **`--version` exits 0 even when the native fails to
load**, so assert on the probe line, never the exit code — CI does the same. A failure here is a
staging problem, not a rasterizer problem; see the SimpleITK section of CLAUDE.md.

```bash
rtmask-conformance generate ./_conformance/fixture --n-quadrature 2
```

```bash
dotnet run --project src/DicomRtNifti.Cli -c Release --no-build -- --forward --rtstruct ./_conformance/fixture/rtstruct/primitives_planar.dcm --image-folder ./_conformance/fixture/refct --output-folder ./_conformance/predictions
```

```bash
rtmask-conformance verify --predictions ./_conformance/predictions --groundtruth ./_conformance/fixture/groundtruth --config ./conformance.yaml
```

Green is `7/7 passed` over sphere, cube, cylinder, ellipsoid, torus, hollow_sphere, straw.
`--n-quadrature 2` is ~16x faster than the `n=8` reference fixture and still stable to well under
a voxel. `--report-only` prints metrics and exits 0 — the only sanctioned way to inspect numbers
you expect to fail.

## After changing RtStructMaskService

The rasterizer is `src/DicomRtNifti.Core/Services/RtStructMaskService.cs`: world coords →
continuous voxel indices via `TransformPhysicalPointToContinuousIndex`, scanline polygon fill,
even-odd XOR across contours on a slice for hollow shapes.

**Any change here moves the metrics. Expect it, and check it before claiming done:**

1. Run the local gate above and compare against the current numbers — cube 1.0000, sphere 0.9964,
   cylinder 0.9885, ellipsoid 0.9954, torus 0.9915, hollow_sphere 0.9952, straw 0.9910.
2. Run the Core unit tests: `dotnet test tests/DicomRtNifti.Core.Tests/DicomRtNifti.Core.Tests.csproj -c Release`
3. If accuracy **improved**, raise the affected threshold in `conformance.yaml` to just under the
   new measured value and update its inline comment. That is how the gate ratchets.
4. If accuracy **regressed**, fix the rasterizer. Do not loosen the threshold.
5. Tell the user — a rasterization change also moves the parent repo's benchmark numbers and the
   manuscript values that quote them.

## The cube threshold is deliberately strict

`conformance.yaml` overrides shallow-merge over the package defaults (`dice >= 0.95`,
`surface_dice_1mm >= 0.95`, HD95 <= 2 mm, MSD <= 0.5 mm, vol_err <= 3%).

The `cube` entry sets `dice: 0.99` — **stricter than the 0.95 default, not a relaxation.** It is
pinned just under the measured 1.0000 so a scanline-fill regression fails the build rather than
sliding down to the looser default.

**A cautionary note, because this file got it wrong for a long time.** The cube used to measure
0.9833, and this section asserted that the gap was "a known ~half-voxel scanline boundary-convention
difference", reasoning that the volume error was exactly 0.00 and the surface metrics were fine, so
"the boundary is in the right place — documented and intentional, not an open bug." It was a bug.
The fill sampled each row at `y + 0.5` while the X fill sampled at the voxel centre, displacing every
mask −0.5 voxels in y. The cube hid it best: its faces land on voxel boundaries, so the shift rounded
to a whole voxel, leaving the shape exact and the volume error at exactly 0.00. The HD95 of 1.0 mm
and MSD of 0.33 mm cited as proof of correct placement were the displacement's signature. Fixed
(`scanY = y`); every primitive improved and the cube became an exact match.

The lesson worth keeping: a volume-error metric is translation-invariant, so it cannot see a rigid
shift. When a residual is explained away as a convention, check a metric that *can* see position.

Nothing in the file is currently looser than the package default. If you ever need to add
something that is, say so explicitly and raise it with the user.

## conformance.yaml is duplicated in the parent repo

The parent research repo carries a byte-identical copy at its own root, used by its
windows-latest workflow. **Editing one and not the other silently splits the two gates.** Change
both, in the same pair of PRs.

## Related

- Parent repo skill `conformance-gate` — the windows-latest gate, the image-reverse lanes, and the
  anchor CSVs that also move when the rasterizer changes.
- CLAUDE.md — SimpleITK native staging, Core service map, architecture.
