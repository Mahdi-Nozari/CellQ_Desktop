# Firmware Update

Firmware Update is implemented using the agreed v1 release policy. The user has reported a successful download and flash. No physical device was opened or flashed by the agent during development. The earlier empty-release observation is historical; the app checks live metadata when the user chooses Check releases and never treats a network/access failure as an empty list. At the user's request, the hardware questionnaire was removed; automatic package and uploader checks remain.

## Using the page

The page uses two rows: Installed firmware spans the top, with device connection and latest-release status. Equal panels below show Update progress on the left and Update file on the right. The release selector and **Selected update: [version]** are together with download/check status and the installation action. This explicitly separates the chosen release from the version currently installed on the device. Release date is in expandable Release notes; Technical details remains collapsed by default. The main controls fit at 1040 × 700 without scrolling.

1. Choose Check releases. This can run without a connected device and does not enter maintenance. Only stable published `vX.Y.Z` releases with the exact matching `cellq-mega2560-vX.Y.Z.hex` and `.sha256` pair from `Mahdi-Nozari/cellq_firmware` are offered. Drafts, prereleases and ambiguous/incomplete pairs are excluded.

   After a successful check, Installed firmware shows a comparison label and the latest eligible release version. An exact match shows **Latest version installed** in green; an older installed version shows **Update available: [version]**. A device ahead of published firmware, an unknown installed version, a disconnected device and an empty release list have distinct text without a successful latest-version claim. Versions are compared numerically against the newest eligible release, independent of the selected item. A new check hides the previous result immediately; failed checks cannot retain a stale success label. During installation/reconnection, the label says **Checking installed version…** and waits for the returning identity. This comparison uses the last reported device version and release metadata; downloading and checksum verification remain separate requirements before installation.
2. Select a release, expand Release notes, then Download update. The app checks the download automatically. The package goes to a private unique temporary directory under LocalAppData/CellQ/Firmware. Failed/cancelled downloads remove their own temporary files. Source archives and arbitrary local HEX files are never accepted. Rechecking releases or changing selection invalidates the previous package; cached downloads cannot authorize an update after restarting the app.
3. Connect your CellQ. The app checks its firmware version and status automatically; the user does not need to identify its internal hardware.
4. Choose Update firmware. The single confirmation asks whether to update from the installed version to the new version and reminds the user that the device restarts and USB/power must stay connected. It defaults to No. Reinstallation, downgrades and noncanonical installed versions are unavailable in v1. Tag `vX.Y.Z` maps to the expected HELLO version string `X.Y.Z`.
5. Keep USB and device power connected until the result. Maintenance entry can idle Manual pressure outputs. A running/paused test or Manual valve denial prevents upload; the app never stops the test. Firmware preparation, installation, readback, reconnect and version checking appear in the PC app. Device Form 8 cannot show bootloader progress during reset.

Installation stays disabled until a verified published package, connection and newer comparable version are available. A confirmation is required before each installation attempt. There is no physical-board checklist or authenticity warning. This simplifies the user interface without changing the fixed uploader target, protected application bounds, device-signature checks or release policy. HELLO does not identify internal board hardware; removing the questionnaire does not qualify additional hardware. Data/configuration preservation is not promised without release-specific evidence.

## Package checks

Both assets must come from the same published release over HTTPS with exact version-specific names. Release and asset identity, sizes, publication date and notes are rechecked before downloading; changed metadata requires checking releases again. Downloads reject unexpected sizes, unapproved redirects, oversize responses and truncated input.

The SHA-256 file is exactly one ASCII line: 64 lowercase hexadecimal characters, two spaces, the exact HEX filename, then LF. Reject CRLF, BOM, missing LF, extra lines, alternate filename, uppercase digest or incorrect spacing. Compute SHA-256 over the original HEX bytes without normalization. This is the approved HTTPS/repository integrity policy; it is not an independent publisher signature. The normal UI says Checked; the digest and filename remain in collapsed Technical details.

Intel HEX validation separately checks record length/checksum, supported record types, EOF, reset-vector presence, nonoverlapping data and application address ranges. The Mega2560 profile allows only the first 253952 flash bytes, reserving the final 8 KiB for the bootloader. No EEPROM, fuse, lock-bit or bootloader operations are requested. The image does not prove board identity. The package is checked again and held with write/delete sharing denied through confirmation and uploader completion.

## USB ownership and recovery

The existing connection service exclusively serializes the full workflow. A fresh HELLO is checked before and after confirmation. ENTER and FWPREP occur only in the confirmed installation workflow and require exact acknowledgements. Cancellation after transmission resolves the outstanding acknowledgement before safe cleanup; an ambiguous timeout closes the ordinary transport and does not launch the uploader.

The app closes its port completely after acknowledged FWPREP. Only then may the pinned bundled AVRDUDE 8.3 start, with `atmega2560`, `wiring`, 115200 baud and the user's exact selected COM port. Normal app/debug/reconnect settings remain 250000, 8-N-1, DTR/RTS off. The uploader intentionally resets the board. Its pinned manifest, executable and configuration are verified; no personal avrdude configuration, force-chip option or verification-skip option is used. Write and explicit readback verification are requested. ArgumentList avoids shell/path quoting errors.

The port lease remains active throughout uploader completion and reconnection. Other device operations wait; the UI disables competing connection commands. Before flashing, Cancel can cleanly EXIT accepted maintenance. Once the uploader starts, ordinary cancel/close actions cannot kill it. A three-minute warning is a soft timeout: the app warns and waits for the process to release the port rather than killing it or racing a reconnect. If it remains hung, contact support before explicitly terminating it externally. Forced OS termination/crashes can still interrupt a flash; automatic rollback is not claimed.

Reconnect retries the same COM port five times with one second between attempts, using the existing three-second HELLO timeout. The app never chooses another port automatically or retries an upload without fresh confirmation. Uploader success plus an exact expected HELLO version is required for Complete. Wrong version, missing device or failed readback remains a failure/unconfirmed result.

If a released uploader fails and the device can be contacted, inspect HELLO and EXIT an existing MAINT session safely. EXIT is also used to recover an old FWPREP before a new confirmed workflow. No SD commands are sent after FWPREP. If the device already returned to normal after reboot, do not send maintenance-only EXIT. The explicit Return to normal action uses HELLO/EXIT and also remains available on Data Logs. An unacknowledged cleanup never claims the normal screen was restored.

Host limits: release metadata request/reading 90 seconds, whole package download/check 3 minutes, preparation acknowledgement 10 seconds, API pagination up to 1000 releases, metadata response 2 MiB, HEX 8 MiB, checksum 512 bytes, visible uploader output approximately 24K characters with bounded pending messages. These are app policies, not invented protocol v1 constants. Unexpected response text is preserved for support rather than interpreting undocumented error codes.

## Validation and previews

Latest validation: 0 build warnings/errors; **1,349 assertions across 102 protocol/core cases**, including 50 firmware cases; **216 WPF checks**, including the later Settings and dark-theme heading checks. The full build script includes all previous connection, parser and Data Logs checks. Layout previews were first built separately while CellQ was running. After the user closed CellQ, the normal Release output used by `start.cmd` was rebuilt. Close CellQ before rebuilding with `build.ps1`.

Firmware tests cover strict checksum grammar; record corruption, overlaps, EOF/reset vector/protected ranges; exact asset discovery, drafts/prereleases/missing/duplicate/mismatched assets; empty release lists versus HTTP errors; temporary download size/hash/format failure and interruption/cancellation; release changes; unsafe redirects; package mutation and write locking; declined confirmation; same/older/unknown versions; TEST/Manual/raced-test refusal; missing uploader; fragmented/debug-interleaved preparation responses; refused/missing FWPREP; cancellation while ENTER/FWPREP awaits acknowledgement; USB loss; old MAINT recovery; uploader launch/write/readback/timeout failures; failed COM closure; disconnect/reconnect; wrong version; MAINT after upload; queued refresh/disconnect and cancellation during flashing; pinned bundle and safe process arguments. Uploads always use a fake process. The real native process runner was exercised only with `--help` and a failing display callback, with no device options.

WPF checks exercise disconnected release lookup, no published releases, package verification without maintenance, update availability without a hardware questionnaire, a plain confirmation with no hardware/checksum jargon, declined/accepted confirmations, disabled controls and truthful connection state during uploader handoff, version completion, explicit visible recovery, network failure, collapsed details, binding correctness, minimum-size progress visibility and 100/125/150/200% renders.

Latest-status checks cover visibility before/after release lookup, empty releases, disconnected/unknown/ahead-of-release device versions, available/current firmware, fresh lookup on an already-current device without downloading or sending serial commands, numeric ordering of 1.10.0 versus 1.9.0, selecting an older release, hiding results during rechecking, failed rechecks and version changes after installation/disconnection.

Layout checks cover equal side-by-side panels with allowance for physical-pixel rounding at Windows display scaling, grouping release selection with the selected-update version and installation action, and keeping the progress bar and installation button inside the minimum-size viewport. Preview renders cover 100/125/150/200% scaling.

Actual compiled WPF previews:

- [Initial page](../artifacts/ui-checks/FirmwareUpdate.png)
- [No release, simulated fixture](../artifacts/ui-checks/FirmwareUpdate-no-release-simulator.png)
- [Ready, simulated fixture](../artifacts/ui-checks/FirmwareUpdate-ready-simulator-100percent.png)
- [Minimum window, simulated fixture](../artifacts/ui-checks/FirmwareUpdate-ready-minimum-simulator.png)
- [Installing, simulated fixture](../artifacts/ui-checks/FirmwareUpdate-installing-simulator.png)
- [Complete, simulated fixture](../artifacts/ui-checks/FirmwareUpdate-complete-simulator.png)
- [Latest version installed after checking, simulated fixture](../artifacts/ui-checks/FirmwareUpdate-latest-installed-simulator.png)
- [Error, simulated fixture](../artifacts/ui-checks/FirmwareUpdate-error-simulator.png)

All populated firmware examples are explicitly labelled SIMULATED TEST DEVICE and do not represent published firmware or a physical upload.

The user's successful flash is a real-device report, not an independently observed development test. A separately authorized hardware session is still needed to characterize board/USB bridge/bootloader details, reset timing and display ACK delays, physical interruption recovery and any additional hardware qualification. The single-file portable distribution embeds the uploader/license/source and extracts a verified local tool cache lazily. Preparation, exclusive port handoff, readback and version checks remain unchanged. A clean-PC launch test remains required before wider distribution. See PORTABLE.md.
