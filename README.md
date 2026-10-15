# CellQ Desktop

Download the portable Windows app from [CellQ Desktop v1.0.0](https://github.com/Mahdi-Nozari/CellQ_Desktop/releases/tag/v1.0.0). Under **Assets**, choose **CellQ.exe**, save it to your computer and double-click it. No installer or separate .NET installation is needed. **CellQ.sha256** is an optional integrity-check file.

Final cleanup: sample graph/demo data removed from the app; local and downloaded log graphing retained. [Release checks and cleanup inventory](docs/FINALIZATION.md).

C# / WPF on .NET 10, using compact Design A: 1120 x 760 initially, minimum 1040 x 700. Device, Data Logs, Firmware Update and Diagnostics share one connection service. Firmware installation is available after a published update is verified, a CellQ is connected, and the user approves installation. No physical device was flashed by the agent; the user has reported a successful download and flash.

## Run and build

For portable use, double-click **dist\CellQ.exe**. This single Windows x64 executable includes the app, .NET runtime and firmware uploader; users need no installer or runtime setup. Build or rebuild it by double-clicking **publish.cmd**, which runs automated checks and verifies the one-file publish before replacing the package. See [portable build, validation and clean-PC checklist](docs/PORTABLE.md).

Double-click `start.cmd` in this folder to launch the same current `dist\CellQ.exe`. Both launchers (`start.cmd` and `start.ps1`) use this one published copy, so they cannot launch an older development build. Rebuild it with `publish.cmd`. The CMD launcher does not run PowerShell scripts or change execution policy. The app opens on Device and enumerates ports; choose the connection for your CellQ and click **Connect**. It never opens a port automatically. For unpublished development work, build with `build.ps1` and run `src\CellQ.Desktop\bin\Release\net10.0-windows\CellQ.Desktop.exe` directly.

To build from PowerShell in this folder:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

The policy override applies only to that PowerShell process. `start.ps1` is an alternative launcher for the published EXE and can be called with the same process-only override if scripts are disabled.

Windows and SDK 10.0.401 are required (`global.json` pins the SDK). Scripts prefer `.tools/dotnet`, otherwise the installed SDK/runtime. The local SDK archive was SHA-512 checked against `.tools/sdk-asset.json` before extraction. `System.IO.Ports` 10.0.0 is the only direct NuGet dependency; lock files pin its dependency graph. Restore uses the explicit `NuGet.Config`, nuget.org, `.tools/packages`, and locked mode. Restricted environments may also need read access to NuGet's user configuration.

`build.ps1` builds Release and runs protocol/parser/fake-device and WPF checks. `-SkipUiChecks` skips only the WPF harness. Test projects have no test-framework package dependencies.

Latest validation: **zero build warnings/errors, 2,140 protocol/core assertions across 151 cases, and 273 WPF UI checks passed**. See [connection discovery and recovery policy](docs/DEVICE_CONNECTION.md), [Settings validation](docs/SETTINGS.md), [Firmware Update validation](docs/FIRMWARE_UPDATE.md), [Data Logs validation](docs/DATA_LOGS.md) and [pressure graph validation](docs/LOG_GRAPHS.md) for covered faults and remaining manual checks.

## Device and Diagnostics

Connect opens the shared port at **250000 baud, 8-N-1, DTR/RTS off, handshake off** and sends HELLO to identify the device. Refresh status asks again. Device states use readable labels: Idle, Manual mode, Test active (running or paused), and Maintenance mode. The state is the last confirmed observation, not continuous telemetry.

Opening the app, connecting, switching pages and viewing messages do not enter maintenance. Diagnostics shows readable activity by default; Technical details shows original `D,...` lines, protocol replies and framing faults. Arbitrary debug data is preserved rather than guessed or decoded. Search messages, pause the display, or select a technical row to copy its full text. Pause/Clear messages affect only bounded local memory and keep the reader running.

An existing Maintenance session is reported without changing it during connection. Data Logs provides explicit recovery. Cancelling/closing a log workflow the app has entered will attempt safe CANCEL/EXIT before closing the port; an observation-only disconnect leaves an existing session unchanged.

## Data Logs

1. **Browse device logs** temporarily enters maintenance, lists `/DATA` and its dated/NO_RTC folders, then returns the device to its normal screen. Merely opening Data Logs does not request maintenance. Running/paused tests block access; the device also decides whether active Manual valves deny entry. Accepted entry idles manual regulator outputs.
2. Select a file and **Download / Resume**. Choose a save filename. Downloads use a `.part` copy and a progress sidecar; only checked bytes can be resumed. After a stopped transfer or USB loss, reconnect, browse again and choose the **same save location**. Changed files and damaged progress are refused, and existing completed files are never overwritten.
3. A file becomes complete only after frame CRC16, offsets, total byte count, GET stream CRC32 and device whole-file HASH checks pass. The app also rereads disk bytes and seals a local SHA-256 receipt, then renames the verified temporary copy to its final filename.
4. **Use saved copy…** rechecks a previously completed copy and its receipt after restarting the app. This is local-only; it does not enter maintenance.
5. **Delete from device…** requires a verified local copy and an explicit confirmation (default No). The app rechecks local bytes, obtains a fresh device HASH, and sends conditional DELETE with the matching size and CRC32. The local copy is kept.

Each explicit browse, download or deletion is a complete workflow and sends EXIT once at its end, including cancellation when communication is available. If USB loss or a missing acknowledgement prevents confirmed exit, the app does not claim the device returned to normal. Reconnect, then resume or choose **Return to normal**. Closing during an owned workflow cancels it safely before closing the shared port and warns if maintenance may remain.

The protocol source is [USB_PROTOCOL.md](docs/references/USB_PROTOCOL.md), unchanged. Implementation details, host timeout policies and validation are in [Data Logs](docs/DATA_LOGS.md). No SD free-space value, test/output controls, firmware commands or credentials have been added.

**Pressure graphs:** choose **Open log file…** for a saved CellQ CSV, or **View graph** for a selected verified download. A separate popup compares six pressures in kPa with channel toggles, editable time span, a zoom slider, horizontal mouse dragging and jump-to-time. Viewing is local-only. The app contains no demo CSV or sample graph action. See [controls and validation](docs/LOG_GRAPHS.md), [Light preview](artifacts/ui-checks/LocalGraph-Light.png) and [Dark preview](artifacts/ui-checks/LocalGraph-Dark.png).

## Firmware Update

Choose **Check releases**, select a published version, review its notes, then **Download update**. The app checks the update automatically. Network/access failures are shown separately from an empty release list.

Installed firmware stays across the top. Below it, Update progress and Update file share equal columns. The release selector, **Selected update: [version]**, download check and installation action are grouped in Update file. This version is the chosen release; Installed firmware is the device's reported version. Main controls fit without scrolling at the minimum window size; release notes and technical details expand when needed.

After checking releases, the Installed firmware panel shows **Latest version installed** when the connected device matches the newest eligible release, or **Update available: [version]** when a newer version exists. The label uses the newest release rather than the selected item and refreshes on connection/version changes. Disconnected, unknown-version and empty-release states cannot claim that the latest firmware is installed.

Connect your CellQ and choose **Update firmware**. One confirmation shows the current and new versions and asks you to keep USB and power connected. There is no board questionnaire. The app displays progress and reconnects to check the new version. Same-version reinstalls and downgrades remain unavailable. Once installation starts, ordinary cancel/close actions cannot interrupt it. See [Firmware Update](docs/FIRMWARE_UPDATE.md) for automatic verification, failure recovery and technical details.

AVRDUDE 8.3 is bundled with its pinned executable/configuration, [license, matching source archive and provenance](src/CellQ.Desktop/Tools/avrdude/8.3/NOTICE.md). Development uploads use a fake process; the real native binary was exercised only with `--help`, without any port or upload arguments.

## Navigation and remaining work

Settings has two preferences: **Light / Dark** appearance and the **log download folder**. The default folder is `CellQ` inside the Windows Documents folder, including redirected Documents locations. Choose folder changes it; Use default folder restores it. Downloads still ask for a filename, starting in that folder. The folder is created when saving a log if needed. Preferences save automatically and load on the next launch; a failed save restores the previous settings and displays a notice. Firmware's verified temporary cache is managed separately. [Light preview](artifacts/ui-checks/Settings.png), [Dark preview](artifacts/ui-checks/Settings-dark-minimum.png).

Alt+D, Alt+L, Alt+F, Alt+G, Alt+S and Alt+H navigate the six pages. Tab/Shift+Tab move between controls. Status indicators include text as well as color. Settings supports keyboard access through Alt+T for Theme, Alt+C for Choose folder and Alt+U for Use default folder. Help is a plain-language workflow guide: connect first, save and check logs, install updates, recover from interruptions, and choose preferences. It uses the visible button names, puts keyboard shortcuts behind an expander, and omits protocol terminology. [Help preview](artifacts/ui-checks/Help.png), [Dark preview](artifacts/ui-checks/Help-dark.png), [recovery and settings at minimum size](artifacts/ui-checks/Help-Light-bottom.png). The Help ribbon has no placeholder documentation/About actions.

The theme retains the supplied colors/logo, with Segoe UI for desktop text and Consolas for technical details. Actual compiled WPF previews are in `artifacts/ui-checks`: [Data Logs disconnected](artifacts/ui-checks/DataLogs.png), [simulator example](artifacts/ui-checks/DataLogs-simulator.png), [Firmware Update](artifacts/ui-checks/FirmwareUpdate.png), [simulated firmware progress](artifacts/ui-checks/FirmwareUpdate-installing-simulator.png), [Device](artifacts/ui-checks/Device.png), and [Diagnostics](artifacts/ui-checks/Diagnostics.png). Simulator examples have an explicit label. Fake transport/card/HTTP/uploader models are compiled only into test projects.

The user has reported a real connection on COM6, firmware 1.0.0 and Idle. New log workflows were verified using the simulator; no physical device was connected by the agent. Real SD-card transfers/deletion, electrical line levels/reset suppression, display ACK delays, USB removal and interactive accessibility/scaling still need hardware testing. The single-file portable package is implemented and tested here; a clean Windows PC launch test remains required before wider distribution. Historical foundation and connection-only reports remain in `docs/design/` and `docs/DEVICE_CONNECTION.md`.
