# CellQ Desktop App Instructions

## Product
Build a Windows desktop maintenance application for the CellQ Pressure Distribution System using C# and WPF on .NET 10. This is a separate project from the Arduino firmware. Develop one feature at a time; do not modify the firmware repository without an explicit request.

The app has four primary areas: Device, Data Logs, Firmware Update, and Diagnostics, plus Settings and Help. It is a maintenance tool, not a remote test controller.

## Source of truth
- Read docs/reference/USB_PROTOCOL.md before implementing serial communication.
- Inspect every GUI screenshot in docs/reference/ before proposing the visual design.
- Treat the protocol document as versioned. Do not silently invent commands or reinterpret responses.
- Ask for the GitHub repository, release-asset naming, and verification policy before implementing firmware downloads.

## Visual direction
Use the display GUI as a visual relative, not as a desktop layout template. Preserve its light surfaces, dark navy text and outlines, strong blue navigation, cyan CellQ accent, and purposeful channel/status colors. Take exact colors and logo treatment from the supplied screenshots; do not guess them.

Design for a desktop window: a compact upper command ribbon, clear navigation, spacious content panels, useful tables, progress views, Settings, keyboard access, and a persistent device/status area. Avoid oversized touchscreen buttons, decorative gradients, excessive animation, and color-only status indicators. Support Windows display scaling and a practical minimum window size.

Show honest states: disconnected, connecting, busy test, maintenance, transferring, verifying, updating, complete, and error. Never show mock device data as if it were live.

## USB and safety
- One connection service owns the COM port. All pages share it; never open a second serial monitor on the same port.
- Use 250000 baud, 8-N-1, with DTR and RTS explicitly disabled for normal connection, debugging, and log transfer.
- Opening the app, connecting, and viewing debug messages must not send CQ1,ENTER or open Form 8.
- Parse D,... debug lines, CQ1,... responses, and binary data frames from the same byte stream.
- Follow the protocol's one-request-at-a-time rule, CRC checks, offsets, timeouts, and cancellation behavior.
- Treat a running/paused test or active Manual valve as a denied maintenance entry. Never stop a test automatically.
- Send CQ1,EXIT when a completed or cancelled maintenance workflow can safely end. Handle app crashes/reconnection to an existing MAINT session.
- Do not add test-start, valve, regulator, calibration, or preset-edit controls without a separate approved protocol and safety review.

## Data and firmware
- Download to a temporary .part file; verify frame CRCs, offsets, final size, stream CRC, and whole-file hash before marking the local copy complete.
- Never overwrite an existing local log silently. Deletion requires a clear user confirmation and the protocol's matching size/CRC.
- Do not display SD free-space figures: protocol v1 does not provide them.
- Firmware updates must use a published, compatible release asset, not source from the main branch. Verify the asset before invoking a bundled bootloader uploader.
- The PC app shows upload progress and reconnects to verify the resulting firmware version. Form 8 cannot report progress while the Arduino is resetting.

## Engineering and delivery
- Keep serial framing, protocol state, file transfer, release download, firmware upload, and WPF views separately testable.
- Keep the UI responsive with asynchronous work and cancellation.
- Build a fake-device/protocol simulator and automated tests for partial reads, mixed debug/data traffic, CRC failures, disconnects, interrupted downloads, rejected maintenance, and failed uploads.
- Keep dependencies few and pinned. Do not embed credentials or tokens.
- Report exactly what was built, tested, and still requires a real device.
- The final deliverable should be an easy-to-install, self-contained Windows package; test installation on a clean Windows machine.