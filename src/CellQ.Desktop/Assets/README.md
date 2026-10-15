# Application icon

Replace **CellQ.ico** here with your own Windows icon, then run `publish.cmd` from the project folder. The icon is embedded in the EXE and used by the app's windows; no companion logo file needs to be distributed.

Include 16, 24, 32, 48 and 256 px sizes (32-bit color/transparency) for sharp Windows Explorer/taskbar results. The current file also includes 20, 40, 64 and 128 px sizes.

**CellQ.svg** is the editable source of the initial CQ monogram. It follows the existing black C and cyan Q (`#00ACC0`), on the header's gray (`#E6E6E6`). Edit the SVG's paths/colors and regenerate the ICO with:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\Update-AppIcon.ps1
```

The generator supports this SVG's rect/path elements. If you replace the ICO directly, regeneration is unnecessary. Rebuild to change an executable's embedded icon; replacing an asset cannot modify an already-built EXE. The full CellQ header logo is unchanged. Windows may cache old icons until the EXE is copied to a new location or Explorer refreshes.
