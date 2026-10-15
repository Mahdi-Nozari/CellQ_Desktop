# Data Logs implementation

The [pressure graph popup](LOG_GRAPHS.md) opens downloaded logs and other local CellQ CSVs. Graph viewing is local-only and never enters maintenance. Use **View graph** or **Open log file…** in Data Logs. No demo CSV is bundled with the app.

This feature uses `references/USB_PROTOCOL.md` v1 unchanged. The UI retains the compact shell and plain-language labels. The only added device commands are ENTER, LIST, GET, NEXT, HASH, conditional DELETE, CANCEL and EXIT. There are no firmware-upload, test, valve, preset or calibration controls.

## Explicit workflow and shared ownership

Connecting and page navigation still only observe the device. Browse device logs, Download / Resume and confirmed deletion explicitly start log access. The connection service holds an exclusive operation lease for the entire workflow, including file verification and cleanup; HELLO cannot interleave with transfer requests. One transport and reader continue to demultiplex debug, CQ1 and binary traffic. DTR/RTS stay disabled at 250000 baud, 8-N-1.

A fresh HELLO checks mode before entry. TEST prevents ENTER locally; the firmware makes the final decision on raced test starts and Manual valve activity. A nonmatching ENTER reply is refused without inventing error-code meanings. Manual regulator outputs are idled on accepted entry, as disclosed in the UI. Once ENTER has been transmitted, a user cancellation waits for its bounded acknowledgement so an accepted transition can safely be exited.

An existing MAINT session is adopted only on an explicit log operation, with CANCEL to recover an old list/transfer/check. An explicit Return to normal action requires a fresh MAINT observation and never sends ENTER from Idle. At the end of the full workflow, cleanup confirms CANCEL, then EXIT, then refreshes HELLO. EXIT is not sent per chunk or during connection/diagnostics. User disconnect and app disposal cancel an owned workflow and wait for cleanup before closing the port. Observation-only disconnects do not end an existing session.

On USB loss or a missing cleanup reply, the app retains incomplete or already verified local data, reports uncertainty and asks for reconnect/resume/Return to normal. It cannot claim Form 0 without EXIT acknowledgement. Closing warns when an owned session may still remain.

## Listing and files

LIST reads `/DATA`, then its dated or NO_RTC directories. BEGIN/ITEM/END sequencing, unsigned sizes, unique item names and generated DATA path restrictions are validated. Malformed/traversal entries and `/PRESETS` are rejected. An empty root or dated directory is a successful empty list. The UI shows folder dates readably and NO_RTC as No date; searches also accept the original folder name. Listings are snapshots and are cleared on disconnection; no automatic listing or polling enters maintenance.

### Preset names and local filenames

On the explicit Browse device logs action, the app also reads each nonempty log's CSV header using GET from offset 0 and NEXT, within the same maintenance lease as LIST. Each frame's CRC16, expected offset and listed size are checked. Every preview is closed with an acknowledged CANCEL before another GET starts; the batch exits to Form 0 once finished. A preview that reaches EOF also checks its file size, byte count and stream CRC32. Header integrity/timeouts permit bounded CANCEL/restart recovery. Stopping or unplugging retains the established maintenance recovery behavior and changes no device file. No header scan runs when connecting, changing pages or finding COM ports.

Preset metadata comes from CSV record 7, column 2, with `CELLQ_DATA` in the first record and `PRESET_NAME` in record 7. The bounded parser supports UTF-8/BOM, LF/CRLF, quoted commas, escaped quotes and quoted newlines. It stops after the seventh record or 8 KiB; empty, old, malformed or oversized headers display an em dash and remain downloadable. These names are header observations, not verified completed copies: the Saved copy column stays unverified until the full transfer checks pass. The list can search by preset, original filename or folder.

The save dialog suggests `<preset>_<original folder>_<original filename>`, for example `Heart Test_20261011_log_001.csv`. Spaces and Unicode are preserved; Windows-invalid filename characters and controls are replaced, unusable prefixes fall back to the original format, and the prefix is bounded to 80 UTF-16 units without cutting a surrogate pair. The user can still choose a destination/name. Remote commands always use the original device path; the downloaded CSV bytes are never rewritten. Existing files/receipts remain protected from overwrite, and resume uses the same selected target and its existing progress sidecars.

Before finalizing, the preset is read again from the fully checked local bytes. A known preset that changed since browsing prevents finalization under the stale suggestion and leaves resumable progress. Verification receipts now optionally contain the preset name; old receipts are still accepted. Use saved copy verifies bytes and re-reads their header rather than trusting editable preset metadata in the receipt. Older completed files can be selected without renaming them.

Each Download / Resume operates on one selected file and user-chosen destination. Existing completed files and receipts are refused rather than overwritten. The progress files are `<destination>.part` and `.part.json`. Their versioned record contains remote path, remote size/CRC32, checked prefix length/CRC32 and a local SHA-256 seal. A fresh device HASH is checked before opening/resuming the temporary copy. Resume rereads the prefix and validates both seals; only then may an uncheckpointed crash tail be truncated. Orphan or changed progress is preserved and refused.

Checkpoint file data is flushed to disk before atomic replacement of the owned progress record, at least every 4 KiB and on graceful interruption. SHA-256 state is accumulated incrementally. Unique metadata scratch files are cleaned only by their creator. After an abrupt process/power loss, resume uses the last durable checkpoint; it never trusts the raw temporary file length.

## Transfer verification

- The byte-stream parser validates A5/5A marker, type 01, payload length 1–48, unsigned little-endian offset and CRC16-CCITT-FALSE. Newlines, D/CQ1-looking text and markers inside payloads remain data.
- GET must echo the exact checked offset and expected full size. Every frame must start at the next checked offset and stay within the expected file size; bytes are appended only after frame CRC and position validation.
- EOF must match the expected file size, total checked byte count and CRC32 of this GET's transmitted segment. Stream CRC starts afresh on each GET, including a resumed/retried GET.
- CRC, position, GET echo, EOF count/checksum and NEXT timeout failures trigger CANCEL, then GET at the last checked position, with bounded retry. Persistent failures retain the incomplete copy and safely end maintenance when possible.
- A final device HASH must match the initial remote size/CRC32 and the complete local CRC32. The exclusive local handle is used to reread disk bytes and verify size, CRC32 and local SHA-256 before final rename. Rename never overwrites another file. The completed file is rechecked before its `.verified.json` receipt is written; only then is the consumed progress record removed.

Device HASH is the protocol's whole-file CRC32, not a remote SHA-256 or device identity. The SHA-256 seal is local evidence used to detect changed saved copies. Receipts can be reopened through Use saved copy, which validates the selected log/path/size and rereads bytes without any serial command; paths embedded in receipts never determine which local file is opened.

## Conditional deletion

Deletion is unavailable until a matching local copy is verified. The local file is rehashed and held open with sharing that prevents writes/removal throughout confirmation and the device transaction. The prompt identifies the exact remote path and retained local path, explains irreversibility on the device, and defaults to No. Declining sends no maintenance or delete commands.

After confirmation, local bytes are rechecked, an explicit maintenance workflow obtains a fresh HASH, and matching size/CRC32 are required before `CQ1,DELETE,<path>,<size>,<uppercase-crc32>`. Both CHECKING and DELETE acknowledgements are required. Firmware still enforces the condition if the file changes between HASH and DELETE. Only an acknowledged deletion removes the device row; local files and receipts are retained. Directories are never deletion candidates.

## Host policies

The protocol does not supply numeric timeout/retry settings. Defaults are HELLO 3 s, command/cleanup 10 s, NEXT 3 s, HASH/DELETE final verification 2 minutes, and 3 additional transfer attempts. `LogProtocolOptions` makes those policies injectable in tests. Idle debug traffic does not reset response deadlines. CANCEL drains late old results until its own acknowledgement or a bounded deadline; failed recovery closes the connection instead of pretending synchronization.

All transmitted commands are ASCII uppercase verbs with LF and at most 79 bytes. CRC32 response fields accept 1–8 uppercase hexadecimal digits because v1 does not require zero padding; DELETE emits eight uppercase digits. The received-line bound is 8,192 bytes, independent of the protocol's transmitted-command limit. Malformed binary/ASCII traffic and unexpected responses remain visible in technical diagnostics.

## Validation and limits

Latest preset-metadata validation: **build/publish passed with zero warnings/errors; 2,100 protocol/core assertions across 135 cases and 247 WPF/UI/package checks passed**. Nineteen added core cases cover all byte boundaries, UTF-8/BOM and quoted CSV fields, absent/malformed/bounded headers, safe prefixed filenames, batch preview cleanup, CRC/offset/GET echo/EOF/timeout recovery, retry exhaustion, cancellation, disconnect/reconnect, TEST/Manual denial, unchanged CSV bytes, original-path conditional deletion, older/tampered metadata receipts, prefixed download resume, changed presets and existing-target refusal. The UI checks cover names before downloading, preset search, actual save-dialog suggestions, unverified-preview state and the new column in both themes. Populated minimum-size previews: [Light](../artifacts/ui-checks/DataLogs-presets-Light-simulator.png), [Dark](../artifacts/ui-checks/DataLogs-presets-Dark-simulator.png); both are explicitly simulated. The final portable EXE passed its standalone check. Development did not open a physical serial port; confirm names/default filenames and download with a real CellQ CSV.

Final validation on 2026-10-09: **Release build passed with zero warnings/errors; 1,200 protocol assertions across 52 cases and 104 WPF UI checks passed.** No physical serial ports were opened. Actual WPF previews were reviewed at the normal and minimum window sizes; the populated example is explicitly marked as a simulator.

Run `build.ps1` for locked restore, Release build and both executable harnesses. Automated tests use a fake serial/card model, never physical ports, and write only beneath `artifacts/`. The simulator shares card/mode state across transport reconnects, fragments responses, mixes debug and binary bytes, and injects integrity faults and missing replies.

Coverage includes root/subfolder and empty-card listings; empty logs; exact download bytes; durable interruption, reconnect and resumed-segment CRC; cancellation during ENTER/download; disconnect and app disposal; frame CRC, offset, GET echo, EOF size/stream CRC and NEXT timeout recovery; retry exhaustion; whole-file hash mismatch; changed remote/local progress and crash tails; TEST/Manual denial; stale Idle entry refusal; overwrite/orphan protection; denied/confirmed deletion; locked local copy; changed files and firmware conditional refusal; restored receipts; forbidden paths; failed EXIT and explicit recovery; serialized HELLO and command length.

The WPF harness executes browse/download/deletion/recovery commands against the injected simulator, checks empty/filter results, shared state, readable labels, original diagnostics, disabled unsafe actions and binding warnings/errors, and renders all pages plus minimum-size and raster-scale previews. Filled example images carry SIMULATED TEST DEVICE; disconnected screenshots do not imply hardware was present.

Real-device SD/card error responses, sustained throughput, physical line levels, ACK-paced display delays, USB removal/replug and conditional deletion still require a CellQ unit. The earlier user-supplied screenshots confirm basic real connection, not these new transfer workflows. Installer and clean-machine testing remain future work.
