#!/usr/bin/env python3
"""Fetch the three-patient LCTSC fixture from TCIA and verify it against the pinned manifest.

The fixture is a subset of the Lung CT Segmentation Challenge collection (LCTSC, Version 3,
CC BY 3.0): one CT series and its RTSTRUCT for each of LCTSC-Test-S1-101, LCTSC-Train-S2-006 and
LCTSC-Train-S3-009 -- three institutions, three slice spacings, three structure-set writers,
about 201 MiB in 392 files. It is downloaded at test time through the public NBIA REST API and is
never committed to this repository (see tests/fixtures/README.md for the licence and citation).

Layout written (the one the GUI's recursive scan and the research harness both accept)::

    <out>/<PatientID>/<StudyInstanceUID>/<SeriesInstanceUID>/<SOPInstanceUID>.dcm

Files are renamed to their SOPInstanceUID because the names inside NBIA's zips are sequential and
not stable, while the instance UIDs and the bytes are. Every file is checked against the SHA-256 in
``lctsc_subset.manifest.json`` next to this script; a mismatch is reported and exits 1, so a
re-versioned collection fails loudly instead of silently moving the figures and tests.

Usage::

    python tests/fixtures/fetch_lctsc_subset.py                 # fetch what is missing, verify all
    python tests/fixtures/fetch_lctsc_subset.py --verify-only   # no network: check the tree on disk
    python tests/fixtures/fetch_lctsc_subset.py --write-manifest   # maintainers: regenerate the pin

Exit codes: 0 verified; 1 hash/size mismatch, incomplete tree, or a fixture that cannot be written
(a path too long for a default Windows installation, for one); 2 network or API failure, including
a response that is not the pinned series.
Standard library only, like .github/scripts/stage_simpleitk.py, so it runs on a bare CI runner.
"""

from __future__ import annotations

import argparse
import hashlib
import io
import json
import os
import struct
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import zipfile
from pathlib import Path

API_BASE = "https://services.cancerimagingarchive.net/nbia-api/services/v1"
USER_AGENT = "DicomRtNiftiConverterGUI-fixture-fetch/1.0 (+https://github.com/brianmanderson/DicomRtNiftiConverterGUI)"

HERE = Path(__file__).resolve().parent
DEFAULT_OUT = HERE / "lctsc_subset"
MANIFEST_PATH = HERE / "lctsc_subset.manifest.json"

# The pin: which series make up the fixture. Everything else (file list, hashes, sizes) is
# derived from these by --write-manifest and frozen in the manifest.
COLLECTION = "LCTSC"
COLLECTION_VERSION = "3"
LICENSE = "CC BY 3.0"
DOI = "10.7937/K9/TCIA.2017.3R3FVZ08"
PATIENTS = [
    {
        "patient_id": "LCTSC-Test-S1-101",
        "study_uid": "1.3.6.1.4.1.14519.5.2.1.7014.4598.492964872630309412859177308186",
        "series": [
            {"modality": "CT", "series_uid": "1.3.6.1.4.1.14519.5.2.1.7014.4598.106943890850011666503487579262"},
            {"modality": "RTSTRUCT", "series_uid": "1.3.6.1.4.1.14519.5.2.1.7014.4598.280355341349691222365783556597"},
        ],
    },
    {
        "patient_id": "LCTSC-Train-S2-006",
        "study_uid": "1.3.6.1.4.1.14519.5.2.1.7014.4598.108466224681512917584454943023",
        "series": [
            {"modality": "CT", "series_uid": "1.3.6.1.4.1.14519.5.2.1.7014.4598.236204792186301490546409224643"},
            {"modality": "RTSTRUCT", "series_uid": "1.3.6.1.4.1.14519.5.2.1.7014.4598.183933370589462162407419543383"},
        ],
    },
    {
        "patient_id": "LCTSC-Train-S3-009",
        "study_uid": "1.3.6.1.4.1.14519.5.2.1.7014.4598.248484087780307011027816481070",
        "series": [
            {"modality": "CT", "series_uid": "1.3.6.1.4.1.14519.5.2.1.7014.4598.210533181201919575928229512890"},
            {"modality": "RTSTRUCT", "series_uid": "1.3.6.1.4.1.14519.5.2.1.7014.4598.272086198123850250338927546378"},
        ],
    },
]


# --------------------------------------------------------------------------------------------
#  Minimal DICOM header reader: enough to pull three tags out of Part-10 files without pydicom.
# --------------------------------------------------------------------------------------------

_TAG_TRANSFER_SYNTAX = (0x0002, 0x0010)
_TAG_SOP_INSTANCE = (0x0008, 0x0018)
_TAG_STUDY_UID = (0x0020, 0x000D)
_TAG_SERIES_UID = (0x0020, 0x000E)
_TAG_PATIENT_ID = (0x0010, 0x0020)
_TAG_MODALITY = (0x0008, 0x0060)
_WANTED = {_TAG_SOP_INSTANCE, _TAG_STUDY_UID, _TAG_SERIES_UID, _TAG_PATIENT_ID, _TAG_MODALITY}
_LONG_VRS = {b"OB", b"OD", b"OF", b"OL", b"OV", b"OW", b"SQ", b"SV", b"UC", b"UN", b"UR", b"UT", b"UV"}
_IMPLICIT_LE = "1.2.840.10008.1.2"


def _read_explicit(buf: bytes, pos: int):
    group, elem = struct.unpack_from("<HH", buf, pos)
    vr = buf[pos + 4:pos + 6]
    if vr in _LONG_VRS:
        (length,) = struct.unpack_from("<I", buf, pos + 8)
        return (group, elem), vr, length, pos + 12
    (length,) = struct.unpack_from("<H", buf, pos + 6)
    return (group, elem), vr, length, pos + 8


def _read_implicit(buf: bytes, pos: int):
    group, elem, length = struct.unpack_from("<HHI", buf, pos)
    return (group, elem), None, length, pos + 8


_ITEM = (0xFFFE, 0xE000)
_ITEM_DELIM = (0xFFFE, 0xE00D)
_SEQ_DELIM = (0xFFFE, 0xE0DD)
_UNDEFINED = 0xFFFFFFFF


def _skip_undefined_sequence(buf: bytes, pos: int, reader) -> int:
    """Position just past the Sequence Delimitation Item of an undefined-length SQ at ``pos``."""
    while True:
        tag, length, pos = _read_delimiter(buf, pos)
        if tag == _SEQ_DELIM:
            return pos
        if tag != _ITEM:
            raise ValueError(f"expected an Item in an undefined-length sequence, found {tag}")
        pos = pos + length if length != _UNDEFINED else _skip_undefined_item(buf, pos, reader)


def _skip_undefined_item(buf: bytes, pos: int, reader) -> int:
    """Position just past the Item Delimitation Item of an undefined-length Item at ``pos``."""
    while True:
        group, elem = struct.unpack_from("<HH", buf, pos)
        if (group, elem) == _ITEM_DELIM:
            return pos + 8
        tag, _vr, length, pos = reader(buf, pos)
        pos = pos + length if length != _UNDEFINED else _skip_undefined_sequence(buf, pos, reader)


def _read_delimiter(buf: bytes, pos: int):
    """Item / delimiter headers are always (tag, 4-byte length), whatever the transfer syntax."""
    group, elem, length = struct.unpack_from("<HHI", buf, pos)
    return (group, elem), length, pos + 8


def read_identity(data: bytes) -> dict:
    """PatientID, StudyInstanceUID, SeriesInstanceUID, SOPInstanceUID, Modality of a Part-10 file."""
    if data[128:132] != b"DICM":
        raise ValueError("not a DICOM Part-10 file (no DICM preamble)")
    pos = 132
    transfer_syntax = None
    # File meta information is always Explicit VR Little Endian.
    while pos < len(data):
        tag, vr, length, pos = _read_explicit(data, pos)
        if tag[0] != 0x0002:
            # First dataset element: rewind to its start.
            pos -= 12 if vr in _LONG_VRS else 8
            break
        if tag == _TAG_TRANSFER_SYNTAX:
            transfer_syntax = data[pos:pos + length].decode("ascii").rstrip("\0 ")
        pos += length
    if transfer_syntax is None:
        raise ValueError("file meta has no TransferSyntaxUID")
    reader = _read_implicit if transfer_syntax == _IMPLICIT_LE else _read_explicit

    found = {}
    while pos < len(data) and len(found) < len(_WANTED):
        tag, vr, length, pos = reader(data, pos)
        if tag > max(_WANTED):
            break
        if length == _UNDEFINED:
            # An undefined-length sequence (LCTSC's de-identification method codes, for one).
            pos = _skip_undefined_sequence(data, pos, reader)
            continue
        if tag in _WANTED:
            found[tag] = data[pos:pos + length].decode("ascii", errors="replace").rstrip("\0 ")
        pos += length
    missing = _WANTED - set(found)
    if missing:
        raise ValueError(f"identity tags missing from file: {sorted(missing)}")
    return {
        "patient_id": found[_TAG_PATIENT_ID],
        "study_uid": found[_TAG_STUDY_UID],
        "series_uid": found[_TAG_SERIES_UID],
        "sop_uid": found[_TAG_SOP_INSTANCE],
        "modality": found[_TAG_MODALITY],
    }


# --------------------------------------------------------------------------------------------
#  NBIA access
# --------------------------------------------------------------------------------------------

class NetworkError(RuntimeError):
    """The NBIA service could not be reached, or what it returned is not the pinned series."""


class WriteError(RuntimeError):
    """The fixture could not be written where it was asked to go."""


def _get(url: str, *, attempts: int = 4, timeout: int = 300) -> bytes:
    delay = 5.0
    last = None
    for attempt in range(1, attempts + 1):
        try:
            req = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})
            with urllib.request.urlopen(req, timeout=timeout) as resp:
                return resp.read()
        except (urllib.error.URLError, urllib.error.HTTPError, TimeoutError, OSError) as ex:
            last = ex
            if attempt < attempts:
                print(f"    attempt {attempt} failed ({ex}); retrying in {delay:.0f}s", file=sys.stderr)
                time.sleep(delay)
                delay *= 2
    raise NetworkError(f"GET {url} failed after {attempts} attempts: {last}")


def series_size(series_uid: str) -> tuple[int, int]:
    url = f"{API_BASE}/getSeriesSize?{urllib.parse.urlencode({'SeriesInstanceUID': series_uid})}"
    rows = json.loads(_get(url, timeout=60).decode("utf-8"))
    if not rows:
        raise NetworkError(f"getSeriesSize returned nothing for {series_uid}")
    return int(float(rows[0]["TotalSizeInBytes"])), int(rows[0]["ObjectCount"])


def download_series(series_uid: str) -> list[tuple[str, bytes]]:
    """All DICOM members of the series zip as (member name, bytes)."""
    url = f"{API_BASE}/getImage?{urllib.parse.urlencode({'SeriesInstanceUID': series_uid})}"
    blob = _get(url)
    try:
        zf = zipfile.ZipFile(io.BytesIO(blob))
    except zipfile.BadZipFile as ex:
        raise NetworkError(f"getImage for {series_uid} did not return a zip ({ex}); first bytes: {blob[:80]!r}")
    members = []
    for info in zf.infolist():
        if info.is_dir():
            continue
        data = zf.read(info)
        if len(data) >= 132 and data[128:132] == b"DICM":
            members.append((info.filename, data))
    if not members:
        raise NetworkError(f"getImage for {series_uid} contained no DICOM files")
    return members


# --------------------------------------------------------------------------------------------
#  Manifest
# --------------------------------------------------------------------------------------------

def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def load_manifest() -> dict:
    with open(MANIFEST_PATH, encoding="utf-8") as f:
        return json.load(f)


def file_path(out: Path, patient_id: str, study_uid: str, series_uid: str, sop_uid: str) -> Path:
    return out / patient_id / study_uid / series_uid / f"{sop_uid}.dcm"


def iter_manifest_files(manifest: dict):
    for patient in manifest["patients"]:
        for series in patient["series"]:
            for entry in series["files"]:
                yield patient, series, entry


def verify(out: Path, manifest: dict) -> list[str]:
    """Every manifest file present with the pinned size and SHA-256; returns the problems."""
    problems = []
    for patient, series, entry in iter_manifest_files(manifest):
        path = file_path(out, patient["patient_id"], patient["study_uid"], series["series_uid"], entry["sop_uid"])
        if not path.exists():
            problems.append(f"missing: {path.relative_to(out)}")
            continue
        data = path.read_bytes()
        if len(data) != entry["bytes"]:
            problems.append(f"size {len(data)} != {entry['bytes']}: {path.relative_to(out)}")
        elif sha256(data) != entry["sha256"]:
            problems.append(f"sha256 mismatch: {path.relative_to(out)}")
    return problems


def fetch(out: Path, manifest: dict | None, *, write_manifest: bool) -> dict:
    """Download every pinned series, lay it out, hash it. Returns the manifest describing it."""
    built = {
        "schema": 1,
        "collection": COLLECTION,
        "collection_version": COLLECTION_VERSION,
        "license": LICENSE,
        "doi": DOI,
        "api_base": API_BASE,
        "generated_utc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        "patients": [],
        "total_files": 0,
        "total_bytes": 0,
    }
    pinned = {}
    if manifest is not None:
        for patient, series, entry in iter_manifest_files(manifest):
            pinned[entry["sop_uid"]] = entry

    for patient in PATIENTS:
        p_out = {"patient_id": patient["patient_id"], "study_uid": patient["study_uid"], "series": []}
        for series in patient["series"]:
            uid = series["series_uid"]
            label = f"{patient['patient_id']} {series['modality']}"

            # Skip a series that is already complete on disk.
            if manifest is not None and not write_manifest:
                pinned_series = next((s for mp in manifest["patients"] if mp["patient_id"] == patient["patient_id"]
                                      for s in mp["series"] if s["series_uid"] == uid), None)
                if pinned_series is not None:
                    present = all(file_path(out, patient["patient_id"], patient["study_uid"], uid, e["sop_uid"]).exists()
                                  for e in pinned_series["files"])
                    if present:
                        print(f"  {label}: {len(pinned_series['files'])} files already present")
                        p_out["series"].append(pinned_series)
                        built["total_files"] += len(pinned_series["files"])
                        built["total_bytes"] += pinned_series["total_bytes"]
                        continue

            expected_bytes, expected_count = series_size(uid)
            print(f"  {label}: downloading {expected_count} files, {expected_bytes / 1e6:.1f} MB ...")
            members = download_series(uid)
            if len(members) != expected_count:
                raise NetworkError(f"{label}: getImage returned {len(members)} DICOM files, getSeriesSize says {expected_count}")

            files = []
            total = 0
            for name, data in members:
                try:
                    ident = read_identity(data)
                except (ValueError, struct.error) as ex:
                    raise NetworkError(f"{label}: zip member {name!r} is not a readable DICOM file: {ex}")
                if ident["series_uid"] != uid or ident["study_uid"] != patient["study_uid"] \
                        or ident["patient_id"] != patient["patient_id"] or ident["modality"] != series["modality"]:
                    raise NetworkError(f"{label}: a downloaded file does not belong to the pinned series: {ident}")
                path = file_path(out, patient["patient_id"], patient["study_uid"], uid, ident["sop_uid"])
                try:
                    path.parent.mkdir(parents=True, exist_ok=True)
                    path.write_bytes(data)
                except OSError as ex:
                    raise WriteError(
                        f"cannot write {path} ({len(str(path))} characters): {ex}. On Windows, enable long "
                        f"paths (LongPathsEnabled=1) or pass --out with a short root such as D:\\lctsc.")
                files.append({"sop_uid": ident["sop_uid"], "bytes": len(data), "sha256": sha256(data)})
                total += len(data)
            if total != expected_bytes:
                raise NetworkError(f"{label}: downloaded {total} bytes, getSeriesSize says {expected_bytes}")
            files.sort(key=lambda e: e["sop_uid"])
            p_out["series"].append({
                "modality": series["modality"], "series_uid": uid,
                "object_count": len(files), "total_bytes": total, "files": files,
            })
            built["total_files"] += len(files)
            built["total_bytes"] += total
        built["patients"].append(p_out)
    return built


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("--out", type=Path, default=DEFAULT_OUT, help=f"download target (default {DEFAULT_OUT})")
    ap.add_argument("--verify-only", action="store_true", help="no network: verify the tree against the manifest")
    ap.add_argument("--write-manifest", action="store_true",
                    help="maintainers: re-download everything and rewrite the manifest from what NBIA served")
    args = ap.parse_args(argv)
    out: Path = args.out

    if args.write_manifest:
        if MANIFEST_PATH.exists() and not os.environ.get("LCTSC_FIXTURE_FORCE"):
            print(f"{MANIFEST_PATH} exists; set LCTSC_FIXTURE_FORCE=1 to overwrite the pin.", file=sys.stderr)
            return 1
        try:
            built = fetch(out, None, write_manifest=True)
        except WriteError as ex:
            print(f"ERROR: {ex}", file=sys.stderr)
            return 1
        except NetworkError as ex:
            print(f"ERROR: {ex}", file=sys.stderr)
            return 2
        MANIFEST_PATH.write_text(json.dumps(built, indent=1) + "\n", encoding="utf-8")
        print(f"wrote {MANIFEST_PATH}: {built['total_files']} files, {built['total_bytes']} bytes")
        return 0

    if not MANIFEST_PATH.exists():
        print(f"manifest missing: {MANIFEST_PATH}", file=sys.stderr)
        return 1
    manifest = load_manifest()

    if not args.verify_only:
        try:
            fetch(out, manifest, write_manifest=False)
        except WriteError as ex:
            print(f"ERROR: {ex}", file=sys.stderr)
            return 1
        except NetworkError as ex:
            print(f"ERROR: {ex}", file=sys.stderr)
            return 2

    problems = verify(out, manifest)
    if problems:
        for p in problems[:20]:
            print(f"MISMATCH {p}", file=sys.stderr)
        if len(problems) > 20:
            print(f"... and {len(problems) - 20} more", file=sys.stderr)
        print(f"FAILED: {len(problems)} of {manifest['total_files']} files differ from the pinned manifest "
              f"(collection {manifest['collection']} v{manifest['collection_version']}).", file=sys.stderr)
        return 1
    print(f"OK: {manifest['total_files']} files, {manifest['total_bytes']} bytes verified under {out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
