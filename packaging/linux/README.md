# CherBot @VERSION@ — @RID@

Native Linux stream automation, with an embedded Web UI, CLI, and Windows Remote client support. No Wine or separately installed .NET SDK/runtime is required.

## Requirements

- A glibc-based Linux distribution compatible with .NET 8. This build is for **@RID@**; Alpine/musl needs a different build.
- Bash, tar/gzip, and standard core utilities. Service installation requires systemd; a user service also needs a running user service manager.
- Native system libraries used by .NET, including ICU, OpenSSL, and zlib. Minimal distributions may need those packages installed. Package names vary by distribution; see [Microsoft's Linux installation guidance](https://learn.microsoft.com/en-us/dotnet/core/install/linux).
- OBS and Twitch accounts are configured after installation. They do not need to be present to start the engine.

## Option 1: offline installer

Download `@STEM@.run` and `@STEM@.sha256` into the same directory. To verify the installer alone:

```bash
grep '  @STEM@.run$' @STEM@.sha256 | sha256sum -c -
bash @STEM@.run --user
```

For a system service, use `sudo bash @STEM@.run` instead. `--help` prints the available modes. The installer verifies its embedded archive before extraction and makes no network requests.

## Option 2: release archive

If both release files are downloaded, verify them with `sha256sum -c @STEM@.sha256`.

```bash
tar -xzf @STEM@.tar.gz
cd @STEM@
bash ./install.sh --user
```

Use `sudo bash ./install.sh` for a system service. Executable permissions are preserved in the archive, including when it was built on Windows.

## Console use, without a service

Extract the archive, or use the standalone installer's extraction mode:

```bash
bash @STEM@.run --extract ./cherbot-unpacked
cd cherbot-unpacked/@STEM@
./cherbot --data-dir ./data
```

The extraction destination must not already exist. Console mode works without systemd. Open `http://127.0.0.1:8091/` on the engine machine. In another terminal, run `./cherbot token --data-dir ./data` to obtain the UI token.

## Installed paths and management

| | User service | System service |
| --- | --- | --- |
| Program and documentation | `~/cherbot` | `/opt/cherbot` |
| Data and configuration | `~/.local/share/cherbot` | `/var/lib/cherbot` |
| Restart | `systemctl --user restart cherbot` | `sudo systemctl restart cherbot` |
| Logs | `journalctl --user -u cherbot -f` | `sudo journalctl -u cherbot -f` |

The installer prints the token command for the selected mode. For a user service, use `~/cherbot/cherbot token --data-dir ~/.local/share/cherbot`. To keep it running after logout, enable lingering with `loginctl enable-linger "$USER"`.

Edit `cherbot.json`, set your Twitch application's `CherBot:Twitch:ClientId`, and restart. Link your accounts and add OBS on **Config** in the Web UI. For a remote browser or the Windows app, bind `CherBot:Api:Address` to a trusted LAN/VPN address and connect to `http://<that-address>:8091` with the API token.

Re-running the installer updates the program and restarts its service; existing configuration and database are kept. Stop the service and back up the entire data folder, including `keys/`, before upgrading. Run only one engine per data folder. To roll back, stop the service and install the previous release; restore its matching backup if a database migration prevents using the older version.

The archive contains `README-install.md`, the operating/configuration guides in `docs/`, and `docs/cherbot-ai-reference.txt` plus the reusable AI prompt. The same documentation is copied beside the installed program.
