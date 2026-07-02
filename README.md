# GCC-PHAT Real-Time — Microphone Array Delay Viewer

![Platform](https://img.shields.io/badge/platform-Windows-lightgrey)
![License](https://img.shields.io/badge/license-MIT-green)
![.NET](https://img.shields.io/badge/.NET-8.0-512BD4)

A Windows desktop app (WPF, .NET 8) that captures a **microphone array** (or any
multichannel / internal microphone) and shows, **in real time**, the **time delay**
between any pair of microphones using the **GCC-PHAT** algorithm (Generalized
Cross-Correlation with Phase Transform, Knapp & Carter, 1976).

Pick a capture device, add the channel pairs you want to compare, press **Start**, and
watch the inter-microphone delay evolve live as a source moves around the array.

## Features

**Capture & delay analysis**

- Enumerate active WASAPI capture endpoints (multichannel arrays, laptop mic, …), with automatic
  fallback to exclusive mode for arrays that expose more channels natively than the shared mix format.
- Select one or more **channel pairs** (channel A ↔ channel B) and estimate their GCC‑PHAT delay in
  real time, with a live **delay (ms) vs time** chart (ScottPlot DataLogger).
- Per-pair numeric readout: current delay, coherence/quality verdict, and band-limited RMS levels.
- Per-channel meters and a "blow on a mic" channel-identification helper.
- Configurable FFT/window size, analysis band (f_min–f_max), update rate, and X/Y plot scaling.

**Localization**

- SRP‑PHAT far-field azimuth estimator across the active pairs, shown live on a compass-style **array map**.
- **Polar power spectrum** overlay on the compass: the SRP-PHAT coarse scan power for every azimuth
  direction is rendered as a filled polar polygon, making directional ambiguities immediately visible.
- **Hemisphere heat map** (toggle): even for a planar array, the app computes the SRP-PHAT response
  over the full upper hemisphere (azimuth × elevation) and renders it as a colour-mapped overlay using
  an azimuthal-equidistant projection (zenith = centre, horizon = edge). Implemented by scaling the
  existing coarse delay LUT by cos(elevation) — no additional LUT needed.
- **Array map** always shows mic positions (colour-coded by channel) and the pair lines between them,
  regardless of the signal threshold. Pairs used for localization are drawn solid; inactive pairs dashed.
- Mic geometry editor (circular or linear layout, mic count, diameter/spacing, optional center mic)
  mapped to capture channels and shared by the localizer and the beamformer.

**Combined Localization (two arrays)**

- Triangulates a single 2D (x, y) source fix from two independently running analysis sessions'
  live SRP-PHAT azimuths, given the physical offset between the two arrays (Δx, Δy, Δz cm).
- **Array sync calibration**: bring the two arrays' closest microphones together, clap, and hit
  "Measure sync" — the measured GCC-PHAT delay between them is ≈ the clock/stream offset between
  the two independently-clocked capture devices (real acoustic delay ≈ 0 when the mics are
  touching). No continuous drift tracking — recalibrating requires bringing the arrays back
  together, which conflicts with the real experiment — so it's a one-shot manual measurement with
  a "Recalibrate" button to redo it any time.
- Once calibrated, a throttled live **cross-check** compares the predicted near-field TDOA (from
  known mic geometry + the current triangulated fix) against the measured, clock-corrected TDOA on
  the same mic pair, shown as a green/amber readout — it corroborates (or flags disagreement with)
  the triangulated fix without recomputing it.
- Opens as its own window; combines any two currently open, running analysis sessions.

**Beamformer (steerable delay-and-sum listening)**

- Frequency-domain delay-and-sum with fractional steering delays, reconstructed by **weighted
  overlap-add** (sqrt-Hann analysis + synthesis window, 50 % overlap) for clean, click-free audio.
- Selectable beamforming mode: classic **delay-and-sum** or an **auto-order differential**
  beamformer whose order follows the selected microphones and their projected geometry.
- Steerable azimuth, per-microphone **include/exclude** in the beam, **output gain** boost, and a
  selectable render (speaker) device.
- **Automatic spatial passband** derived from the geometry — limits the output to
  `[c/(2·aperture), c/(2·min-spacing)]`, the band where the array actually has directivity and does
  not alias (toggleable).

**Classification**

- **YAMNet** 521-class sound event classifier (MobileNet-based, Google AudioSet) running via
  **ONNX Runtime** on a dedicated background thread — no impact on the DSP pipeline.
- Feeds a 1-second window from any capture channel, resampled from the device rate to 16 kHz with a
  windowed-sinc FIR anti-alias filter. Fires every ~480 ms.
- Displays the **top-10 predicted labels** with confidence scores and horizontal bar indicators.
- The model is downloaded and converted automatically on first launch via `start.bat`
  (requires Python; `start.bat` runs `pip install tensorflow tf2onnx` then `tf2onnx.convert`).
  Subsequent launches skip the setup entirely.
- Graceful degradation: if the model is absent or Python is unavailable, all other features
  continue working normally and the classification panel shows setup instructions.

**UI & DSP**

- Configuration lives in the main window; each tool — **Delay View**, **Localization map**,
  **Beamformer**, **Classification** — opens in its own focused window. Channel pairs are editable
  from both the Delay View and the Localization map; plot axes from the Delay View; beam mic
  selection from the Beamformer.
- Thread-safe, allocation-conscious DSP in `GccPhat.Core` (FFT, analyzer, SRP-PHAT, beamformer) with
  xUnit non-regression tests.

## How it works

```
[WASAPI capture thread]          [Analysis thread]                  [UI thread / WPF]
 WasapiCapture(device)   ──►  per-channel ring buffers  ──►  per active pair:           ──►  ScottPlot live chart
 de-interleave float32        (GccPhat.RealTime.Audio)        latest window → GccPhat        per-pair delay readout
                                                              → (delay ms, rms)
                                                              └─► SRP-PHAT localizer     ──►  array map + compass
                                                                  → azimuth + spectrum        polar overlay
                                                                  → hemisphere powers         heat map overlay

[YAMNet thread]  (every 480 ms)
 ring buffer channel N ──► FIR downsample to 16 kHz ──► ONNX Runtime (yamnet.onnx) ──►  top-10 labels + scores
```

When beam listening is enabled, the analysis thread also feeds a weighted-overlap-add beamformer
that reads contiguous hop-sized blocks from the same ring buffers, steers and sums the selected
channels, and pushes the reconstructed mono stream to the chosen render device (NAudio `WasapiOut`).

**Combined Localization** is a lightweight coordinator on top of two independent sessions — it
watches each session's live `AzimuthDeg` and triangulates, never fusing raw audio. Sync
calibration and the cross-check are the one exception: they borrow each session's
`RealTimeEngine` for short, explicit `TryCopyLatestChannel` reads (see
`CombinedLocalizationViewModel`), not a continuously-running joint audio pipeline — there is
deliberately no attempt at sample-accurate joint beamforming across the two devices' independent
clocks.

The DSP lives in a reusable, dependency-free library, **`GccPhat.Core`**:

- `Fft2` — in-place radix-2 complex FFT (flat reusable buffers, allocation-free per call).
- `GccPhatAnalyzer` — instance-based, allocation-free, **thread-safe-per-instance** estimator.
  One analyzer is created per channel pair, so pairs can be analysed independently/in parallel.
- `SrpPhatLocalizer` — far-field SRP-PHAT azimuth search over the active pairs and array geometry.
  Exposes the coarse scan power buffer and a `ScanHemisphere()` method for the 2-D heat map.
- `Beamformer` — frequency-domain delay-and-sum with fractional steering delays and an optional
  spatial passband (the overlap-add windowing/streaming is driven by the real-time engine).
- `NearFieldTdoa` — near-field point-source TDOA prediction, used by the Combined Localization
  sync cross-check to compare a predicted delay against a measured, clock-corrected one.

`GccPhat.Core` is a faithful, instance-based port of the
[`gccphat`](https://github.com/pmarmaroli/gccphat) CLI algorithm and reproduces its
results bit-for-bit (covered by a non-regression test against `stereo_noise.wav`).

## Sign convention

Same as the original `gccphat` CLI:

- a **negative** delay means channel B (2) is delayed relative to channel A (1),
- a **positive** delay means channel A (1) is delayed relative to channel B (2).

## Project layout

```
src/
  GccPhat.Core/             DSP library (FFT, GCC-PHAT, SRP-PHAT, beamformer, near-field TDOA) — no UI/audio deps
  GccPhat.RealTime/
    Analysis/               RealTimeEngine, ChannelPair, AudioResampler, YamNetClassifier, SyncCalibration
    Audio/                  MultichannelCapture + ChannelRingBuffer (WASAPI, NAudio)
    ViewModels/             MainViewModel, PairViewModel, ClassificationViewModel, CombinedLocalizationViewModel, …
    Assets/                 yamnet.onnx + yamnet_class_map.csv (downloaded by start.bat)
    ArrayMapWindow          Compass + mic geometry + polar spectrum + hemisphere heat map
    CombinedLocalizationWindow  Two-array triangulated fix + sync calibration + cross-check
    ClassificationWindow    Top-10 sound event results with score bars
    BeamformerWindow        Beam steering + channel selection + passband toggle
    DelayViewWindow         Live ScottPlot delay chart + per-pair readouts
tests/
  GccPhat.Core.Tests/       xUnit tests: bit-exact GCC-PHAT vs the gccphat CLI, plus SRP-PHAT and near-field TDOA
```

## Build & run

Requirements: **.NET 8 SDK** (or newer) on **Windows** (WASAPI capture is Windows-only).

### Quickstart (double-click)

```
start.bat
```

The script will:
1. Kill any running instance of the app.
2. Download and convert the YAMNet ONNX model on first launch (requires Python 3.9+;
   subsequent launches skip this step).
3. Build the project in Release mode (`dotnet build`).
4. Launch `GccPhat.RealTime.exe`.

The console window stays open so you can read any error messages.

### Manual build

```bash
git clone https://github.com/pmarmaroli/gccphat-realtime.git
cd gccphat-realtime

# run the tests (verifies the DSP matches the gccphat CLI)
dotnet test tests/GccPhat.Core.Tests/GccPhat.Core.Tests.csproj -c Release

# run the app
dotnet run --project src/GccPhat.RealTime/GccPhat.RealTime.csproj -c Release
```

### Self-contained single-file build (no .NET install required to run)

```bash
dotnet publish src/GccPhat.RealTime/GccPhat.RealTime.csproj -c Release -r win-x64 ^
    --self-contained true -p:PublishSingleFile=true -o publish
```

### YAMNet model (manual setup)

If Python is not available, set up the model manually and place both files in the
`Assets/` folder next to the executable:

```bash
pip install tensorflow tensorflow-hub tf2onnx
python -c "import tensorflow_hub as hub, tensorflow as tf, tf2onnx; m=hub.load('https://tfhub.dev/google/yamnet/1'); sig=[tf.TensorSpec([None],tf.float32,'waveform')]; cf=tf.function(lambda w:m(w)[0],input_signature=sig).get_concrete_function(); tf2onnx.convert.from_function(cf,input_signature=sig,opset=13,output_path='yamnet.onnx')"
curl -o yamnet_class_map.csv https://raw.githubusercontent.com/tensorflow/models/master/research/audioset/yamnet/yamnet_class_map.csv
```

## Choosing the analysis window

The window (FFT size) must be a power of two and large enough to contain the maximum
expected delay between the two microphones. As a rule of thumb:

```
max delay (samples) = ceil(sampleRate * maxDelayMs / 1000)
window              = next power of two ≥ max delay (samples)
```

A larger window resolves larger delays and is more robust to noise, but reduces the
time resolution of the live plot. See the
[buffer size calculator](https://pmarmaroli.github.io/bufferSizeCalculator.html).

## Roadmap

- **Combined Beamforming**: a single coherent delay-and-sum beam across both arrays' microphones,
  rather than two independently-steered beams. Needs continuous clock-drift correction beyond the
  one-shot sync calibration already shipped for Combined Localization — deferred until that's
  proven necessary; the sync-offset measurement it would build on already exists.
- Optional CSV logging of the real-time delay streams.
- **Sub-band beamforming**: steer only within the array's useful band while passing a full-band
  reference outside it, so listening stays wide-band without sacrificing spatial selectivity.

## Acknowledgments

- GCC-PHAT algorithm and reference C# implementation:
  [pmarmaroli/gccphat](https://github.com/pmarmaroli/gccphat).
- In-place complex FFT after Gerald T. Beauregard (MIT License).
- Plotting by [ScottPlot](https://scottplot.net); audio I/O by [NAudio](https://github.com/naudio/NAudio).
- Sound event classification by [YAMNet](https://tfhub.dev/google/yamnet/1) (Google, Apache 2.0),
  served via [ONNX Runtime](https://onnxruntime.ai) for .NET.

## License

MIT — see [LICENSE](LICENSE).
