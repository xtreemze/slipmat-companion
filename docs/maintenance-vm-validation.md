# Maintenance runner disposable VM validation

This harness is for an isolated Linux systemd VM with **no real Jellyfin installation or production data**. It creates a new local rclone remote, synthetic media, XML configuration, and a separate migration journal under `/mnt/slipmat-maintenance-fixture-*`. It retains the fixture and journal for inspection. The test helper creates its admitted job through `CloudMigrationJournal` and reads its canonical phase after each transition. Job creation is test scaffolding, not a supported production admission interface. Rollback uses the runner command.

Build the runner and fixture helper from the same source tree for the VM architecture, for example:

```sh
dotnet publish MaintenanceRunner/Slipmat.MaintenanceRunner.csproj -c Release -r linux-arm64 --self-contained true -o /tmp/runner
dotnet publish scripts/maintenance-vm-fixture/Fixture.csproj -c Release -r linux-arm64 --self-contained true -o /tmp/fixture
```

Transfer those outputs, `MaintenanceRunner/custody.py`, and `scripts/prove_maintenance_runner_vm.sh` to trusted root-controlled paths in the disposable VM. Install a verified rclone binary supporting `listremotes --long --json`, Python at `/usr/bin/python3.14`, FUSE, systemd, and `setpriv`. The test uses UID and GID 65534 for the service read. Its paths and files must be root-controlled; the fixture path must be new. The existing `/etc/systemd/system/jellyfin.service` must be a persistent `/dev/null` mask, and no installed Jellyfin unit may exist. The script temporarily substitutes a runtime stand-in to test `ExecStartPre` and restores the mask on exit. Never run it on a machine where that service or its mask belongs to a real installation.

```sh
sudo sh /trusted/prove_maintenance_runner_vm.sh \
  /mnt/slipmat-maintenance-fixture-example \
  /trusted/runner/Slipmat.MaintenanceRunner \
  /trusted/fixture/Fixture /trusted/rclone /trusted/custody.py
```

The script creates a root-owned 32 MiB tmpfs cache on a different device from the source, then checks preparation, promotion, a managed FUSE mount and service-user read, a read-only startup guard with unchanged journal hash, and rollback restoring the synthetic local file. After a successful rollback it verifies the exact cache mount and unmounts it. On failure, preserve the printed fixture and inspect its journal, mount, unit, and both copies; do not manually edit a production journal or infer that an interrupted job is safe to resume. This single happy-path fixture does not prove crash recovery, real Jellyfin integration, target storage compatibility, or production release readiness.

## Additional startup-barrier regression cases (candidate, not VM-certified)

The updated `scripts/prove_maintenance_runner_vm.sh` checks the real `ExecStartPre` service result and exact journal bytes in three additional disposable-systemd states:

1. A journal at `Prepared` MUST reject service startup, without modifying the journal, before any source move or mount.
2. After a successful promotion, a deliberately changed plugin configuration MUST reject startup. The fixture restores the exact promoted configuration and then requires a successful startup.
3. After the runner's completed rollback, the restored local library MUST allow startup without changing journal bytes.

The fixture uses synthetic files only. Every startup attempt temporarily replaces a pre-verified systemd mask and restores it on exit. These cases extend the single clean-run fixture. The original November 10 execution **did not run these new assertions**; the updated script requires a fresh disposable-VM execution. CI syntax and build checks, when available, do not substitute for that execution, real Jellyfin 12.4, injected crashes/reboots, or production NTFS custody certification.

## Recorded VM result, 2026-11-10

On a disposable Ubuntu 26.04 ARM64 VM with systemd 259, the current working-tree candidate built self-contained with .NET SDK 10.0.401. Runner DLL SHA-256 was `ef8cbe8d5e103847ec364e306783b658171c7def48697fb32b3672e9c471a678`; custody helper SHA-256 was `03dfd452b21b7ba0f59669ec61b5c9900e022f54f87a585a3cf9ba4c8482e80b`. Official rclone 1.74.4's Linux ARM64 archive was checked against its published SHA-256. The harness completed on job `a08f8fbcfb624810aed171124fef0243`: prepare and promote reached stable with canonical `ready-to-finalize` and the exact backup path retained, the service user read a read-only `fuse.rclone` mount, startup preserved journal bytes, and the runner rollback reached canonical `rolled-back` with a null backup path, identical local and remote fixture bytes, and no remaining mount. The cache was a separate root-owned tmpfs, verified and unmounted after rollback. Fresh fixtures with a cache symlink and a bind mount to the source were both rejected before intent, with source files intact. The stand-in was restored to masked/inactive and the baseline AppArmor profile was used. No real Jellyfin server was installed or tested.

An earlier candidate, before the separate-cache guard was added, rejected a separate fresh fixture using Ubuntu's rclone 1.60.1-DEV during preparation with no maintenance intent and its source intact. That version rejects `listremotes --long --json`; the runner's public error is generic. The test retains the fixture and preparation artifacts.
