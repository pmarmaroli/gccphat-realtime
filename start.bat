@echo off
setlocal
REM ============================================================================
REM  start.bat  -  Double-click to build and launch the GCC-PHAT Real-Time UI.
REM ============================================================================
cd /d "%~dp0"

set "PROJ=src\GccPhat.RealTime\GccPhat.RealTime.csproj"
set "EXE=src\GccPhat.RealTime\bin\Release\net8.0-windows\GccPhat.RealTime.exe"

REM --- Check the .NET SDK is available --------------------------------------
where dotnet >nul 2>&1
if errorlevel 1 (
    echo [ERROR] .NET SDK not found.
    echo         Install the .NET 8 SDK from https://dotnet.microsoft.com/download
    echo.
    pause
    exit /b 1
)

REM --- Build (incremental, fast after the first run) ------------------------
echo Building GccPhat.RealTime ^(Release^)...
dotnet build "%PROJ%" -c Release --nologo -v minimal
if errorlevel 1 (
    echo.
    echo [ERROR] Build failed. See the messages above.
    echo.
    pause
    exit /b 1
)

if not exist "%EXE%" (
    echo [ERROR] Executable not found: %EXE%
    echo.
    pause
    exit /b 1
)

REM --- Launch the UI detached so this window can close ----------------------
echo Launching the interface...
start "" "%EXE%"
exit /b 0
