# Package a Linux release

Run this from the repository root with the .NET SDK selected by `global.json` and Python 3.11+:

```bash
python packaging/package_linux.py
```

The builder publishes a fresh, self-contained, single-file Linux x64 engine. It adds the service installer, system/user units, configuration template, Web UI documentation, AI reference/prompt, and a Linux quick-start README. It emits these versioned files into `artifacts/linux-release/`:

- `cherbot-<version>-linux-x64.tar.gz`: one top-level release directory, with Linux executable permissions.
- `cherbot-<version>-linux-x64.run`: an offline Bash installer containing the same archive and checking its SHA-256 before extraction.
- `cherbot-<version>-linux-x64.sha256`: checksums for the archive and installer.

The `.NET` version and CherBot version come from the repository's SDK/project configuration. No account data is collected. The archive contains fresh program files and repository documentation, not a working data folder.

## Installer modes

For version 0.3.0:

```bash
# User service: ~/cherbot, data in ~/.local/share/cherbot
bash cherbot-0.3.0-linux-x64.run --user

# System service: /opt/cherbot, data in /var/lib/cherbot
sudo bash cherbot-0.3.0-linux-x64.run

# Extract only; the destination must not already exist
bash cherbot-0.3.0-linux-x64.run --extract ./unpacked
```

Installation requires Linux of the matching CPU architecture and systemd. Extraction supports console-only deployments without systemd. Native system-library requirements are described in the generated release's README. No Wine or separate .NET installation is needed.

Reinstalling updates program files and documentation while retaining data/configuration. Stop the engine and back up the entire data folder before upgrading. Service installation is explicit; the builder never installs a service on the build host.

## Options and checks

```bash
python packaging/package_linux.py --rid linux-arm64
python packaging/package_linux.py --output artifacts/my-release

# Repackage an already-published self-contained single-file engine:
python packaging/package_linux.py --input artifacts/cherbot-linux-x64

python -m unittest discover -s tests/packaging -p 'test_*.py' -v
bash tests/packaging/install.test.sh

# On native Linux x64, verify checksums, extraction, startup, health and UI assets:
bash tests/packaging/smoke-linux-release.sh artifacts/linux-release
```

`--input` copies only `cherbot` and `appsettings.json` from that directory; it takes service files and documentation from this checkout. It validates the ELF architecture. The input must already be a self-contained single-file publish, such as the output of `publish.sh` or `publish.ps1`.

With both release files downloaded, verify them in their download directory:

```bash
sha256sum -c cherbot-0.3.0-linux-x64.sha256
```

If only the installer was downloaded, verify just its entry:

```bash
grep '  cherbot-0.3.0-linux-x64.run$' cherbot-0.3.0-linux-x64.sha256 | sha256sum -c -
```

`tests/packaging/test_linux_package.py` tests file modes, private-file exclusion, architecture mismatch, exact installer payload bytes, extraction, and checksum failure. Bash tests run natively on Linux in CI; local Windows maintainers can set `CHERBOT_TEST_BASH` to their Git Bash executable for the extraction tests.
