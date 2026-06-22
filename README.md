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

- Enumerate active WASAPI capture endpoints (multichannel arrays, laptop mic, ...).
- Select one or more **channel pairs** (channel A ↔ channel B) to analyse simultaneously.
- Real-time GCC-PHAT delay estimation per pair, with a live **delay (ms) vs time** chart.
- Per-pair numeric readout of the current delay and band-limited RMS level.
- Configurable analysis window (FFT size), frequency band `[fmin, fmax]`, and update rate.

## How it works

```
[WASAPI capture thread]          [Analysis thread]                  [UI thread / WPF]
 WasapiCapture(device)   ──►  per-channel ring buffers  ──►  per active pair:           ──►  ScottPlot live chart
 de-interleave float32        (GccPhat.RealTime.Audio)        latest window → GccPhat        per-pair delay readout
                                                              → (delay ms, rms)
```

The DSP lives in a reusable, dependency-free library, **`GccPhat.Core`**:

- `Fft2` — in-place radix-2 complex FFT (flat reusable buffers, allocation-free per call).
- `GccPhatAnalyzer` — instance-based, allocation-free, **thread-safe-per-instance** estimator.
  One analyzer is created per channel pair, so pairs can be analysed independently/in parallel.

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
  GccPhat.Core.Tests/   xUnit non-regression tests (bit-exact vs the gccphat CLI)
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
- Peak-quality / confidence indicator to flag unreliable estimates (silence, no source).

## Acknowledgments

- GCC-PHAT algorithm and reference C# implementation:
  [pmarmaroli/gccphat](https://github.com/pmarmaroli/gccphat).
- In-place complex FFT after Gerald T. Beauregard (MIT License).
- Plotting by [ScottPlot](https://scottplot.net); audio I/O by [NAudio](https://github.com/naudio/NAudio).

## License

MIT — see [LICENSE](LICENSE).
