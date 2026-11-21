# Source-path repair foundation

This draft contains the repair transaction, ownership guards and a Linux host
adapter exercised against disposable files and a real journal. Read-only
operator checks and explicitly requested offline execution are available.
Installation and end-to-end proof on the production host are pending.
Do not edit a journal to manufacture an admitted repair. No source-path move is
authorized by a diagnostic report or a terminal custody receipt alone.

## Read-only runner checks

`source-repair-startup ABSOLUTE_JOURNAL_DIRECTORY` reads the full journal
inventory under the existing writer lock. It refuses service startup while
any source repair still owns a job, including a job other than the original
VFS maintenance job. It also refuses unreadable inventory. It allows valid
terminal repair receipts without requiring a VFS promotion admission.

`source-repair-check ABSOLUTE_JOURNAL_DIRECTORY JOB_ID ABSOLUTE_SPEC_PATH
ABSOLUTE_APPROVAL_PATH` uses the stopped-service barrier to validate the
reviewed plan, exact job revision, bound directories, full source metadata,
namespace and selected content. The root-controlled approval JSON contains
`expectedJournalRevision` and `plan`. The check does not reserve an archive,
run the filesystem probe or change the journal.

## Offline execution

`source-repair-run JOURNAL JOB_ID SPEC APPROVAL` admits the reviewed failed
job and drives repair to a verified state. Omit `APPROVAL` to resume an
already admitted job. `source-repair-rollback JOURNAL JOB_ID SPEC` records
rollback and drives the original namespace verification. Use absolute paths
for artifacts and invoke the installed `Slipmat.MaintenanceRunner` apphost.

The commands require a root-controlled apphost, its managed assembly and
runtime metadata, and the exact persistent drop-in at
`/etc/systemd/system/jellyfin.service.d/90-slipmat-source-repair.conf`.
Its contents are `[Service]` followed by
`ExecStartPre=+ABSOLUTE_APPHOST source-repair-startup ABSOLUTE_JOURNAL`,
each on its own LF-terminated line. Before masking for a real run,
run `source-repair-check-hook ABSOLUTE_JOURNAL_DIRECTORY` while Jellyfin is
stopped and unmasked. This checks the effective privileged argv and requires
failure propagation. Systemd skips loading drop-ins for a masked unit.

Keep Jellyfin persistently masked throughout repair. The runner requires
the on-disk mask, the manager's masked load and file states, no pending
daemon reload, and no service processes. After a verified terminal result,
run `source-repair-check-hook` again while unmasked and stopped before starting
Jellyfin.
These commands perform the local source repair only. After a verified repair,
request archive-and-repair again through Companion. The first request creates
a new attempt and preserves the original pre-plan failure record and artifacts.
A subsequent retry resumes the new attempt. Once cloud recovery has been
admitted, the offline source rollback command refuses the job because the
cloud side may already depend on the repaired namespace. Cloud content
verification and VFS activation follow separately.

## Purpose and owner

A case-insensitive cloud destination cannot represent every case-distinct local
path. A reviewed repair may preserve the versions under distinct names before
normal cloud conflict recovery. File-only inventories are insufficient: include
all directories, empty directories, unique descendants of moved directories, and
related filenames such as a track, its lyrics and its `._` companion.

`CloudMigrationJob.SourcePathRepair` is the only durable owner of repair state.
The original blocked conflict session stays intact. An active, malformed or
unknown repair state reserves the job and blocks ordinary saves, recovery,
worker restoration and the runner's startup/run path. Ordinary saves may retain
an unchanged stable receipt bound to the enclosing job, source and destination,
but cannot introduce, replace or erase repair state. A foreign terminal receipt
still reserves the containing job.
The optional absent field is omitted from JSON, retaining legacy job bytes.

The new state is distinct from VFS maintenance promotion and rollback. Repair
does not enable a profile, change a mount, adopt a baseline, clear cloud conflict
history, or certify a library. A future explicit new conflict attempt must retain
the earlier attempt and its artifacts in bounded history and capture fresh
namespace and content evidence.

## Deterministic transaction

The typed schema-1 plan binds one job, library, root and mount identity, source
evidence, host specification, preservation manifest, expected repaired namespace
and ordered same-parent moves. Its compact typed JSON fingerprint covers order
and exact spelling. Moves cannot overlap, replace one another or escape their
parents. The contract permits at most 32 moves.

The pure decision function and bounded engine require a matching whole-job
revision plus fresh trusted lease, stopped-service startup barrier, binding,
archive and filesystem-capability observations. An API caller must never supply
those observations as authority.

1. Persist preservation intent before creating the immutable preservation copy.
2. Verify the complete preservation manifest and exact archive identity before
   journaling any move intent.
3. Persist one move intent, then request atomic no-replace movement. A successful
   adapter return is insufficient: observe the exact resulting source/target
   identities and persist the committed prefix before another effect.
4. Reconcile a pending move after interruption from its observed physical state.
   Foreign objects, both paths present, both missing, invalid prefix order, or
   lost exclusion block rather than guessing.
5. A rollback request first reconciles any pending forward move, then records
   restore intents in reverse prefix order. Preservation data is retained.
6. Verify the full resulting namespace and preserved content before recording a
   repaired or rolled-back receipt. Repeat read-only verification on replay.

The operations interface is a contract, not a second store. A future adapter must
use the canonical journal lease and compare-exchange the entire job revision,
revalidate every effect boundary, settle child processes on cancellation, and
flush durable journal and directory writes. It must never use plain rename as a
fallback, merge trees, delete data, or patch unrelated job state.

## Evidence and remaining gates

The next host primitives remain unwired. `CloudMigrationJournalLease.ReadAllAsync`
provides an exclusive-lease, bounded complete inventory: unusable job files,
missing/non-null custody fields, duplicate JSON properties and mismatched IDs
block the whole read. Optional legacy fields may remain absent. It returns raw
whole-job revisions, not proof of adversarial directory custody. The dashboard's
permissive `LoadAllAsync` behavior is unchanged.

`custody.rename_source_entry` supports one regular file or directory under an
exact parent/entry device, inode and mount identity. It requires root-controlled
parents and source entries, pins no-follow descriptors, checks the exact stored
spelling of each parent component and source entry, uses only no-replace rename
and flushes the parent directory. It has no new CLI dispatch. A flush
failure reports an uncertain committed outcome for journal reconciliation; it
never guesses a rollback. This primitive does not create or verify preservation
copies, establish service isolation or authorize a planned move.

The archive readback verifier takes the reviewed manifest bytes and their
plan-bound SHA-256 digest, plus pinned identities for the archive and its parent.
Schema 1 records the complete tree, including empty directories, with file
sizes, SHA-256 digests and aggregate counts. Readback checks exact stored names,
rejects links and special entries, and verifies every file after a complete
metadata pass. Its caller must establish exclusive archive custody.

The custody helper can reserve a new empty archive directory, then copy using
the reserved identity. A subsequent request revalidates completed files before
continuing. An unknown directory or incomplete file blocks resume without
overwriting it. The copy can select a subset of the source. Before it can handle
Music, the offline runner must supply the admitted repair plan. The adapter
derives the source root, pinned move identities, and manifest digest from that
plan. The helper requires the manifest to include every descendant of each
moved directory while allowing unrelated siblings outside those subtrees.
It checks the source again after copying. The transaction records the reservation
identity through whole-job compare-and-exchange before permitting copy. Verified
readback must match that identity before any move can be authorized. If a crash
occurs after directory creation but before that journal update, replay blocks
and leaves the unbound directory for inspection. The Linux preservation host connects the journaled intent and root-owned,
hash-pinned host specification to the archive helper. It checks the stopped
service before and after each helper call. The Linux move host observes both
exact names under each pinned parent, verifies the expected prefix and archive,
and applies only the move or restore index recorded in the current journal.
A case alias or foreign identity is not treated as absence. Before renaming,
the host proves occupied-target refusal and successful no-replace moves for
both files and directories. This uses a pinned scratch directory on the source
mount and outside the source and archive trees. The source-repair command
requires the persistent startup guard. The Linux transaction
adapter verifies terminal custody by checking the exact full namespace and
rehashing the preservation manifest selection at translated paths. Namespace
SHA-256 covers UTF-8 byte-sorted relative paths, excluding the root. Each record
is the byte `D` for a directory or `F` for a file, a NUL, the UTF-8 path and a
final NUL. Empty directories are included. The staged preservation archive
is read back separately.

Tests exercise write-ahead ordering, preservation before moves, every synthetic
forward/restore journal-effect crash boundary, pending rollback outcomes, foreign
replacements, lost barriers, stale revisions, plan tampering and terminal proofs.
Integration tests cover ordinary-writer refusal, source-root reservation,
worker/executor exclusion and legacy serialization. The Linux runner harness
invokes the actual `startup` and `run` entry point with unknown repair state and
no VFS maintenance intent, requires the explicit owned-repair refusal before
host-spec access, and checks unchanged journal bytes.

The primitive tests perform real file and populated/empty directory moves on
isolated Linux fixtures, preserve occupied targets and exercise uncertain fsync
outcomes. A disposable ext4 casefold reproduction first demonstrated the inode-only
source/parent alias bug, then refused both aliases after exact spelling checks;
its exact-spelling positive control still moved. That proof covers the ext4
fixture, not NTFS.

These remain model, ownership, entry-point and isolated primitive tests. They do
not prove a complete source-repair execution, preservation copies, NTFS,
service-control lifecycle, reboot, catalog identity retention or production readiness.

Before any admission or effect producer can be enabled:

- Implement and review canonical admission, rollback request and completion,
  including bounded prior-attempt history and conflict-owner exclusion.
- Admission must use the complete lease-scoped inventory rather than the
  permissive UI listing, validate the returned job addresses and bind physical
  root identities. An inventory read alone cannot prove that a source is unowned.
  No admission producer is enabled by these primitives.
- Install and prove a source-repair-aware persistent startup barrier and exact
  runner version. Old binaries can ignore a new JSON field; PID or on-disk plugin
  version alone does not prove active guard support.
- Integrate the corresponding guard into draft offline preparation work before
  combining it with this contract. Its existing ready-for-cutover admission does
  not apply to a failed pre-hash source-repair job.
- Prove root-controlled source parents, exact volume/mount and configuration
  custody, an independent verified preservation copy, no-replace/fsync support,
  process settlement, and crash/reboot recovery on disposable host fixtures.
  NTFS-3G capability rejection must remain a blocker.
- Rebuild full source evidence while writers are stopped. A scoped cloud-audio
  diagnostic can report unrelated metadata drift but cannot refresh this gate.
- Validate complete remote/provider resolution, audio byte selection and related
  filename handling. Preserve originals and block unsupported/unknown conflicts.
- Inventory and reconcile Jellyfin items, both user state and playlist/reference
  relationships before and after repair. A library ID remaining unchanged does
  not establish child item ID retention.

Normal archive-and-repair, immutable copy and downloaded-content verification
still follow a repaired source. VFS promotion and local cleanup remain separate,
explicitly gated operations.
