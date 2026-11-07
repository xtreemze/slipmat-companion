# Music VFS recovery: retained diagnostics and host target

Live readback found the original Music job still Failed with baseline-verification-failed and the original manifest intact. Its .11 diagnostic had started but hit the process watchdog with no retained samples. Jellyfin is now 12.4 while .11 declares 12.3. No copy, adoption, cutover or deletion was performed during this investigation.

A real subprocess test reproduced the loss of already emitted stdout/stderr on watchdog timeout. The runner now retains bounded partial output; incomplete trailing report lines are discarded. Diagnostic matching rows are removed before the capture limit so they cannot crowd out later differences. Rclone check did not honor the transfer-duration cutoff in an actual local experiment; the explicit three-minute process watchdog is the bound. Separate diagnostic statistics show observation time and check/error counters without reusing old transfer samples. Zero findings before timeout remains inconclusive, particularly during backend listing.

The list API projects allowed recovery actions from the same predicates as admission. A failed baseline check therefore does not display an original-baseline Resume button which the server will reject. The workbench keeps current-source review and diagnosis subject to canonical command guards.

The paired client/companion target moves strictly to Jellyfin 12.4, with package references, advertised target and canonical examples aligned. No compatibility fallback or weaker verification is introduced. The fixture snapshot names the actual published client candidate commit.

Verification: integrated companion tests ran with the pinned .NET SDK and official checksum-verified Jellyfin FFmpeg. The Edge fixture used shipped workbench functions with simulated backend/counters/paths; it confirmed absent unsupported Resume, pending disabled diagnosis and a retained partial report. These are implementation checks, not production conflict, mount, rollback or playback certification.

The separate storage handoff remains unresolved: existing online forward/rollback flows do not exclude a concurrently starting scan, and generic unmount refuses an attached library. Jellyfin 12.4's separate path remove/add APIs do not provide an atomic catalog-preserving transaction. A systemd maintenance proposal is prepared separately; no guard bypass or offline swap was implemented in this diagnostic release. All local/cloud media and the old baseline remain retained.
