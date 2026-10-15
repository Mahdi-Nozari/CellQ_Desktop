# Compact Design A implementation

This records the earlier sample-only foundation. For the subsequent live Device and Diagnostics implementation and current validation, see [Device connection](../DEVICE_CONNECTION.md).

Implemented the approved 1120 x 760 WPF/.NET 10 shell. Minimum window size: 1040 x 700. All six pages share the ribbon, navigation, theme, sample banner, device area, and persistent disconnected status. Device actions remain disabled. The application has no serial, transfer, deletion, release-download, or firmware-upload services.

The solution is `CellQ.Desktop.sln`; launch the built app with `start.ps1`. Build and rerender with `build.ps1`.

## Verification

- Release solution build: succeeded, zero warnings, zero errors, SDK 10.0.401.
- UI harness: **84 checks passed**, covering six-page navigation, disabled workflow actions, case-insensitive filename/folder filtering, empty results, shared display preference, nine labeled sample states, preserved disconnected status, opaque canvas rendering, and binding errors/warnings.
- Actual compiled WPF views rendered offscreen at 1120 x 760 and minimum 1040 x 700. Raster DPI outputs produced at 100%, 125%, 150%, and 200% while preserving logical layout dimensions.
- Visually reviewed all six pages, the minimum-size Data Logs view, sample Error treatment, and 150% output. The initial render exposed a missing offscreen canvas background; the root now explicitly paints the exact reference canvas color. Icon glyphs were replaced with code-native vector geometry for clear rendering.
- The state progress binding is explicitly OneWay; rendering the Device view caught and resolved an initial read-only binding failure.

Generated previews: `artifacts/ui-checks/DataLogs.png`, `Device.png`, `FirmwareUpdate.png`, `Diagnostics.png`, `Settings.png`, `Help.png`, and supporting size/state/DPI images. These show the implemented WPF views; earlier images in this directory remain design mockups.

Physical Windows scaling transitions, native window/taskbar behavior, interactive keyboard and screen-reader use, device integration, protocol simulator/failure tests, firmware policy configuration, installer packaging, and clean-machine installation tests remain for later work. No real-device behavior has been tested or claimed.
