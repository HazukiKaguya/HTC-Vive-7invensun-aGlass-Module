"""Standalone build/test/package entry point; Python standard library only."""
from concurrent.futures import ThreadPoolExecutor
import hashlib
import http.client
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import time
import urllib.request
from urllib.parse import urlsplit
import uuid
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[1]
CACHE = ROOT / ".build"
COMMIT = "ad06f2967a2243d85ad161a397eb911522182c15"
SOURCE_SHA256 = "c087151c077e9df3c7befeebe5f5bb09cb1850b4a80001502ba7c83c18f0a629"
PAGE = "https://github.com/HazukiKaguya/HTC-Vive-7invensun-aGlass-Module"
PACKAGES = {
    "microsoft.extensions.logging.abstractions.7.0.0": "ba83245ff4e7c0fd1869b4e169c4cc9b2c5d73d8ad429ef708def1105168c7be",
    "newtonsoft.json.13.0.2": "112ca3b7f47bcbd743befcf949fb68ce4f9eff73ad9f7f1b39c2c61a1ebd3add",
    "microsoft.netcore.app.ref.7.0.20": "5bd454ddb8dae0140b01bb1a22100d40f24987a0f270388147e599de62ba66bb",
    "microsoft.aspnetcore.app.ref.7.0.20": "3840d75e34351c344f880e18d733ebd7151e1fac25cd3089a6db3e0d92fad702",
    "microsoft.windowsdesktop.app.ref.7.0.20": "5b00a010bebf0542d0d2cfd3079ca3ccf3f71b38ff39d9761a7141acdf06cdac",
}


def run(*args, **kwargs):
    return subprocess.run([str(a) for a in args], cwd=ROOT, check=True, **kwargs)


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def download(url, path, expected):
    path.parent.mkdir(parents=True, exist_ok=True)
    if not path.exists():
        print("Downloading " + path.name, flush=True)
        partial = path.with_suffix(path.suffix + ".partial")
        for attempt in range(1, 4):
            try:
                request = urllib.request.Request(url, headers={"User-Agent": "aGlassVRCFT-build/0.2.0"})
                with urllib.request.urlopen(request, timeout=60) as response, partial.open("wb") as output:
                    shutil.copyfileobj(response, output, length=64 * 1024)
                if digest(partial) != expected:
                    raise RuntimeError("Download checksum mismatch: " + path.name)
                partial.replace(path)
                break
            except (OSError, http.client.HTTPException) as error:
                if attempt == 3:
                    raise RuntimeError(f"Download failed after 3 attempts: {path.name}: {error}") from error
                print(f"Retrying {path.name} ({attempt}/3): {error}", flush=True)
                time.sleep(attempt)
            finally:
                partial.unlink(missing_ok=True)
    if digest(path) != expected:
        raise RuntimeError("Cached checksum mismatch; remove this file and retry: " + str(path))


def validate_manifest(manifest, version):
    url = manifest.get("ModulePageUrl")
    if not isinstance(url, str) or not url or any(c.isspace() for c in url):
        raise ValueError("ModulePageUrl must be a nonempty absolute HTTP(S) URL")
    parsed = urlsplit(url)
    if parsed.scheme not in ("http", "https") or not parsed.hostname:
        raise ValueError("ModulePageUrl must be an absolute HTTP(S) URL")
    if url != PAGE:
        raise ValueError("ModulePageUrl must point to the project's GitHub page: " + PAGE)
    if manifest.get("Version") != version or manifest.get("DllFileName") != "aGlassVRCFT.dll":
        raise ValueError("Manifest version or DLL name does not match the project")
    if str(uuid.UUID(manifest["ModuleId"])) != "8ea02335-197a-4822-81ed-3e414bf4a1e3":
        raise ValueError("Preserve ModuleId when updating an existing module")
    for key in ("ModuleName", "AuthorName", "ModuleDescription", "UsageInstructions", "LastUpdated"):
        if not isinstance(manifest.get(key), str) or not manifest[key].strip():
            raise ValueError("Missing manifest field: " + key)


def prepare_dependencies():
    archive = CACHE / "downloads" / "vrcft-source.zip"
    jobs = [(f"https://codeload.github.com/benaclejames/VRCFaceTracking/zip/{COMMIT}", archive, SOURCE_SHA256)]
    feed = CACHE / "nuget-feed"
    for package, checksum in PACKAGES.items():
        parts = package.split(".")
        name, version = ".".join(parts[:-3]), ".".join(parts[-3:])
        jobs.append((f"https://api.nuget.org/v3-flatcontainer/{name}/{version}/{package}.nupkg", feed / (package + ".nupkg"), checksum))
    with ThreadPoolExecutor(max_workers=4) as pool:
        for future in [pool.submit(download, *job) for job in jobs]:
            future.result()
    source = CACHE / "source" / f"VRCFaceTracking-{COMMIT}"
    # Refresh source files from the verified archive. Extract only the Core
    # build inputs; no external Git checkout or installed VRCFT is required.
    with zipfile.ZipFile(archive) as z:
        prefix = f"VRCFaceTracking-{COMMIT}/"
        for entry in z.infolist():
            if entry.is_dir() or not entry.filename.startswith(prefix):
                continue
            relative = entry.filename[len(prefix):]
            if not (relative.startswith("VRCFaceTracking.Core/") or relative in ("fti_osc.dll", "LICENSE", "LICENSE.txt")):
                continue
            destination = (source / relative).resolve()
            if not destination.is_relative_to(source.resolve()):
                raise ValueError("Unsafe source archive path")
            destination.parent.mkdir(parents=True, exist_ok=True)
            content = z.read(entry)
            if not destination.exists() or destination.read_bytes() != content:
                destination.write_bytes(content)
    return source, feed


def main():
    if sys.version_info < (3, 10):
        raise RuntimeError("Python 3.10 or newer is required for building")
    if not shutil.which("dotnet"):
        raise RuntimeError("Install the .NET 8 SDK and reopen the terminal")
    version = ET.parse(ROOT / "aGlassVRCFT.csproj").findtext("./PropertyGroup/Version")
    manifest = json.loads((ROOT / "module.json").read_text("utf-8"))
    validate_manifest(manifest, version)
    config = json.loads((ROOT / "aGlassVRCFT.json").read_text("utf-8"))
    if config.get("Source") != "runtime":
        raise ValueError("The distributed default must be Source=runtime")
    run(sys.executable, ROOT / "tests" / "test_release.py")
    source, feed = prepare_dependencies()
    env = dict(os.environ, DOTNET_CLI_HOME=str(CACHE / "dotnet-home"),
               NUGET_PACKAGES=str(CACHE / "nuget-packages"), DOTNET_NOLOGO="1",
               DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_SKIP_FIRST_TIME_EXPERIENCE="1")
    flags = ("-c", "Release", "--source", feed, "-p:NuGetAudit=false", "--nologo")
    core = source / "VRCFaceTracking.Core/bin/Release/net7.0/VRCFaceTracking.Core.dll"
    run("dotnet", "build", source / "VRCFaceTracking.Core/VRCFaceTracking.Core.csproj", *flags, env=env)
    run("dotnet", "build", ROOT / "tests/ModuleTests.csproj", *flags, f"-p:VrcftCorePath={core}", env=env)
    run(sys.executable, ROOT / "tests/check_module.py", env=env)
    files = {"aGlassVRCFT.dll": ROOT / "bin/Release/net7.0/aGlassVRCFT.dll",
             **{name: ROOT / name for name in ("module.json", "aGlassVRCFT.json", "README.md", "LICENSE")}}
    release = ROOT / "release"
    release.mkdir(exist_ok=True)
    archive = release / f"aGlassVRCFT-{version}.zip"
    partial = archive.with_suffix(".zip.partial")
    try:
        with zipfile.ZipFile(partial, "w", compression=zipfile.ZIP_DEFLATED) as z:
            for name, path in sorted(files.items()):
                info = zipfile.ZipInfo(name, (2026, 1, 1, 0, 0, 0))
                info.compress_type = zipfile.ZIP_DEFLATED
                info.external_attr = 0o100644 << 16
                z.writestr(info, path.read_bytes())
        with zipfile.ZipFile(partial) as z:
            if z.testzip() is not None or set(z.namelist()) != set(files):
                raise RuntimeError("Invalid release archive contents")
            validate_manifest(json.loads(z.read("module.json")), version)
            if json.loads(z.read("aGlassVRCFT.json")) != config:
                raise RuntimeError("Packaged configuration differs from source")
        partial.replace(archive)
    finally:
        partial.unlink(missing_ok=True)
    archive.with_suffix(".zip.sha256").write_text(digest(archive) + "  " + archive.name + "\n", encoding="utf-8")
    print("Built, tested and packaged: " + str(archive), flush=True)


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, RuntimeError, subprocess.CalledProcessError) as error:
        print("Build failed: " + str(error), file=sys.stderr)
        sys.exit(1)
