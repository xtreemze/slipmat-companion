# Verified cloud conflict recovery

An interrupted local-to-VFS upload can leave cloud artwork and NFO metadata that
differs from the current local library. Ordinary rebaseline intentionally refuses
these conflicts. The elevated dashboard action **Archive originals and repair
metadata** provides an explicit recovery path without discarding those originals.

This action does not enable VFS, change a Jellyfin library path, stop Jellyfin,
remove local files, or authorize finalization. Recovered jobs require a separately
validated offline maintenance procedure for cutover; online cutover is blocked.

## Operator decision

Before submitting the action, confirm that the current local library is the
intended baseline and that only Companion writes to the configured cloud root.
Stop external sync tools and manual edits first. The process-wide command fence
excludes other Companion rclone commands; it cannot exclude external writers.
The request binds the selected job's existing manifest digest and the explicit
`companion-only` writer policy. Clients cannot submit inventories, archive paths,
hash evidence, or mutation receipts.

After updating Companion, verify that the Jellyfin Linux service actually
restarted; an installed-version label alone does not prove the running assembly
changed. Open a fresh settings tab if an already-open page still has the old
controls. When handing a confirmation to an operator, retain that browser tab
and read the canonical journal before submitting again. A queued attempt or
preflight progress confirms admission; do not duplicate the request because a
browser action timed out.

The source must remain available and unchanged during recovery. The profile must
remain disabled and the original library binding must still match. Authentication
must work in Jellyfin's service environment, with token refresh allowed to persist
to its actual rclone configuration. A successful interactive-user rclone command
alone does not establish service authentication.

The provider absence probes currently require the tested rclone **1.74.4**
command contract and a typed case-insensitive capability. They check file and
directory resolution separately: a successful lookup of an otherwise unmatched
local path blocks recovery as an unproven provider alias. Permission, transport,
malformed-output and unrecognized missing-result errors never count as absence.
These metadata requests add work per unmatched local file and directory. A
read-only existing-file/random-missing-path self-check runs before payloads;
an initially empty remote cannot supply the positive file check and is rejected.
This feature does not establish a general Unicode equivalence rule or rename
unknown aliases. Source and namespace checks run again on resume and before the
first active-library mutation.

## Content and archive rules

1. Inventory and validate the complete local and remote namespaces before reading
   payloads. For a verified case-insensitive provider, a unique match that differs
   only in ASCII letter case retains both exact path spellings: local reads use
   the local path, and cloud operations use the observed remote path. No rename
   is implied. Same-side aliases, file/directory collisions and unproven Unicode
   equivalences block recovery. Then compute SHA-256 content hashes on both sides.
   Remote preflight downloads content for hashing, grouped by directory; matching
   file sizes or provider hash presence are not equality evidence.
2. Fail the whole plan before archive or mutation if an overlapping audio file or
   an unsupported, ambiguous, unsafe, or malformed entry conflicts. Supported
   replacements are byte-inspected NFO XML, JPEG, and PNG. Remote-only removal is
   limited to inspected JPEG/PNG artwork. Structural inspection is not a full
   image decoder or a claim of visual equivalence.
3. Copy **every** conflicting remote original into the server-generated sibling
   archive outside the active library. Verify every archived object's content
   before permitting any active-library mutation. Archive writes are immutable.
4. Replace approved metadata/artwork from verified local content, or remove the
   approved remote-only artwork from the active root. Recheck affected objects
   and retain their archived originals. No broad sync, purge, hard delete, audio
   overwrite, or source-file modification is part of this transaction.
5. Queue the existing current-source rebaseline, additive copy and downloaded
   content verification. Local-only files, including audio, are handled by that
   canonical migration path. Only successful verification can reach
   `ready-for-cutover`.

Content preflight may read the full library from both local storage and cloud.
Normal verification afterward adds another cloud content-read pass. Budget time,
provider traffic and private cache space accordingly. Current bounds are 100,000
namespace candidates, 10,000 conflicts, 2 MiB per listing/hash response, a 16 GiB
conflict payload cache plus a 512 MiB free-space reserve, and bounded per-command
timeouts. Exceeding a bound blocks recovery rather than truncating evidence.

## Interrupted recovery

The existing migration journal remains the sole job owner. It references immutable
SHA-bound plan/inventory sidecars and records compact ordered archive/mutation
intent and receipt prefixes. Recovery revalidates the source, profile, archive,
active objects and journal binding before continuing. It does not trust stale
client proofs or assume an interrupted command succeeded. Cancellation and errors
retain the archive, source, private cache, job history and blocked recovery state.
Use the same dashboard action to retry a blocked attempt after resolving its
reported cause. Do not manually edit its journal or delete sidecars to force it
forward. Private recovery artifacts may contain media paths and must not be
published as diagnostics.

Progress counters are observations, never verification authority. After repair
completes, inspect the migration's normal copy/check status as well. A completed
repair by itself does not establish a verified or mounted cloud library.
Cloud listing reports the number of files observed while its total is still
unknown. It does not invent a percentage or use the safety limit as a total.
Full local hashing follows namespace validation. Cloud-hashing progress advances
after each complete directory command. Quiet counters alone do not establish a
hang. A retry clears the previous attempt's progress observations before starting.

## Validation boundary

Tests cover deterministic planning, unsupported conflicts, process fencing,
source and journal ownership, crash/retry boundaries, archive and mutation
receipts, native local-file handling, real rclone against disposable local
remotes, and worker handoff to canonical copy/download verification. These tests
do not certify a particular cloud provider, installed host, NTFS mount policy,
offline cutover, library identity, playback, or reboot recovery. Those acceptance
gates remain necessary on the target host before promotion.
