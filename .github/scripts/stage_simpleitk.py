#!/usr/bin/env python3
"""Download a SimpleITK C# release ZIP and stage the managed wrapper + this
platform's native library into <parent-of-workspace>/SimpleITK -- the layout the
src/SimpleITK.props build expects (the same ../SimpleITK location the legacy
csproj used). Cross-platform: runs identically on Windows, Linux and macOS runners.

Usage: stage_simpleitk.py <zip-url> <github-workspace>
"""
import glob
import os
import shutil
import sys
import urllib.request
import zipfile


def find(root, name):
    hits = glob.glob(os.path.join(root, "**", name), recursive=True)
    return hits[0] if hits else None


def main():
    if len(sys.argv) != 3:
        sys.exit("usage: stage_simpleitk.py <zip-url> <github-workspace>")
    url, workspace = sys.argv[1], sys.argv[2]

    # SimpleITK.props resolves SitkDir to <repoRoot>/../SimpleITK; the runner's
    # workspace IS the repo root, so the parent of the workspace is the target.
    target = os.path.join(os.path.dirname(os.path.abspath(workspace)), "SimpleITK")
    os.makedirs(target, exist_ok=True)

    print(f"Downloading {url}")
    urllib.request.urlretrieve(url, "sitk.zip")
    with zipfile.ZipFile("sitk.zip") as z:
        z.extractall("sitk-extracted")

    managed = find("sitk-extracted", "SimpleITKCSharpManaged.dll")
    if not managed:
        sys.exit("ERROR: SimpleITKCSharpManaged.dll not found in the SimpleITK ZIP")
    shutil.copy(managed, os.path.join(target, "SimpleITKCSharpManaged.dll"))
    print(f"staged managed: {managed}")

    # Native lib name is platform-specific; copy whichever the ZIP contains.
    staged_native = False
    for native in ("SimpleITKCSharpNative.dll",
                   "libSimpleITKCSharpNative.so",
                   "libSimpleITKCSharpNative.dylib"):
        p = find("sitk-extracted", native)
        if p:
            shutil.copy(p, os.path.join(target, native))
            print(f"staged native:  {p} -> {os.path.join(target, native)}")
            staged_native = True
    if not staged_native:
        sys.exit("ERROR: no SimpleITK native library found in the ZIP")

    print(f"Contents of {target}:")
    for f in sorted(os.listdir(target)):
        full = os.path.join(target, f)
        if os.path.isfile(full):
            print(f"  {os.path.getsize(full):>12,}  {f}")


if __name__ == "__main__":
    main()
