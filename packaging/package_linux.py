#!/usr/bin/env python3
"""Build a self-contained Linux release archive and offline Bash installer. Stdlib only."""
import argparse
import gzip
import hashlib
from pathlib import Path
import re
import shutil
import subprocess
import tarfile
import tempfile
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
MACHINES = {"linux-x64": 62, "linux-arm64": 183}


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def validate_binary(path, rid):
    with path.open("rb") as stream:
        header = stream.read(20)
    if (len(header) < 20 or header[:6] != b"\x7fELF\x02\x01"
            or int.from_bytes(header[18:20], "little") != MACHINES[rid]):
        raise ValueError(f"{path} is not a 64-bit little-endian ELF executable for {rid}")


def stage_release(binary_dir, target, version, rid):
    validate_binary(binary_dir / "cherbot", rid)
    target.mkdir()
    # Only select build outputs; never copy runtime data from an input directory.
    for name in ("cherbot", "appsettings.json"):
        shutil.copyfile(binary_dir / name, target / name)
    for name in ("install.sh", "cherbot.service", "cherbot.user.service"):
        shutil.copyfile(ROOT / "packaging/linux" / name, target / name)
    shutil.copyfile(ROOT / "packaging/README-install.md", target / "README-install.md")
    shutil.copyfile(ROOT / "packaging/cherbot.example.json", target / "cherbot.example.json")
    shutil.copytree(ROOT / "docs", target / "docs")
    readme = (ROOT / "packaging/linux/README.md").read_text(encoding="utf-8-sig")
    for key, value in {"VERSION": version, "RID": rid, "STEM": target.name}.items():
        readme = readme.replace(f"@{key}@", value)
    (target / "README.md").write_text(readme, encoding="utf-8", newline="\n")
    for path in target.rglob("*"):
        if path.suffix in (".sh", ".service"):
            contents = path.read_bytes().replace(b"\r\n", b"\n")
            path.write_bytes(contents)


def create_archive(staged, archive):
    # Write Unix modes explicitly: the build host can be Windows.
    with archive.open("wb") as output:
        with gzip.GzipFile(filename="", mode="wb", fileobj=output, mtime=0) as compressed:
            with tarfile.open(fileobj=compressed, mode="w", format=tarfile.PAX_FORMAT) as tar:
                for path in [staged, *sorted(staged.rglob("*"))]:
                    if path.is_symlink():
                        raise ValueError(f"Symlinks are not allowed in the staged release: {path}")
                    info = tar.gettarinfo(str(path), arcname=path.relative_to(staged.parent).as_posix())
                    info.uid = info.gid = info.mtime = 0
                    info.uname = info.gname = ""
                    info.mode = 0o755 if path.is_dir() or path.name in ("cherbot", "install.sh") else 0o644
                    if path.is_file():
                        with path.open("rb") as source:
                            tar.addfile(info, source)
                    else:
                        tar.addfile(info)


def create_installer(archive, installer, version, rid, stem):
    header = (ROOT / "packaging/linux/installer-header.sh").read_text(encoding="utf-8-sig")
    for key, value in {"VERSION": version, "RID": rid, "STEM": stem, "PAYLOAD_SHA256": digest(archive)}.items():
        header = header.replace(f"@{key}@", value)
    header = header.replace("\r\n", "\n").rstrip("\n") + "\n"
    header = header.replace("@PAYLOAD_LINE@", str(header.count("\n") + 1))
    with installer.open("wb") as output, archive.open("rb") as payload:
        output.write(header.encode("utf-8"))
        shutil.copyfileobj(payload, output)
    installer.chmod(0o755)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--rid", choices=MACHINES, default="linux-x64")
    parser.add_argument("--output", type=Path, default=ROOT / "artifacts/linux-release")
    parser.add_argument("--input", type=Path, help="Use an existing self-contained single-file publish directory instead of building")
    args = parser.parse_args()
    version = ET.parse(ROOT / "Directory.Build.props").findtext(".//Version")
    if not version or not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+(?:[-+][A-Za-z0-9.-]+)?", version):
        raise ValueError("Directory.Build.props must contain a valid release version")
    args.output = args.output.resolve()
    args.output.mkdir(parents=True, exist_ok=True)
    stem = f"cherbot-{version}-{args.rid}"
    with tempfile.TemporaryDirectory(prefix=".cherbot-package-", dir=args.output) as work:
        work = Path(work)
        binary_dir = args.input.resolve() if args.input else work / "publish"
        if not args.input:
            subprocess.run([
                "dotnet", "publish", str(ROOT / "src/CherBot.Host/CherBot.Host.csproj"),
                "-c", "Release", "-r", args.rid, "-o", str(binary_dir), "--self-contained", "true",
                "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true",
                "-p:IncludeAllContentForSelfExtract=true", "-p:DebugType=none", "-m:1",
            ], cwd=ROOT, check=True)
        staged = work / stem
        stage_release(binary_dir, staged, version, args.rid)
        archive = args.output / f"{stem}.tar.gz"
        installer = args.output / f"{stem}.run"
        create_archive(staged, archive)
        create_installer(archive, installer, version, args.rid, stem)
        checksums = args.output / f"{stem}.sha256"
        checksums.write_text("".join(f"{digest(p)}  {p.name}\n" for p in (archive, installer)), encoding="ascii", newline="\n")
    for artifact in (archive, installer, checksums):
        print(f"Created {artifact} ({artifact.stat().st_size:,} bytes)")


if __name__ == "__main__":
    main()
