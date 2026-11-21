# NTFS custody driver feasibility (disposable VM)

This is feasibility evidence, not authorization or certification for a production cutover. The existing custody adapter remains fail-closed and does not fall back to a plain rename.

On 2026-11-10, `scripts/prove_ntfs3_custody_vm.sh` exercised the current `MaintenanceRunner/custody.py` on a newly formatted, separate loop image in the disposable `slipmat-vfs-test-20261108` Lima VM. Ubuntu kernel `7.0.0-34-generic`, package `linux-modules-7.0.0-34-generic 7.0.0-34.34`, loaded its in-tree `ntfs3` module. The mount accepted `uid=0,gid=0,dmask=022,fmask=133`. The original NTFS-3G fixture remained mounted and was not modified.

The new volume root initially appeared as root-owned mode `0777`, despite those mount masks; custody correctly rejected it. On a separate disposable image, setting only that image's volume root to `0755` allowed the following checks to pass:

- An **empty** existing destination was rejected with `destination-exists`, and both directories remained intact. An ordinary rename could replace an empty directory, making this a meaningful no-replace check.
- Forward and restore operations through `rename_directory` succeeded, including its directory `fsync`; source inode and test bytes were preserved.
- The mount root and source both appeared root-owned mode `0755`.

The script detached its own loop device after unmount. Its images and empty mountpoint directories remain in the VM for inspection. The initial attempted image and the subsequent successful image are disposable evidence, not backups. The test did not simulate power loss, validate all NTFS metadata or prove the production kernel, disk, permissions, or service behavior. A successful `fsync` call is not power-loss certification.

The exact Ubuntu kernel source tag for the tested package has the [NTFS3 rename flag check](https://git.launchpad.net/~ubuntu-kernel/ubuntu/+source/linux/+git/resolute/plain/fs/ntfs3/namei.c?h=Ubuntu-7.0.0-34.34) accepting `RENAME_NOREPLACE`. The [kernel NTFS3 documentation](https://docs.kernel.org/filesystems/ntfs3.html) describes its mount options. These sources do not establish the production host's driver support or its volume's state.

## Production decision and reversible preparation

A driver switch requires a separate human decision. Before deciding, establish the production kernel and NTFS3 module availability, current mount source and options, volume health, root/source ownership and permissions, active users, mount configuration and restart dependencies. Assess the effect of NTFS3 ownership and permission handling on Jellyfin and every other consumer. Preserve the source library, cloud data, failed job, baseline and existing mount configuration throughout.

If a switch is separately authorized, plan a maintenance window, stop and verify all consumers, record the exact original mount configuration, and establish a tested rollback to that configuration. Only then evaluate an NTFS3 mount with verified restrictive permissions and run a bounded, independently created no-replace probe on the actual volume. Recheck the entire custody and service barrier before any migration action. If any precondition or probe fails, stop and restore the original mount configuration; do not perform the cutover. Do not change permissions on production media merely because this disposable image needed them without assessing the access consequences.
