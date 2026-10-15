# Settings

Settings contains only appearance and the log download folder. Both are local app preferences and save automatically.

- Theme: Light (default) or Dark, applied immediately across all six pages. Colors reuse the supplied CellQ palette. The original logo strip and navigation/status colors stay recognizable. Dropdowns, text fields, tables, status badges and caption controls retain readable foregrounds.
- Log download folder: defaults to `CellQ` inside the Windows Documents known folder. Windows resolves the location; no user-profile or drive path is hardcoded. Retrieval does not require that Documents already exists or is currently online. Choose folder changes it; Use default folder restores it. A cancelled picker leaves the preference alone.

Data Logs' save dialog uses the current preference for each new download and creates the folder if needed. Previously saved-copy selection also starts there. Users still choose a filename and the same location when resuming a partial download. Existing log verification, resume and overwrite protection remain unchanged. Firmware's private verified cache is not a log download.

Preferences are versioned JSON in `%LOCALAPPDATA%\CellQ\settings.json`. Writes use a unique temporary file, flush it, then replace the app-owned settings file. Controls wait for a save to finish, and app shutdown awaits any pending save. A failed save restores the last saved theme/folder and displays a notice. Unreadable, corrupt, unknown-version or invalid settings fall back to defaults with a notice; an absent file simply uses defaults. No device communication is available from Settings.

Validation: normal Release build, zero warnings/errors; **1,349 protocol/core assertions across 102 cases and 216 WPF checks passed**. Tests use isolated settings files and fake folder selection inside the workspace, never the user's real preferences or physical ports. Coverage includes defaults, theme application and restart persistence, cancelled/invalid folder choices, Unicode paths, restoring Documents/CellQ, real save-dialog configuration, malformed preferences, failed saves and rollback, dropdown/caption/table contrast, all six pages in Dark, minimum window size, 100/125/150/200% renders, and no serial traffic caused by preference changes.

Section headings and monospace table cells explicitly use the changing theme foreground. Regression checks place both styles under a black-text ancestor to reproduce the live control inheritance bug, verify their light text in Dark, then verify the same controls return to navy in Light. Every page's section headings are also checked in Dark.

Previews from compiled WPF:

- [Light](../artifacts/ui-checks/Settings.png)
- [Light, minimum window](../artifacts/ui-checks/Settings-minimum.png)
- [Dark, minimum window](../artifacts/ui-checks/Settings-dark-minimum.png)

Interactive testing of the native Windows folder/file dialogs, assistive technology and disconnected/permission-restricted Documents locations remains a manual check. No new hardware testing is required for these local preferences. The single-file portable package is implemented; a clean-PC launch test remains required before wider distribution. See PORTABLE.md.
