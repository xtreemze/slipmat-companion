# Music migration recovery retrospective

## Observed incident

A local Music library preparation captured 66,553 files and 941,691,053,814 bytes. The job changed from copying to failed/copy-failed after roughly ten hours. The existing local library stayed active; partial remote objects remained. Attempting a new preparation returned HTTP 409 migration-destination-not-empty. No cutover or cleanup was performed.

## Findings and limits

- Start correctly protects an unowned nonempty destination, but there was no explicit resume transition for the owned failed job. Restart recovery only handled interrupted active phases, not terminal failures.
- The worker discarded command exit information and exposed only a generic failure. Migration commands used --stats=0; phase alone could not establish progress. The initiating transfer error is still unknown. The configured command timeout was seven days, so the ten-hour duration is not evidence of that timeout.
- The deployed page lagged source main: source inventory/cost presentation already existed upstream but was absent on the installed page. Main, candidate, installed binary and browser cache must be identified separately.
- The agent began work before establishing the approximately 942 GB scale and recovery capability. Repeated unchanged polling and messages did not provide new evidence. Broad server-log snapshots were unnecessary and could include authentication tokens; future evidence must be filtered and redacted before output.

## Changes

- Explicit job-bound, elevated local-to-VFS resume reuses immutable copy and original inventory. Source/profile/library/competing-job changes fail closed. The empty-destination guard remains for new jobs.
- Numeric JSON statistics are projected into the existing durable job. No remote-control server, additional transfer authority, provider URLs or raw log text is introduced. Failures retain a safe classification and exit code; legacy unknown failures remain unknown.
- The workbench exposes resume, phase/observation freshness and per-command counters. Failed reads retain last-known jobs and label them stale. Action buttons prevent repeated requests while pending.
- Regression coverage exercises custody rejection, same-job reuse, copy/check ordering, numeric-only parsing and real process stderr observation. These tests do not certify a production Tele2 transfer or Linux FUSE cutover.

## Prepare the real retry

1. Keep the failed journal and partial destination; keep local Music active and the saved profile disabled. Do not delete, move, sync, purge, or manually change job phase.
2. Review and test the recovery candidate, then obtain release/install authorization. Preserve the installed plugin and configuration rollback package. Verify the actual DLL version and refreshed workbench after installation.
3. Select Resume on the existing failed Music job. If its profile, path or inventory has changed, stop at that guard and investigate; never force a new manifest over partial custody.
4. Confirm a fresh numeric observation and capture any sanitized exit code/failure classification. A queued/copying label alone is insufficient. Use paced checks and report state changes; schedule only when requested.
5. Require exact downloaded-content verification and ready-for-cutover. Verify the mount and library after explicit cutover; retain local rollback data. Finalize remains separately authorized and certification-gated.

## Host-target correction

Preparation verified Audio Gateway 0.5.2.8 installed and the server dashboard reporting Jellyfin 12.3. The old source/negotiation contract still pinned 12.1. Official NuGet Controller/Model 12.3.0 packages target net10.0, so the candidate pins both host references and its advertised target to 12.3.0; Slipmat's strict negotiation and canonical examples move together. No old-server fallback is added. The prior mirror-only track-event fixture change is numeric formatting (0 to 0.0); refreshing the reviewed upstream fixture snapshot does not add a semantic contract change. Historical 12.1 Docker/PoC evidence remains historical, not proof of 12.3 runtime behavior.

## Open evidence

The old command's exit code/provider error was not retained. Full upload/check, production installation, real mount/readback, cutover and rollback remain unverified for this candidate. The separate journaled unmount/handoff issue in #50/#139 is unchanged by this preparation-resume fix.

## Changed-source follow-through

After installation and restart, 0.5.2.9 was active and a cache-bypassed workbench exposed Resume. The original job was rejected with `migration-source-changed`; no transfer began. The operator then explicitly chose the current Music tree as the intended baseline, preserving the old record and all media.

This exposed the missing per-file v1 history and a separate metadata trap: actual rclone local tests show `copy --immutable` rejects timestamp drift even for identical content. The next candidate adds an explicit queued current-source recovery, downloaded-content verification of REMOTE as a subset of LOCAL, source/binding revalidation, and baseline revision history before normal copying. Forward copy keeps immutable protection and uses checksum comparison; matching-content timestamps stay untouched. Full two-way download verification is unchanged. Actual local rclone checks verified matching-content reuse and rejection/preservation of remote extras and content conflicts. This does not prove a production cloud transfer or establish the initiating old copy error.

The long review uses the existing durable queue and worker rather than a request-thread scan or a manually edited journal. UI error parsing covers native Fetch Responses as well as legacy response shapes, and tests exercise these shipped functions. Release and installation are separate from candidate validation.
