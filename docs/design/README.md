# CellQ desktop design proposals

The proposals in this directory are static previews. Compact A was selected and implemented in the WPF solution; see IMPLEMENTATION.md for the build and validation results. The earlier proposal PNGs remain labeled design mockups.

Open index.html to compare the HTML proposals. option-a.png, option-b.png, and option-c.png are code-drawn layout mockups, not screenshots of a running WPF app. Browser screenshot attempts produced no output in this environment, so render-mockups.ps1 provides the PNG previews. All three PNGs were rendered and visually inspected for readability, text encoding, and clipping.

Read AGENTS.md and USB protocol v1 in docs/references/USB_PROTOCOL.md. Inspected all seven screenshots in docs/references/ (the actual directory uses the plural name).

Exact screenshot palette: text #001D50; outlines #003378; navigation #0067BF; CellQ cyan #00ACC0; canvas #E6E6E6; content #FFFFFF; fields #F8FCF8; separators #A6A6A6; secondary text #555555; success #0F7900 on #C5E3C1; warning #FCBF71; error #D70000 on #FFBDBD.

Proposed typography: Segoe UI, 14-unit body, 12-unit supporting text, 26-unit page titles; Consolas for filenames and diagnostics. Spacing uses a 4-unit base, 16–24 between panels, 32–36-unit controls. Proposed initial window 1280 × 820; minimum 1040 × 700 with adaptive layouts and scrolling. Validate Windows display scaling during WPF implementation.

A: Service workspace — blue sidebar and spacious content, recommended.
B: Wide workspace — top navigation and full-width tables.
C: Technical workbench — compact sidebar and persistent detail inspector.

The user selected A and requested a smaller snapshot before WPF implementation. `option-a-compact.png` proposes a 1120 x 760 window (previous mockup: 1376 x 940). The sidebar is 190 units wide, the command ribbon is 66 units tall, and table rows are 42 units tall. The revision retains the type sizes, all navigation destinations, device status, four sample rows, and both lower panels. This becomes the proposed starting size if accepted; the earlier 1280 x 820 starting-size proposal is superseded. Render with `powershell -NoProfile -File docs/design/render-mockups.ps1 -Compact`. The snapshot was visually inspected for clipping and readable text; it remains a code-drawn sample mockup, not a WPF application capture.

All filenames, sizes, states, and progress are samples. All device commands are disabled. No hardware or file operations are implemented. The screenshot logo is displayed through a clipped viewport; no logo replacement is invented.

The eventual shell includes Device, Data Logs, Firmware Update, Diagnostics, Settings, Help, upper command ribbon, persistent device/status area, and keyboard access. A labeled sample gallery will include disconnected, connecting, busy test, maintenance, transferring, verifying, updating, complete, and error.
