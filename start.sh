#!/usr/bin/env bash
# ============================================================================
#  start.sh  -  Build and launch the GCC-PHAT Real-Time UI (Ubuntu/Linux).
#  Equivalent of start.bat, but for the Avalonia UI instead of the WPF one.
#  Run it from a terminal: ./start.sh   (chmod +x start.sh first if needed)
# ============================================================================
set -uo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")"

PROJ="src/GccPhat.RealTime.Avalonia/GccPhat.RealTime.Avalonia.csproj"
EXE="src/GccPhat.RealTime.Avalonia/bin/Release/net8.0/GccPhat.RealTime.Avalonia"
ASSETS="src/GccPhat.RealTime.Avalonia/bin/Release/net8.0/Assets"

pause_on_exit() {
    echo
    read -n 1 -s -r -p "Press any key to close this window..."
    echo
}
trap pause_on_exit EXIT

# --- Close any running instance so the build can overwrite its files ------
pkill -f "$EXE" >/dev/null 2>&1 || true

# --- Check the .NET SDK is available ---------------------------------------
if ! command -v dotnet >/dev/null 2>&1; then
    echo "[ERROR] .NET SDK not found."
    echo "        Install the .NET 8 SDK: https://learn.microsoft.com/dotnet/core/install/linux-ubuntu"
    exit 1
fi

# --- Sanity-check native libs Avalonia/PortAudio need at runtime -----------
if command -v ldconfig >/dev/null 2>&1; then
    ldconfig -p | grep -q libasound.so.2 || \
        echo "[WARN] libasound2 not found - install it (sudo apt install libasound2) or capture devices won't enumerate."
    ldconfig -p | grep -q libfontconfig.so.1 || \
        echo "[WARN] libfontconfig1 not found - install it (sudo apt install libfontconfig1) or the UI may fail to render."
fi

# --- YAMNet model: download once if not present -----------------------------
if [ ! -f "$ASSETS/yamnet.onnx" ]; then
    echo
    echo "[SETUP] YAMNet model not found. Downloading and converting (one-time, ~400 MB)..."
    echo "        Press Ctrl+C to skip and launch without classification."
    echo

    if ! command -v python3 >/dev/null 2>&1; then
        echo "[WARN] python3 not found on PATH - skipping YAMNet setup."
        echo "       Install Python 3.9-3.12 (e.g. sudo apt install python3 python3-pip) then re-run."
        echo
    else
        mkdir -p "$ASSETS"

        echo "[1/3] Installing tensorflow + tf2onnx via pip..."
        if ! python3 -m pip install --quiet --upgrade tensorflow tensorflow-hub tf2onnx; then
            echo "[WARN] pip install failed - skipping YAMNet setup."
            echo
            echo "       Diagnostics:"
            echo "         Python version : $(python3 --version 2>&1)"
            echo "         Architecture   : $(python3 -c 'import platform;print(platform.machine())' 2>&1)"
            echo
            echo "       TensorFlow requires 64-bit Python 3.9-3.12 on x86_64."
            echo "       Fix: install Python 3.11 or 3.12 (e.g. via apt or pyenv), then re-run."
            echo
        else
            echo "[2/3] Downloading YAMNet from TFHub and converting to ONNX via Python API..."
            ONNX_OUT="$ASSETS/yamnet.onnx"
            if ! python3 -c "import tensorflow_hub as hub, tensorflow as tf, tf2onnx; m=hub.load('https://tfhub.dev/google/yamnet/1'); sig=[tf.TensorSpec([None],tf.float32,'waveform')]; fn=tf.function(lambda w:m(w)[0],input_signature=sig); tf2onnx.convert.from_function(fn,input_signature=sig,opset=13,output_path=r'$ONNX_OUT'); print('Conversion OK')"; then
                echo "[WARN] YAMNet conversion failed - classification will be disabled."
            else
                echo "[3/3] Downloading class map CSV..."
                if ! curl -fsSL -o "$ASSETS/yamnet_class_map.csv" "https://raw.githubusercontent.com/tensorflow/models/master/research/audioset/yamnet/yamnet_class_map.csv"; then
                    echo "[WARN] Class map download failed - class names will use generic labels."
                fi
                echo "[SETUP] YAMNet ready."
            fi
            echo
        fi
    fi
fi

# --- Build (incremental, fast after the first run) --------------------------
echo "Building GccPhat.RealTime.Avalonia (Release)..."
if ! dotnet build "$PROJ" -c Release --nologo -v minimal; then
    echo
    echo "[ERROR] Build failed. See the messages above."
    exit 1
fi

if [ ! -f "$EXE" ]; then
    echo "[ERROR] Executable not found: $EXE"
    exit 1
fi
chmod +x "$EXE"

# --- Launch the UI detached so this window can close -------------------------
echo "Launching the interface..."
nohup "$EXE" >/dev/null 2>&1 & disown
echo "Done. You can close this window."
