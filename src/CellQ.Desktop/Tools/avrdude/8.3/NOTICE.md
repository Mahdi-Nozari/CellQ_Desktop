# AVRDUDE 8.3

The CellQ app invokes this separate program for confirmed Mega2560 bootloader uploads. AVRDUDE is distributed under GPL version 2 or later; see COPYING. Copyright notices and build instructions are included in the matching upstream source archive beside this file.

Official release: https://github.com/avrdudes/avrdude/releases/tag/v8.3

Downloaded Windows x64 archive: https://github.com/avrdudes/avrdude/releases/download/v8.3/avrdude-v8.3-windows-x64.zip

Archive SHA-256 checked against the official release's published digest:
8630c7d8bb9682a1685aaa9ad42550ae1131c9d13528f0d20975368ba615e9b0

Matching source: https://github.com/avrdudes/avrdude/archive/refs/tags/v8.3.zip

Bundled source archive: avrdude-v8.3-source.zip

Locally computed source archive SHA-256:
9ebb706a796ac8d5ed5f6906c76aed97eb012c16c661fbb61be97f487754ab77

avrdude.exe and avrdude.conf were copied unchanged from the verified Windows archive. Their hashes are recorded in uploader-manifest.json; the app pins that manifest's SHA-256 in its source and verifies the files before preparation and process launch. Source/license files are not executable dependencies and are excluded from that runtime manifest. The PDB debugging file is omitted.

This bundled tool does not establish clone qualification or the physical identity of the CellQ board. A real bootloader upload still needs an explicit user-confirmed compatible target. Development checks used a fake process for uploads and invoked the real binary only with --help, with no port or upload arguments.
