# Firmware Update page plan

Planning draft only. No firmware download or upload implementation is authorized by this document. No device upload during development without the user's explicit permission.

Implementation follow-up: the user authorized coding after supplying the v1 configuration below. The feature is now implemented; see [current behavior and validation](FIRMWARE_UPDATE.md). Stable-only releases, no downgrades/reinstallation, and tag `vX.Y.Z` to HELLO version `X.Y.Z` are the implemented v1 defaults. The later anonymous GitHub API check on 2026-10-10 succeeded with an empty release list. Earlier unavailable-network observations below are planning history. No physical device upload was performed.

## Configuration supplied by the user

Later UI decision: after reporting a successful real update, the user requested removing the physical-board/authenticity questionnaire and microcontroller wording. The current implementation uses one plain confirmation; automatic package, application-range and uploader signature checks remain. Earlier board-confirmation requirements below are planning history.

The following v1 rules were supplied by the user on 2026-10-10. They replace the draft's open repository, asset, target and verification questions.

| Input | Agreed rule |
| --- | --- |
| GitHub repository | [Mahdi-Nozari/cellq_firmware](https://github.com/Mahdi-Nozari/cellq_firmware). Use published GitHub Releases over HTTPS. No embedded credentials. Public/private access has not been independently established. |
| Release tag | `vX.Y.Z`. |
| Required assets | Exactly one asset named `cellq-mega2560-vX.Y.Z.hex` and exactly one named `cellq-mega2560-vX.Y.Z.sha256` in the same version-matched release. Unrelated assets do not become alternative firmware candidates. |
| Current example | Tag `v1.0.0`; assets `cellq-mega2560-v1.0.0.hex` and `cellq-mega2560-v1.0.0.sha256`. |
| Compatible target | Arduino Mega 2560-compatible target, ATmega2560 at 16 MHz; PlatformIO board `megaatmega2560`. Reject Mega 1280. Only a confirmed compatible physical target may be uploaded. |
| Clone policy | Clones are unqualified and untested. Do not advertise clone support or automatically flash an unknown clone. Physical revision and USB bridge of the actual device remain unconfirmed. |
| Bootloader profile | User-supplied board-definition defaults: bundled avrdude, MCU `atmega2560`, programmer protocol `wiring`, 115200 baud. No custom uploader or upload-port override supplied. Pin and validate the bundled uploader/configuration before implementation. |
| USB identity | `2341:0042` is a board-definition default, not a measured identity of the actual device. It cannot authorize upload on its own. |
| Verification | Strict version-matched SHA-256 sidecar and a computed digest of the downloaded HEX bytes. No manifest, signature or trusted public key in v1. |
| Release availability | The user reports packaging is implemented but no release assets have been published. Keep firmware installation disabled until a matching published release is obtained and verified. |

Read-only inspection of the supplied local `C:\CellQ\Frimware\CellQ_Frimware\platformio.ini` confirmed `board = megaatmega2560`, `monitor_speed = 250000`, `monitor_dtr = 0`, and `monitor_rts = 0`. No firmware-repository files were modified. Arduino's [Mega 2560 documentation](https://docs.arduino.cc/hardware/mega-2560/) confirms the ATmega2560 and 16 MHz oscillator. The actual hardware and installed uploader have not been qualified by this planning session.

An independent published-release check was attempted on 2026-10-10, but both the browser fetch and direct HTTPS request were unavailable. This does not establish that the repository is private, absent or has no releases. Availability above is the user's report; the future app must check its own live response and distinguish no matching release from a network/access error.

Remaining proposed defaults to confirm before implementation: stable `vX.Y.Z` releases only, no prereleases or downgrades, and expected HELLO firmware version `X.Y.Z` for tag `vX.Y.Z`. Physical board confirmation is still required; the packaging target is not proof of the installed board. Release-specific preservation of logs, presets or configuration must not be promised without evidence.

### Exact checksum grammar

The sidecar must contain precisely one ASCII line:

```text
<64 lowercase hexadecimal SHA-256 characters><two spaces>cellq-mega2560-vX.Y.Z.hex<LF>
```

Require a final LF, no CR, BOM, leading/trailing whitespace, extra lines, alternate filename or wildcard. Accept only lowercase `0-9a-f` in the 64-character digest. Compute SHA-256 over the original downloaded HEX bytes, with no newline normalization or decoding/re-encoding before hashing. Reject any missing or ambiguous asset, invalid sidecar or digest mismatch. Never substitute a source archive, arbitrary local HEX, another version's checksum or an unverified download. The HEX parser separately validates Intel HEX record checksums, structure and approved flash address ranges; Intel HEX does not declare a trustworthy board identity.

SHA-256 from the same published release verifies integrity under the approved HTTPS/repository trust policy. It does not provide an independent publisher signature. The UI should say "SHA-256 verified", not "Signed" or "Authenticity verified".

## Proposed page

Keep the approved compact shell, existing palette and Segoe UI typography. Use plain language for normal operation and a collapsed Technical details area for raw protocol and uploader output.

```text
Firmware Update                        [Check releases]

Device                 Release
Installed version      Version, board, published date
Connection/status      Release notes and source link
                       [Download and verify]

Package status: Not downloaded / Checking / SHA-256 verified / Failed
Compatibility: Confirmed / Needs confirmation / Unsupported

Update progress
Download -> Check package -> Prepare device -> Install -> Check device
Current step, measured progress or an indeterminate indicator
Plain-language status and appropriate recovery action

[Review update]          [Cancel, when safe]
Technical details (collapsed)
```

No available version, compatibility result or progress is displayed without evidence. Published release selection and package verification do not enter maintenance. The existing connection status remains visible; navigation alone sends no maintenance command. Keep Update disabled until a matching published release, verified package and confirmed compatible target are available. Before that, show "No compatible published release available" only after a successful query finds none; show a separate connection/access error when the query fails. The current production placeholder remains disabled because this turn changes only the plan.

The final confirmation shows the selected connection, board profile, installed and proposed versions, verification result, and release-specific cautions. Suggested wording: "This restarts the device and replaces its firmware. Keep USB and device power connected until the update finishes. An interrupted installation may require recovery." Buttons: Cancel (default) and Install update. Entering maintenance can idle Manual regulator outputs; explain this before confirmation. No automatic test stop.

## Release and verification workflow

1. Query `Mahdi-Nozari/cellq_firmware` published releases. Exclude drafts. Proposed v1 scope is stable `vX.Y.Z` releases without downgrades; this remains a planning default. Never build or download firmware from the main branch.
2. Match the exact version-specific HEX/SHA-256 asset pair and approved board profile. Treat release metadata as untrusted input. Reject missing, ambiguous or incompatible assets and malformed checksum files; constrain paths, file sizes and HTTPS download redirects. Never fall back to source archives or arbitrary local firmware.
3. Download asynchronously to a temporary file. Keep the selected release, asset and expected digest pinned throughout the transaction. Cancellation or failure leaves the device untouched. Do not silently substitute another asset if the release changes.
4. Strictly parse the checksum line above and compare computed SHA-256 of the original HEX bytes with its declared digest. Independently validate Intel HEX structure and approved memory ranges. Compatibility comes from the approved target mapping and physical board confirmation, not a board identity allegedly embedded in the HEX. There is no v1 manifest/signature step; do not present checksum verification as a signature.
5. Mark Verified only when every required check passes. Recheck the exact bytes used by the uploader and prevent concurrent modification during handoff. Never run an uploader executable supplied by a firmware release; use the pinned, bundled Mega bootloader uploader.

Protocol v1 HELLO returns only firmware version and mode. It cannot prove board identity or uniquely identify a physical device. USB identifiers are supporting evidence, not a claim of unique identity. The implementation needs an approved board/asset mapping and must use the uploader's MCU identity check where supported. Never bypass a chip mismatch. If physical identity cannot be established, require clear user selection instead of choosing an arbitrary COM port.

## Confirmed device workflow

1. The user selects Install update in the final confirmation. Acquire an exclusive workflow lease on the existing shared connection. Block competing log, refresh, disconnect and update operations with an explanation.
2. Recheck package evidence, selected connection and HELLO status. If TEST is reported, refuse preparation. Do not stop the test. For other modes, ENTER remains the firmware authority: it rejects running/paused tests or active Manual valves. Preserve an unknown rejection as a technical response rather than inventing an error-code interpretation.
3. Send `CQ1,ENTER` only as part of the confirmed workflow, or explicitly adopt an existing MAINT session. Wait for the exact acknowledgement. Resolve a prior session using documented commands before FWPREP; never silently adopt maintenance just by visiting the page.
4. Send `CQ1,FWPREP` only after confirmation and verified package checks. Wait for exactly `CQ1,OK,FWPREP`. ENTER and FWPREP can wait for the display queue; use bounded host timeouts and do not interpret a timeout as successful preparation. Cancellation after transmission must first resolve the outstanding response/state. No uploader launch on an ambiguous preparation result.
5. After the acknowledged FWPREP, close the shared COM port completely and hand ownership to the bundled Mega bootloader uploader. Keep the app's exclusive workflow lease across this handoff. Use the user's explicitly selected COM port; do not inherit PlatformIO's automatic port selection. Ordinary app serial connections continue to use 250000 baud, 8-N-1, DTR and RTS off. The uploader uses the approved `atmega2560` / `wiring` / 115200 profile and intentionally resets the board; normal connection settings must not be changed globally.
6. Show uploader stages and measured progress on the PC. Use an indeterminate indicator when no trustworthy percentage is available. The device's Form 8 cannot show upload progress while resetting. Capture exit status and output, require the uploader's write/read-back verification, and do not use force-signature or verification-skip options.
7. Release the uploader's port/process ownership before reopening the shared service. Reconnect using bounded retries and fresh HELLO. Require the expected firmware-version string, in addition to successful uploader verification, before showing Update complete. A process exit code alone does not prove the device is running the intended firmware.
8. If the reconnected device is still MAINT and recovery is safe, send `CQ1,EXIT`, require the acknowledgement, then refresh status. If it already rebooted to normal mode, do not send a maintenance-only EXIT. Never assert Form 0 was restored after an unacknowledged EXIT. A lost connection or uncertain board selection offers explicit reconnect/recovery actions.

The current log-maintenance wrapper always CANCELs and EXITs in its finally block. Firmware needs a separate, testable maintenance handoff lifecycle so it does not EXIT between FWPREP and bootloader upload. Preserve existing log behavior while sharing serial ownership and request framing. After FWPREP, do not continue SD operations or assume log cancellation behavior applies; EXIT is the documented preparation-recovery command.

## States and recovery

| State | What the user sees / permitted action |
| --- | --- |
| Not configured / disconnected | Missing configuration or connection; no fabricated release/device data. |
| Checking releases / downloading | Current activity, byte progress if known; Cancel available. Device remains untouched. |
| Checking package | Checking SHA-256, HEX structure and compatibility; Update unavailable. No signature claim. |
| Ready | Exact selected version, evidence-backed compatibility and verification; Review update available. |
| Test active / entry refused | Explain that the operation was refused; finish the activity on the device. No forced stop. |
| Preparing device | Explain maintenance and upcoming reset. Resolve any sent request before cancellation cleanup. |
| Installing | Keep power and USB connected. Do not present ordinary cancellation as safe once flashing starts. |
| Reconnecting / checking version | Show observed status; never a premature success message. |
| Complete | Expected version confirmed, uploader verified, and final device status shown. |
| Failed / outcome uncertain | Distinguish package failure, denied preparation, uploader failure, missing device and version mismatch. Show a specific next step; preserve evidence for support. |

Before the uploader starts, cancellation should safely EXIT an acknowledged maintenance session. During flashing, prevent ordinary cancel/close actions from killing the uploader; explain why and wait for its bounded completion/failure. A hung uploader needs an explicit recovery decision; do not silently force-kill it or retry a reset/upload. Never claim rollback or automatic recovery after a partial flash. A new attempt requires a fresh user confirmation.

If FWPREP fails before uploader launch, recover with EXIT when protocol state and connection permit. If communication is lost, mark the result uncertain and offer reconnection. After a crash, merely connecting to MAINT remains observational; recovery requires an explicit action. Version mismatch or inability to identify the same board is not success.

## Implementation boundaries and validation

Separately testable components: GitHub release client, strict checksum/HEX verifier, approved board profile, bundled uploader adapter with an injectable process runner, exclusive connection handoff/workflow coordinator, and WPF view model/view. Keep dependencies few and pinned. Set host timeout and retry policies explicitly; protocol v1 does not specify their numeric values.

Automated tests must cover no published releases, drafts, proposed prerelease/downgrade restrictions, network/access failures, tag/asset version mismatch, missing/duplicate assets, source-archive/local-file substitution, uppercase or short digests, wrong spacing/filename, CRLF, missing LF, BOM, extra lines, malformed HEX, invalid record checksums or memory ranges, unsupported/unconfirmed board and unqualified clones, digest mismatch, interrupted downloads, changed assets, package modification after verification, and cancellation before confirmation. Use fake HTTP responses; no real release assets are necessary for these tests. Signatures and key management are outside the approved v1 policy.

Fake-device/process tests must prove: no ENTER/FWPREP on page navigation or download; FWPREP never precedes confirmation and verification; active/paused tests and Manual denial prevent uploader launch; fragmented and mixed replies; delayed/missing ENTER/FWPREP acknowledgement; exactly one port owner; intentional reset only through the uploader; failed launch, write, verification, timeout and disconnect; cancellation at handoff boundaries; no concurrent log/refresh operations; safe failed-FWPREP EXIT; reconnect to MAINT; expected version, version mismatch and ambiguous returning ports; crash recovery without hidden upload retries.

WPF checks cover the compact minimum window and scaling, keyboard access, readable disabled reasons, truthful progress, confirmation defaulting to Cancel, and plain-language errors with optional technical details. Run the full existing build/protocol/UI checks after implementation.

Real-device bootloader behavior, compatible-board identification, version reporting, failure recovery and clean-machine bundled-uploader packaging remain hardware/delivery checks. They require a separately approved device upload session. Planning and simulator development must never open a port, send FWPREP or run the real uploader against a device.
