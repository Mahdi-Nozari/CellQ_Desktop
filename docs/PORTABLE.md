# Single-file portable distribution

Users receive only `dist/CellQ.exe`. Copy it to another folder or computer and double-click it. No installer, launcher script, separate .NET installation, uploader folder or development project is required. The package targets Windows x64; validate it on a clean Windows 11 x64 PC before wider distribution. Windows must recognize the device's USB connection to use hardware features.

Within this project, `start.cmd` and `start.ps1` both launch this same published EXE. Publishing therefore updates all three launch methods together; neither launcher falls back to an old development output. If it has not been published, the launcher asks you to run `publish.cmd`.

The replaceable application icon is [src/CellQ.Desktop/Assets/CellQ.ico](../src/CellQ.Desktop/Assets/CellQ.ico). Replace it and publish again to change the embedded Explorer/taskbar/window icon. See [icon source and regeneration](../src/CellQ.Desktop/Assets/README.md). The full header logo is unchanged.

## Build

Double-click `publish.cmd` in the project folder. Or run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\publish.ps1
```

The script uses the pinned local .NET SDK 10.0.401 when available, otherwise that SDK must be installed. First-time runtime-pack restore needs internet access. The PowerShell policy override applies only to this process.

The script runs the full automated build/core/UI checks in separate output folders, restores the portable dependency lock files, then publishes with `Portable.pubxml`. It targets `win-x64`, includes .NET 10.0.12 and native runtime libraries, compresses the bundle, embeds debug symbols, and disables trimming/ReadyToRun. WPF assets and the uploader's executable, configuration, manifest, notices, license and matching source are embedded. [Microsoft's single-file documentation](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview) explains runtime extraction; the deprecated all-content extraction mode is not used.

Publish must produce exactly one file. The script renames the host to `CellQ.exe`, copies it alone to a directory containing spaces and Unicode, and verifies it from another working directory with unusable `DOTNET_ROOT` paths. Only after all checks pass does it atomically replace `dist/CellQ.exe`. A failed build/check leaves the previous package intact. If the portable EXE is open and Windows locks replacement, close it after its active operation finishes, then publish again.

Validation reports, executable SHA-256 and WPF previews are under `artifacts/portable-build/<build-id>/`. Those files are development evidence; users need only the EXE.

## Files created while running

The portable app saves preferences under `%LOCALAPPDATA%\CellQ\settings.json`. Logs default to the Windows Documents folder's `CellQ` subfolder, and users can choose another folder. Verified firmware downloads continue to use the private firmware cache and the published-release policy; firmware images are not baked into the app.

The .NET host extracts its native libraries into the user's runtime cache. Firmware tools extract lazily under `%LOCALAPPDATA%\CellQ\Tools\avrdude\8.3\<pinned-manifest-hash>`. No files are written beside the EXE. Extraction is asynchronous, uses unique scratch files and atomic moves, and verifies embedded/cached bytes. Verified files are reused, damaged/missing files are repaired, cancellation leaves no finalized partial tool, and independent instances can share the cache. The existing uploader verifies the pinned manifest/configuration/executable again and holds files against modification while its process runs. Extraction never connects or prepares a device.

The distribution includes AVRDUDE's license and matching upstream source, unchanged. To obtain the included tool files without hardware operations, run `CellQ.exe --export-tools <output-folder>`; `tools-location.txt` points to the extracted notices, COPYING and source archive. Internet is not needed for this export.

## Validation and next checks

Latest validation: zero build/publish warnings or errors; 2,140 protocol/core assertions across 151 cases and 273 UI checks passed. The final EXE is approximately 72.1 MiB and includes no demo CSV. Its standalone check, existing-package replacement, Unicode path, read-only Windows port metadata lookup, local-file graph rendering and native uploader help passed. Matching source/license export was also validated in an earlier package build. [Passing standalone report](../artifacts/portable-build/a3c1c2b1b4f84cbebb10f52174c292da/verification/PASS.txt), [EXE SHA-256](../artifacts/portable-build/a3c1c2b1b4f84cbebb10f52174c292da/CellQ.sha256), [actual portable graph preview](../artifacts/portable-build/a3c1c2b1b4f84cbebb10f52174c292da/verification/LocalGraph.png).

This package includes a bounded initial-connection recovery for the reported COM10 `CQ1,ERR,SYNTAX` response: wait 250 ms after that exact rejection and resend only `CQ1,HELLO` once, without reopening/resetting the port or entering maintenance. Simulator checks cover mixed/fragmented input, all device modes, repeated rejection, strict handling of other responses, timeout, cancellation, stale responses and unchanged status-refresh behavior. The user confirmed that recovery works on the second Windows 11 computer/Mega running v1.0.1; its underlying cause remains unconfirmed. No firmware source or installed firmware was changed.

The updated selectors now show only Windows-named Mega candidates, as raw COM names. Only a successful response changes the current candidate to `CellQ (COM10)`; disconnect/failure clears that label. Twenty-three added UI checks cover matching/nonmatching names, numeric ordering/deduplication, read-only native lookup, cancellation, both selectors, unchanged raw COM values, selection preservation/removal, metadata failures and empty results. No new dependency or automatic serial probe is needed. Actual Windows device-name matching on the second PC remains a user check. [Simulated candidate-list preview](../artifacts/ui-checks/Device-port-list-simulator.png), [simulated identified label preview](../artifacts/ui-checks/Device-CellQ-label-simulator.png).

The explicit `CellQ.exe --verify-package <output-folder> [local-csv-path]` mode uses isolated settings/cache files, a port factory that always throws, and an empty device list. It verifies the x64 single-file app and bundled runtime core library, checks the runtime host and extracted WPF engine paths, checks/extracts all tool files, invokes only native `avrdude --help`, and renders all six actual WPF pages at minimum window size. With a CSV path it also loads the local file and renders the popup; publish.ps1 supplies a test-only fixture. The check rejects bundled CSV demo resources. It sends no network or serial requests and performs no upload.

The latest build additionally verifies that the native EXE has an icon and the WPF windows load its embedded icon resource. A native Shell extraction was visually checked: [actual EXE icon](../artifacts/ui-checks/ExecutableIcon.png), [large artwork](../artifacts/ui-checks/AppIcon.png). Both real launch scripts were exercised with offline package verification and passed: [CMD launcher](../artifacts/launcher-checks/f7b6835a03f44f1487aa983768a55320/cmd-report/PASS.txt), [PowerShell launcher](../artifacts/launcher-checks/f7b6835a03f44f1487aa983768a55320/powershell-report/PASS.txt). Neither test opened a port or modified user preferences.

Automated checks also cover cache corruption/missing files, locked-file reuse, concurrent first extraction, cancellation before/during extraction, recovery, rejected embedded manifests, inaccessible cache paths, and a fake upload process using the resolved cache paths. Existing protocol/firmware/log tests remain part of every publish.

This development machine has .NET installed; hiding runtime roots and checking the bundled core library and native module paths proves the package uses its own runtime here, but does not replace a clean-PC test. Next, copy only `CellQ.exe` to a Windows 11 x64 PC with no .NET SDK/runtime installed. Verify opening, all pages, Light/Dark, folder selection and persistence after reopening. Then check device identification and a log download with CellQ attached. Any real firmware upload remains a separately confirmed user action; no device was flashed while building this package.

The current EXE is unsigned. Publisher/signing checks remain a distribution decision. Self-contained runtime servicing requires rebuilding and redistributing the EXE with an updated pinned runtime; installed machine-wide runtime updates do not update this bundled copy.
