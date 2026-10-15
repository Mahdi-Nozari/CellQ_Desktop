# Pressure graphs

In **Data Logs**, choose **Open log file…** to view a CellQ CSV already on the computer. After a verified download, select its row and choose **View graph**. These actions open a separate, resizable popup; they never connect to hardware, enter maintenance, download a file or change a log. The app has no sample graph action or bundled demo CSV.

One shared pressure-versus-elapsed-time plot has six individually colored channel toggles. The preset name and local filename appear above it. Hover over the plot to see the nearest actual sample's pressures and timestamp; missing values show a dash.

## Moving through time

- **Time span** sets the visible duration. Type seconds (`30`), minutes:seconds (`02:30`) or hours:minutes:seconds (`01:00:00`), then choose **Apply** or press Enter.
- The slider and mouse wheel zoom between the whole recording and a one-second window (or the full duration for shorter logs). Wheel zoom stays centered on the pointer.
- Drag horizontally, use the scrollbar, or choose the earlier/later arrows. When the graph has keyboard focus, Left/Right pan, +/- zoom, and Home/End go to its boundaries.
- **Jump to time** accepts the same formats; choose **Go** or Enter to center on that time. From whole-test view it restores the previous detailed span (initially 30 seconds, shortened for small logs), so the jump visibly locates the requested moment. Near a boundary the view clamps to the recorded range and still marks the requested time. Invalid input gives a correction without moving the plot.
- **Show whole test** restores the full recorded range.

## Data and rendering

The reader requires CellQ CSV version 1 and all named columns: TYPE, ELAPSED_MS, STATE, G1_CYCLE, G2_CYCLE, V1–V6 and P1_X10_KPA–P6_X10_KPA. Column positions come from the header. Only SAMPLE rows contribute readings. EVENT/END timestamps can extend the time range without creating pressure points. Configured targets never replace measured values.

Pressure values are divided by 10 and retain their sign: 258 becomes 25.8 kPa and -350 becomes -35.0 kPa. Missing values and long sampling gaps break curves rather than adding zeros or joining across lost data. Invalid files fail with a readable error.

Loading and trace reduction run on background tasks, cancelled when the popup closes. For long recordings, visible samples are grouped per screen column, retaining first/minimum/maximum/last values in time order so peaks remain visible. This changes only drawing, never the file. The vertical scale uses the selected channels' full ranges to remain stable while panning. Readouts use original samples.

The viewer accepts up to 5,000,000 samples and 16 KiB per CSV record. At the provided 20 ms interval this covers more than 24 hours. Multiple windows each load their own snapshot. No chart dependency was added. Light channel colors match the supplied display's borders; the hues are lightened for dark contrast.

## Verification

Core checks cover a test-only CSV fixture, signed units, quoted/multiline metadata, missing readings, invalid schema/values/times, cancellation, time parsing, late-started and six-hour recordings, bounded panning and anchored zoom, gaps, peak preservation and bounded drawing counts. WPF checks cover offline entry points, toggles, editable duration/Enter, jumping, scrollbar, cursor values, bad input, compact layout, both themes, closing and malformed files. Tests ensure the demo action and embedded CSV are absent. The standalone EXE loads and renders an explicitly supplied local CSV without serial or network requests. The fixture lives under tests/Fixtures and is embedded only in test assemblies.

The jump regression test first failed against the earlier version, then passed after the fix: type seconds into the bound textbox, invoke the actual Go button from whole-test view, verify a detailed span centered on the requested time, then change the textbox and use its Enter binding. Latest full validation passed 2,140 core assertions across 151 cases and 273 UI checks with no build/publish warnings or errors.

Previews using the local test fixture: [Light](../artifacts/ui-checks/LocalGraph-Light.png), [Dark](../artifacts/ui-checks/LocalGraph-Dark.png), [compact](../artifacts/ui-checks/LocalGraph-minimum.png), [whole recording](../artifacts/ui-checks/LocalGraph-whole-test.png).

Previews render actual WPF content. The user has confirmed that the graph features work. High-DPI/multiple-monitor changes and clean-PC responsiveness with the user's largest logs remain manual acceptance checks. No physical device was connected by the agent.
