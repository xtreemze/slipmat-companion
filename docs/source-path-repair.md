# Source-path repair foundation

This is an unwired contract and ownership-guard candidate. It does not add an
admission endpoint, operator command, filesystem adapter, or production repair.
Do not edit a journal to manufacture an admitted repair. No source-path move is
authorized by a diagnostic report or a terminal custody receipt alone.

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

Tests exercise write-ahead ordering, preservation before moves, every synthetic
forward/restore journal-effect crash boundary, pending rollback outcomes, foreign
replacements, lost barriers, stale revisions, plan tampering and terminal proofs.
Integration tests cover ordinary-writer refusal, source-root reservation,
worker/executor exclusion and legacy serialization. The Linux runner harness
invokes the actual `startup` and `run` entry point with unknown repair state and
no VFS maintenance intent, requires the explicit owned-repair refusal before
host-spec access, and checks unchanged journal bytes.

These are model, ownership and entry-point tests. They do not prove real file
movement, source preservation copies, NTFS, service-control lifecycle, reboot,
catalog identity retention or production readiness.

Before any admission or effect producer can be enabled:

- Implement and review canonical admission, rollback request and completion,
  including bounded prior-attempt history and conflict-owner exclusion.
- Resolve the pre-existing `LoadAllAsync` behavior that omits corrupt/unreadable
  jobs: incomplete inventory cannot prove that a source is unowned. This candidate
  does not change that shared-store policy and must not be used to admit repairs.
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
