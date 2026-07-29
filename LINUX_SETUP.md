# Running the Avalonia app on Linux

`start.sh` builds and launches the Avalonia UI (`src/GccPhat.RealTime.Avalonia`),
the Linux counterpart of `start.bat` / the WPF app. This note covers the setup
that is not obvious, and the failure modes that are silent.

Verified on Ubuntu 24.04, GNOME on Wayland.

---

## 1. .NET: you need a new SDK *and* the .NET 8 runtime

The app targets `net8.0`, but Avalonia 12.1's source generator needs a **newer
C# compiler than the SDK Ubuntu ships**. So the two halves come from different
places:

| Purpose | Needs | Where from |
| --- | --- | --- |
| **Building** | Roslyn >= 4.14, i.e. a recent SDK | `dotnet-install.sh` into `~/.dotnet` |
| **Running** | .NET 8 runtime | `apt install dotnet-runtime-8.0` |

Ubuntu's `dotnet-sdk-8.0` (8.0.1xx) bundles Roslyn **4.8**, and Avalonia's
analyzers ask for **4.14**. The build does not fail cleanly — the generator is
skipped, and you get a wall of errors for code it should have produced:

```
warning CS9057: The analyzer assembly ... references version '4.14.0.0' of the
                compiler, which is newer than the currently running version '4.8.0.0'.
error CS0103: The name 'InitializeComponent' does not exist in the current context
error CS0103: The name 'LevelPlot' does not exist in the current context
error CS0103: The name 'MapCanvas' does not exist in the current context
```

That `CS9057` warning is the real cause; the `CS0103` errors are downstream
noise. Install a current SDK for your user only (no sudo, no system packages):

```bash
curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
chmod +x /tmp/dotnet-install.sh
/tmp/dotnet-install.sh --channel 10.0 --install-dir "$HOME/.dotnet"
```

Then put it ahead of the system one, in `~/.bashrc`:

```bash
export PATH="$HOME/.dotnet:$PATH"
```

Open a new terminal (or `source ~/.bashrc`) and confirm:

```bash
dotnet --version    # expect 10.x, not 8.0.1xx
```

### Do NOT set `DOTNET_ROOT`

This is the trap. `DOTNET_ROOT` also controls where an **already-built**
`net8.0` executable looks for its runtime. Point it at `~/.dotnet` (which only
has .NET 10) and the app builds fine, then dies instantly on launch:

```
You must install or update .NET to run this application.
Framework: 'Microsoft.NETCore.App', version '8.0.0' (x64)
.NET location: /home/you/.dotnet
The following frameworks were found:
  10.0.10 at [/home/you/.dotnet/shared/Microsoft.NETCore.App]
```

You will almost certainly never see that message, because `start.sh` launches
the app with `>/dev/null 2>&1`. The symptom is just **"Done. You can close this
window." and no window ever appears.**

`PATH` alone is enough: the `dotnet` muxer finds its own SDK from its own
location, while the built app falls back to the system-wide .NET 8 runtime.

To debug a no-window launch, always bypass `start.sh` and run the binary
directly so you can see its output:

```bash
./src/GccPhat.RealTime.Avalonia/bin/Release/net8.0/GccPhat.RealTime.Avalonia
```

## 2. Native libraries

```bash
sudo apt install libasound2t64 libfontconfig1
```

`start.sh` warns if these are missing. On startup PortAudio enumerates every
ALSA backend, so a healthy run still prints a lot of noise — all harmless:

```
ALSA lib pcm_dsnoop.c:567:(snd_pcm_dsnoop_open) unable to open slave
jack server is not running or cannot be started
Cannot connect to server socket err = No such file or directory
```

Errors mentioning JACK, `/dev/dsp`, `cards.pcm.rear` or `a52` are just probes
for backends you do not have. They are not the reason something is failing.

## 3. YAMNet classification (optional)

The classifier needs two files that are **not** in the repo:

```
src/GccPhat.RealTime.Avalonia/Assets/yamnet.onnx            (~16 MB)
src/GccPhat.RealTime.Avalonia/Assets/yamnet_class_map.csv
```

`start.sh` tries to fetch them on first run, but on Debian/Ubuntu its
`pip install` is refused by [PEP 668](https://peps.python.org/pep-0668/):

```
error: externally-managed-environment
```

`start.sh` handles this gracefully — it warns, skips YAMNet, and launches the
app without classification. To actually enable it, build the model in a
virtualenv:

```bash
python3 -m venv ~/.venvs/gccphat-yamnet
source ~/.venvs/gccphat-yamnet/bin/activate

pip install tensorflow tensorflow-hub tf2onnx
pip install "setuptools<81"          # see note below

ASSETS=src/GccPhat.RealTime.Avalonia/Assets
mkdir -p "$ASSETS"

python -c "import tensorflow_hub as hub, tensorflow as tf, tf2onnx; \
m=hub.load('https://tfhub.dev/google/yamnet/1'); \
sig=[tf.TensorSpec([None],tf.float32,'waveform')]; \
fn=tf.function(lambda w:m(w)[0],input_signature=sig); \
tf2onnx.convert.from_function(fn,input_signature=sig,opset=13,output_path='$ASSETS/yamnet.onnx')"

curl -fsSL -o "$ASSETS/yamnet_class_map.csv" \
  https://raw.githubusercontent.com/tensorflow/models/master/research/audioset/yamnet/yamnet_class_map.csv
```

Re-run `start.sh` afterwards. It skips the download when `yamnet.onnx` already
exists, and the `.csproj` copies both files into the build output.

Two gotchas:

- **`setuptools<81` is required.** `tensorflow_hub` still does
  `from pkg_resources import parse_version`, and setuptools 81+ removed
  `pkg_resources`. Without the pin you get
  `ModuleNotFoundError: No module named 'pkg_resources'`.
- **Have ~3 GB free before you start.** TensorFlow unpacks to well over 1 GB.
  If it runs out of disk mid-install, pip leaves a **broken but
  installed-looking** package: `pip list` reports `tensorflow 2.21.0` while the
  import fails with `No module named 'tensorflow.python'`. Recover with
  `pip uninstall tensorflow && pip install --no-cache-dir tensorflow`, not by
  retrying the same install.

### Supported capture rates

Classification resamples to the 16 kHz YAMNet expects, and only supports source
rates that are an integer multiple of it — **16, 32, 48 and 96 kHz**.

At any other rate (44.1 kHz, or 141.12 kHz from an XMOS ultrasonic capture) the
resampler throws `NotSupportedException`, which the classification loop's
catch-all swallows. The status will read "YAMNet ready" and no results will ever
appear.

## 4. Quick reference

| Symptom | Cause |
| --- | --- |
| `CS0103: 'InitializeComponent' does not exist` (plus `CS9057`) | SDK too old — Roslyn 4.8 vs Avalonia's 4.14 |
| Build succeeds, "Done...", but no window | `DOTNET_ROOT` set to an install without the .NET 8 runtime |
| `error: externally-managed-environment` | PEP 668 — use a venv for the YAMNet setup |
| `No module named 'pkg_resources'` | setuptools >= 81 — pin `setuptools<81` |
| `No module named 'tensorflow.python'` | Truncated install (disk full) — uninstall and reinstall |
| Classification says ready but never produces results | Capture rate is not a multiple of 16 kHz |
| Lots of ALSA/JACK errors at startup | Normal PortAudio backend probing |

> Note: CI (`.github/workflows/ci.yml`) builds the WPF app and runs the core
> tests on Windows. It does not build the Avalonia project, so Linux-only
> breakage will not be caught there.
