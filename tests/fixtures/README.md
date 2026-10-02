# Test fixtures

No DICOM is committed to this repository (`*.dcm` is gitignored). The two fixtures the tests and
the figure captures use are produced at test time:

| Fixture | How it is produced | Used by |
|---|---|---|
| Synthetic CT + RTSTRUCT with analytic ground truth | `rtmask-conformance generate` (SHA-pinned in `.github/workflows/conformance-crossplatform.yml`) | the CLI conformance gate; the headless GUI tests when `RTMASK_FIXTURE_DIR` points at it |
| Three-patient LCTSC subset (real, de-identified clinical data) | `python tests/fixtures/fetch_lctsc_subset.py` | GUI walkthrough figures; optional local GUI runs |

When neither is present, the headless GUI tests synthesize a small CT + RTSTRUCT in memory with
fo-dicom, so `dotnet test` works on a bare checkout.

## The LCTSC subset

`fetch_lctsc_subset.py` downloads one CT series and its RTSTRUCT for each of three patients from
the public NBIA REST API, lays them out as `<PatientID>/<StudyInstanceUID>/<SeriesInstanceUID>/<SOPInstanceUID>.dcm`
under `tests/fixtures/lctsc_subset/` (gitignored), and verifies every file against the SHA-256
pinned in `lctsc_subset.manifest.json`. A mismatch exits 1; the manifest is only ever rewritten
deliberately with `--write-manifest`.

| Patient | Institution / split | CT | RTSTRUCT writer |
|---|---|---|---|
| LCTSC-Test-S1-101 | S1, test (the manuscripts' representative case) | 130 slices, 0.977 × 0.977 × 3.0 mm | Plastimatch |
| LCTSC-Train-S2-006 | S2, train | 144 slices, 0.977 × 0.977 × 2.5 mm | MIM |
| LCTSC-Train-S3-009 | S3, train | 115 slices, 1.172 × 1.172 × 3.0 mm | MIM |

Each RTSTRUCT holds the five LCTSC organs at risk: Esophagus, Heart, Lung_L, Lung_R, SpinalCord.
About 201 MiB in 392 files. The files are de-identified at source (DICOM PS3.15 Annex E basic
profile; `PatientIdentityRemoved = YES`) and are stored unmodified, because any rewrite would break
the SHA-256 contract.

```bash
python tests/fixtures/fetch_lctsc_subset.py            # fetch what is missing, verify everything
```

```bash
python tests/fixtures/fetch_lctsc_subset.py --verify-only
```

The script needs only the Python standard library.

### Licence and attribution

This fixture is a three-patient subset of the TCIA **Lung CT Segmentation Challenge (LCTSC)**
collection, Version 3, used under the
[Creative Commons Attribution 3.0 Unported licence (CC BY 3.0)](https://creativecommons.org/licenses/by/3.0/).
It is downloaded at test time from the public NBIA REST API and is not redistributed in this
repository. Use is subject to the
[TCIA Data Usage Policy](https://www.cancerimagingarchive.net/data-usage-policies-and-restrictions/);
do not attempt to re-identify participants.

Please cite the data and TCIA when the fixture contributes to published work:

- Yang, J., Sharp, G., Veeraraghavan, H., Van Elmpt, W., Dekker, A., Lustberg, T., & Gooding, M.
  (2017). *Data from Lung CT Segmentation Challenge (LCTSC)* (Version 3) [Data set]. The Cancer
  Imaging Archive. https://doi.org/10.7937/K9/TCIA.2017.3R3FVZ08
- Yang, J., Veeraraghavan, H., Armato, S. G., et al. (2018). Autosegmentation for thoracic radiation
  treatment planning: A grand challenge at AAPM 2017. *Medical Physics*, 45(10), 4568–4581.
  https://doi.org/10.1002/mp.13141
- Clark, K., Vendt, B., Smith, K., et al. (2013). The Cancer Imaging Archive (TCIA): Maintaining
  and Operating a Public Information Repository. *Journal of Digital Imaging*, 26(6), 1045–1057.
  https://doi.org/10.1007/s10278-013-9622-7
