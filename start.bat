@echo off
REM ============================================================================
REM  start.bat  -  Double-click to build and launch the GCC-PHAT Real-Time UI.
REM  The window stays open so you can read any errors.
REM ============================================================================

REM -- Self-relaunch inside cmd /k so the window never auto-closes -----------
if "%~1"=="--child" goto :main
cmd /k ""%~f0" --child"
exit /b

:main
setlocal
cd /d "%~dp0"

set "PROJ=src\GccPhat.RealTime\GccPhat.RealTime.csproj"
set "EXE=src\GccPhat.RealTime\bin\Release\net8.0-windows\GccPhat.RealTime.exe"
set "ASSETS=src\GccPhat.RealTime\bin\Release\net8.0-windows\Assets"

REM --- Close any running instance so the build can overwrite its files ------
taskkill /F /IM GccPhat.RealTime.exe >nul 2>&1

REM --- Check the .NET SDK is available --------------------------------------
where dotnet >nul 2>&1
if errorlevel 1 (
    echo [ERROR] .NET SDK not found.
    echo         Install the .NET 8 SDK from https://dotnet.microsoft.com/download
    echo.
    goto :eof
)

REM --- YAMNet model: download once if not present ---------------------------
if exist "%ASSETS%\yamnet.onnx" goto :after_yamnet

echo.
echo [SETUP] YAMNet model not found. Downloading and converting ^(one-time, ~400 MB^)...
echo         Press Ctrl+C to skip and launch without classification.
echo.

where python >nul 2>&1
if errorlevel 1 (
    echo [WARN] Python not found on PATH - skipping YAMNet setup.
    echo        Install Python 3.9+ from https://python.org then re-run.
    echo.
    goto :after_yamnet
)

if not exist "%ASSETS%" mkdir "%ASSETS%"

echo [1/3] Installing tensorflow + tf2onnx via pip...
python -m pip install --quiet --upgrade tensorflow tensorflow-hub tf2onnx
if errorlevel 1 (
    echo [WARN] pip install failed - skipping YAMNet setup.
    echo.
    echo        Diagnostics:
    for /f "delims=" %%V in ('python --version 2^>^&1') do echo          Python version : %%V
    for /f "delims=" %%A in ('python -c "import platform;print(platform.machine())" 2^>^&1') do echo          Architecture   : %%A
    echo.
    echo        TensorFlow requires 64-bit Python 3.9-3.12 on x86_64.
    echo        Python 3.13+, ARM64 ^(Surface/Snapdragon^), or 32-bit Python have no TF wheel.
    echo        Fix: install Python 3.11 or 3.12 x64 from https://python.org, then re-run.
    echo.
    goto :after_yamnet
)

echo [2/3] Downloading YAMNet from TFHub and converting to ONNX via Python API...
set "ONNX_OUT=%ASSETS%\yamnet.onnx"
python -c "import tensorflow_hub as hub, tensorflow as tf, tf2onnx; m=hub.load('https://tfhub.dev/google/yamnet/1'); sig=[tf.TensorSpec([None],tf.float32,'waveform')]; fn=tf.function(lambda w:m(w)[0],input_signature=sig); tf2onnx.convert.from_function(fn,input_signature=sig,opset=13,output_path=r'%ONNX_OUT%'); print('Conversion OK')"
if errorlevel 1 (
    echo [WARN] YAMNet conversion failed - classification will be disabled.
    goto :after_yamnet
)

echo [3/3] Downloading class map CSV...
curl -fsSL -o "%ASSETS%\yamnet_class_map.csv" "https://raw.githubusercontent.com/tensorflow/models/master/research/audioset/yamnet/yamnet_class_map.csv"
if errorlevel 1 (
    echo [WARN] Class map download failed - class names will use generic labels.
)

echo [SETUP] YAMNet ready.
echo.

:after_yamnet

REM --- Build (incremental, fast after the first run) ------------------------
echo Building GccPhat.RealTime ^(Release^)...
dotnet build "%PROJ%" -c Release --nologo -v minimal
if errorlevel 1 (
    echo.
    echo [ERROR] Build failed. See the messages above.
    echo.
    goto :eof
)

if not exist "%EXE%" (
    echo [ERROR] Executable not found: %EXE%
    echo.
    goto :eof
)

REM --- Launch the UI detached so this window can close ----------------------
echo Launching the interface...
start "" "%EXE%"
echo Done. You can close this window.
