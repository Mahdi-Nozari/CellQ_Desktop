# Final cleanup — 2026-10-15

The release has no sample graph action, bundled demo CSV, sample-source branching, sample-page flags or obsolete sample banner. Data Logs still opens saved CellQ CSVs offline and graphs verified downloads. Help and documentation describe those workflows.

The original CSV remains only in `tests/Fixtures/pressure-log.csv` for regression coverage; test assemblies embed it, while the production app does not. Standalone package verification accepts an optional local CSV path, so publishing can check graph rendering without bundling test data.

The final build/publish passed with zero warnings/errors, 2,140 core assertions across 151 cases and 273 WPF checks. The standalone EXE passed from a Unicode path with unusable runtime-root settings, including local CSV rendering and a check for absent demo resources. After deleting development build outputs, both launch scripts passed isolated offline package checks. No serial port was opened and no firmware was uploaded.

Cleanup removed 45 generated targets (3,479.5 MiB): old release builds, extracted verification caches, accumulated test workspaces, browser preview profiles, obsolete SDK download/archive copies, duplicate uploader downloads/source extraction, old sample graph previews, stale executable replacement files, and project bin/obj outputs. The latest release evidence and current UI previews remain. Source, tests, approved reference screenshots/protocol, icon assets, pinned local SDK/NuGet cache and bundled uploader license/source are retained. User preferences and downloaded logs outside the workspace were not touched.

Use `dist/CellQ.exe` or `start.cmd`. Use `publish.cmd` to build again; generated project outputs will be recreated.

- [Standalone verification](../artifacts/portable-build/a3c1c2b1b4f84cbebb10f52174c292da/verification/PASS.txt)
- [EXE SHA-256](../artifacts/portable-build/a3c1c2b1b4f84cbebb10f52174c292da/CellQ.sha256)
- [Cleanup inventory](../artifacts/portable-build/a3c1c2b1b4f84cbebb10f52174c292da/cleanup.json)
- [CMD launcher check](../artifacts/launcher-checks/f7b6835a03f44f1487aa983768a55320/cmd-report/PASS.txt)
- [PowerShell launcher check](../artifacts/launcher-checks/f7b6835a03f44f1487aa983768a55320/powershell-report/PASS.txt)

The user has confirmed the hardware workflows and graph features work. A clean Windows installation and broader high-DPI/accessibility testing remain distribution acceptance checks; the agent did not perform a physical-device test during cleanup.
