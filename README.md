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
- Mic geometry editor (circular or linear layout, mic count, diameter/spacing, optional center mic)
  mapped to capture channels and shared by the localizer and the beamformer.

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

**UI & DSP**

- Configuration lives in the main window; each tool — **Delay View**, **Localization map**,
  **Beamformer** — opens in its own focused window. Channel pairs are editable from both the Delay
  View and the Localization map; plot axes from the Delay View; beam mic selection from the Beamformer.
- Thread-safe, allocation-conscious DSP in `GccPhat.Core` (FFT, analyzer, SRP-PHAT, beamformer) with
  xUnit non-regression tests.

## How it works

```
[WASAPI capture thread]          [Analysis thread]                  [UI thread / WPF]
 WasapiCapture(device)   ──►  per-channel ring buffers  ──►  per active pair:           ──►  ScottPlot live chart
 de-interleave float32        (GccPhat.RealTime.Audio)        latest window → GccPhat        per-pair delay readout
                                                              → (delay ms, rms)
```

When beam listening is enabled, the analysis thread also feeds a weighted-overlap-add beamformer
that reads contiguous hop-sized blocks from the same ring buffers, steers and sums the selected
channels, and pushes the reconstructed mono stream to the chosen render device (NAudio `WasapiOut`).

The DSP lives in a reusable, dependency-free library, **`GccPhat.Core`**:

- `Fft2` — in-place radix-2 complex FFT (flat reusable buffers, allocation-free per call).
- `GccPhatAnalyzer` — instance-based, allocation-free, **thread-safe-per-instance** estimator.
  One analyzer is created per channel pair, so pairs can be analysed independently/in parallel.
- `SrpPhatLocalizer` — far-field SRP-PHAT azimuth search over the active pairs and array geometry.
- `Beamformer` — frequency-domain delay-and-sum with fractional steering delays and an optional
  spatial passband (the overlap-add windowing/streaming is driven by the real-time engine).

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
  GccPhat.Core/         DSP library (FFT + GCC-PHAT analyzer) — no UI/audio dependencies
  GccPhat.RealTime/     WPF app (WASAPI capture, analysis engine, ScottPlot UI)
tests/
  GccPhat.Core.Tests/   xUnit tests: bit-exact GCC-PHAT vs the gccphat CLI, plus SRP-PHAT localizer
```

## Build & run

Requirements: **.NET 8 SDK** (or newer) on **Windows** (WASAPI capture is Windows-only).

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

- **Multi-device capture** (phase 2): combine several separate input devices. Note that
  independent devices run on independent clocks, so cross-device correlation is subject to
  drift/offset and needs resampling/synchronisation — it will ship with clear warnings.
- Optional CSV logging of the real-time delay streams.
- **Sub-band beamforming**: steer only within the array's useful band while passing a full-band
  reference outside it, so listening stays wide-band without sacrificing spatial selectivity.

## Acknowledgments

- GCC-PHAT algorithm and reference C# implementation:
  [pmarmaroli/gccphat](https://github.com/pmarmaroli/gccphat).
- In-place complex FFT after Gerald T. Beauregard (MIT License).
- Plotting by [ScottPlot](https://scottplot.net); audio I/O by [NAudio](https://github.com/naudio/NAudio).

## License

MIT — see [LICENSE](LICENSE).
