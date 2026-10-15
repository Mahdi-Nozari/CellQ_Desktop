@echo off
setlocal
set "cellq_app=%~dp0dist\CellQ.exe"
if not exist "%cellq_app%" (
    echo The CellQ application has not been published. Run publish.cmd first.
    pause
    exit /b 1
)
start "" "%cellq_app%" %*
exit /b
