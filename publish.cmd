@echo off
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish.ps1"
if errorlevel 1 (
    echo.
    echo Portable build failed. Read the error above; the previous package is kept.
    pause
    exit /b 1
)
echo.
echo Ready: %~dp0dist\CellQ.exe
pause
