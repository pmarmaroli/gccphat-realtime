using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using GccPhat.Core;
using GccPhat.RealTime.Audio;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace GccPhat.RealTime.Analysis;

/// <summary>
/// Drives real-time GCC-PHAT analysis: on a periodic tick it reads the latest window for every
/// active channel pair, estimates the delay, and raises <see cref="ResultsReady"/>.
///
/// One <see cref="GccPhatAnalyzer"/> instance is kept per pair (rebuilt when the configuration
/// changes), so analysis is allocation-free per tick. All callbacks run on the timer thread;
/// subscribers are responsible for marshalling to the UI thread.
/// </summary>
public sealed class RealTimeEngine : IDisposable
{
    private readonly object _gate = new();
    private readonly Stopwatch _clock = new();

    private MultichannelCapture? _capture;
    private Timer? _timer;
    private int _busy;

    private int _bufferSize = 4096;
    private int _fmin = 200;
    private int _fmax = 8000;
    private int _updateIntervalMs = 50;

    private ChannelPair[] _pairs = Array.Empty<ChannelPair>();
    private readonly Dictionary<ChannelPair, GccPhatAnalyzer> _analyzers = new();
    private double[] _frameA = Array.Empty<double>();
    private double[] _frameB = Array.Empty<double>();
    private int _configVersion;

    // SRP-PHAT localization (far-field azimuth) over the active pairs.
    private double[] _micX = Array.Empty<double>();
    private double[] _micY = Array.Empty<double>();
    private SrpPhatLocalizer? _localizer;
    private double[][] _srpCorr = Array.Empty<double[]>();

    public event Action<IReadOnlyList<PairResult>>? ResultsReady;
    public event Action<double[]>? ChannelLevelsReady;
    public event Action<SrpEstimate>? AzimuthReady;

    private const int LevelWindow = 2048;
    private readonly double[] _levelScratch = new double[LevelWindow];

    // Beamforming / listen prototype
    private Beamformer? _beamformer;
    private WasapiOut? _waveOut;
    private BufferedWaveProvider? _bufferedProvider;
    private RenderDeviceInfo? _renderDevice;
    private bool _listening;
    private double _beamAzimuthDeg;
    private double _beamGain = 1.0; // linear output gain applied to the beamformed stream
    private BeamformerMode _beamformerMode = BeamformerMode.DelayAndSum;
    private bool[] _beamChannels = Array.Empty<bool>(); // per-channel: include in the beam sum
    private double _beamFmin; // spatial passband (Hz); 0..0 disables limiting until set
    private double _beamFmax = double.PositiveInfinity;

    // Weighted overlap-add (WOLA) state: sqrt(Hann) analysis+synthesis window, 50% overlap (hop = N/2).
    private double[] _beamWindow = Array.Empty<double>(); // sqrt(Hann), length N
    private double[] _beamAcc = Array.Empty<double>();    // overlap-add accumulator, length N
    private long _beamReadPos;                            // absolute sample index of the next analysis frame
    private readonly object _audioGate = new();

    public bool IsRunning { get; private set; }

    public void Configure(int bufferSize, int fmin, int fmax, int updateIntervalMs)
    {
        lock (_gate)
        {
            _bufferSize = bufferSize;
            _fmin = fmin;
            _fmax = fmax;
            _updateIntervalMs = updateIntervalMs;
            _frameA = new double[bufferSize];
            _frameB = new double[bufferSize];
            _analyzers.Clear();
            _configVersion++;
            RebuildLocalizer();

            // If beamformer exists, rebuild to match new FFT size
            lock (_audioGate)
            {
                if (_waveOut != null && _capture != null)
                {
                    _beamformer = new Beamformer(_bufferSize, _capture.SampleRate);
                    _beamformer.SetBand(_beamFmin, _beamFmax);
                    BuildBeamWindowLocked(_bufferSize);
                    _beamReadPos = _capture.GetChannel(0).TotalWritten;
                    RebuildOutputLocked(_capture.SampleRate);
                }
            }
        }
    }

    public void SetPairs(IEnumerable<ChannelPair> pairs)
    {
        lock (_gate)
        {
            _pairs = new List<ChannelPair>(pairs).ToArray();
            RebuildLocalizer();
        }
    }

    /// <summary>Sets the microphone geometry (metres) used by SRP-PHAT, indexed by channel number.</summary>
    public void SetGeometry(double[] micX, double[] micY)
    {
        lock (_gate)
        {
            Volatile.Write(ref _micX, micX ?? Array.Empty<double>());
            Volatile.Write(ref _micY, micY ?? Array.Empty<double>());
            RebuildLocalizer();
        }
    }

    // Builds an SRP-PHAT localizer restricted to the CURRENTLY ACTIVE pairs; null when geometry
    // or pairs are insufficient. Must be called under _gate.
    private void RebuildLocalizer()
    {
        _localizer = null;
        _srpCorr = Array.Empty<double[]>();
        if (_pairs.Length == 0 || _micX.Length < 2 || _micX.Length != _micY.Length)
        {
            return;
        }
        var srpPairs = new (int a, int b)[_pairs.Length];
        for (int i = 0; i < _pairs.Length; i++)
        {
            if (_pairs[i].ChannelA >= _micX.Length || _pairs[i].ChannelB >= _micX.Length)
            {
                return; // pair references a channel without geometry
            }
            srpPairs[i] = (_pairs[i].ChannelA, _pairs[i].ChannelB);
        }
        int fs = _capture?.SampleRate ?? 48000;
        _localizer = new SrpPhatLocalizer(_micX, _micY, srpPairs, fs);
        _srpCorr = new double[_pairs.Length][];
        for (int i = 0; i < _pairs.Length; i++)
        {
            _srpCorr[i] = new double[_bufferSize];
        }
    }

    public void Start(MultichannelCapture capture)
    {
        lock (_gate)
        {
            _capture = capture;
            _frameA = new double[_bufferSize];
            _frameB = new double[_bufferSize];
            _analyzers.Clear();
            _clock.Restart();
            _capture.Start();
            RebuildLocalizer();

            // prepare beamformer if listening was requested earlier
            lock (_audioGate)
            {
                if (_listening && _capture is not null)
                {
                    _beamformer = new Beamformer(_bufferSize, _capture.SampleRate);
                    RebuildOutputLocked(_capture.SampleRate);
                }
            }

            _timer = new Timer(OnTick, null, _updateIntervalMs, _updateIntervalMs);
            IsRunning = true;
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            IsRunning = false;
            _timer?.Dispose();
            _timer = null;
            _capture?.Dispose();
            _capture = null;
            _analyzers.Clear();
        }

        // stop audio output
        lock (_audioGate)
        {
            DestroyOutputLocked();
            _beamformer = null;
            _listening = false;
        }
    }

    private void OnTick(object? state)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1)
        {
            return; // previous tick still running
        }

        try
        {
            PublishChannelLevels();

            List<PairResult>? results = AnalyzeOnce();
            if (results is { Count: > 0 })
            {
                ResultsReady?.Invoke(results);
            }

            // Beamform output runs independently of GCC-PHAT pairs.
            SynthesizeBeamOutput();
        }
        catch
        {
            // Never let an analysis exception tear down the timer thread.
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private List<PairResult>? AnalyzeOnce()
    {
        MultichannelCapture capture;
        ChannelPair[] pairs;
        int bufferSize, fs, fmin, fmax;
        double[] frameA, frameB;
        SrpPhatLocalizer? localizer;
        double[][] srpCorr;

        lock (_gate)
        {
            if (_capture is null || _pairs.Length == 0)
            {
                return null;
            }
            capture = _capture;
            pairs = _pairs;
            bufferSize = _bufferSize;
            fs = capture.SampleRate;
            fmin = _fmin;
            fmax = _fmax;
            frameA = _frameA;
            frameB = _frameB;
            localizer = _localizer;
            srpCorr = _srpCorr;
        }

        double time = _clock.Elapsed.TotalSeconds;
        var results = new List<PairResult>(pairs.Length);
        bool srpUsable = localizer is not null && srpCorr.Length == pairs.Length;

        for (int i = 0; i < pairs.Length; i++)
        {
            ChannelPair pair = pairs[i];
            if (pair.ChannelA >= capture.ChannelCount || pair.ChannelB >= capture.ChannelCount)
            {
                srpUsable = false;
                continue;
            }

            bool haveA = capture.GetChannel(pair.ChannelA).CopyLatest(frameA);
            bool haveB = capture.GetChannel(pair.ChannelB).CopyLatest(frameB);
            if (!haveA || !haveB)
            {
                results.Add(new PairResult(pair, time, 0, 0, 0, 0, 0, 0, 0, Valid: false));
                srpUsable = false;
                continue;
            }

            GccPhatAnalyzer analyzer = GetAnalyzer(pair, bufferSize, fs, fmin, fmax);
            DelayEstimate estimate = analyzer.Process(frameA, frameB);
            results.Add(new PairResult(
                pair, time, estimate.DelayMs, estimate.Rms,
                estimate.LevelA, estimate.LevelB, estimate.Coherence,
                estimate.ZeroLagCorrelation, estimate.DifferenceRatio, Valid: true));

            if (srpUsable)
            {
                analyzer.CrossCorrelation(frameA, frameB, srpCorr[i]);
            }
        }

        if (srpUsable && localizer is not null)
        {
            AzimuthReady?.Invoke(localizer.Estimate(srpCorr, bufferSize / 2));
        }

        return results;
    }

    // Synthesizes the beamformed mono stream with weighted overlap-add (WOLA): sqrt(Hann) window on
    // both analysis and synthesis, 50% overlap. Driven by sample availability (NOT the tick rate) so
    // frames are contiguous and the output reconstructs cleanly. Runs while listening, independently
    // of GCC-PHAT pairs (beamforming only needs the capture + geometry).
    private void SynthesizeBeamOutput()
    {
        lock (_audioGate)
        {
            if (!_listening || _beamformer is null || _capture is null || _bufferedProvider is null)
            {
                return;
            }

            int n = _beamWindow.Length;
            if (n == 0)
            {
                return; // window not built yet
            }
            int hop = n / 2;

            // which channels feed the beam (mask indexed by channel; default include none)
            int channelCount = _capture.ChannelCount;
            var included = new List<int>(channelCount);
            for (int c = 0; c < channelCount; c++)
            {
                if (c < _beamChannels.Length && _beamChannels[c])
                {
                    included.Add(c);
                }
            }
            if (included.Count == 0)
            {
                return; // no microphones selected for the beam
            }

            try
            {
                long available = long.MaxValue;
                foreach (int c in included)
                {
                    available = Math.Min(available, _capture.GetChannel(c).TotalWritten);
                }

                // If we have fallen far behind (e.g. after a stall), resync to bound latency.
                if (available - _beamReadPos > 8L * n)
                {
                    _beamReadPos = available - n;
                    Array.Clear(_beamAcc, 0, n);
                }

                // steering delays (samples) for the selected mics at the current azimuth
                var delays = new double[included.Count];
                var weights = new double[included.Count];
                if (!BuildBeamDesignLocked(included, delays, weights))
                {
                    return;
                }

                var frames = new double[included.Count][];
                for (int i = 0; i < included.Count; i++)
                {
                    frames[i] = new double[n];
                }
                var outFull = new double[n];
                byte[] buf = new byte[hop * 4];

                while (_beamReadPos + n <= available)
                {
                    bool ok = true;
                    for (int i = 0; i < included.Count; i++)
                    {
                        if (!_capture.GetChannel(included[i]).CopyRange(_beamReadPos, frames[i]))
                        {
                            ok = false;
                            break;
                        }
                        // sqrt(Hann) analysis window
                        double[] f = frames[i];
                        for (int k = 0; k < n; k++)
                        {
                            f[k] *= _beamWindow[k];
                        }
                    }
                    if (!ok)
                    {
                        // a frame fell out of the ring; resync and stop this pass
                        _beamReadPos = available - n;
                        Array.Clear(_beamAcc, 0, n);
                        break;
                    }

                    _beamformer.Process(frames, delays, weights, outFull);

                    // sqrt(Hann) synthesis window (→ Hann product, COLA at 50%) + overlap-add
                    for (int k = 0; k < n; k++)
                    {
                        _beamAcc[k] += outFull[k] * _beamWindow[k];
                    }

                    // emit the first hop samples, then slide the accumulator left by hop
                    for (int k = 0; k < hop; k++)
                    {
                        byte[] b = BitConverter.GetBytes((float)_beamAcc[k]);
                        Buffer.BlockCopy(b, 0, buf, k * 4, 4);
                    }
                    _bufferedProvider.AddSamples(buf, 0, buf.Length);

                    Array.Copy(_beamAcc, hop, _beamAcc, 0, n - hop);
                    Array.Clear(_beamAcc, n - hop, hop);
                    _beamReadPos += hop;
                }
            }
            catch
            {
                // swallow beamforming errors for robustness
            }
        }
    }

    // Builds the sqrt(Hann) WOLA window and resets the overlap-add state. Call under _audioGate.
    private void BuildBeamWindowLocked(int n)
    {
        _beamWindow = new double[n];
        for (int i = 0; i < n; i++)
        {
            // periodic Hann (denominator n) so sqrt(Hann)^2 satisfies COLA at 50% overlap
            _beamWindow[i] = Math.Sqrt(0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / n)));
        }
        _beamAcc = new double[n];
    }

    private void PublishChannelLevels()
    {
        MultichannelCapture? capture;
        lock (_gate)
        {
            capture = _capture;
        }
        if (capture is null)
        {
            return;
        }

        int channelCount = capture.ChannelCount;
        var levels = new double[channelCount];
        for (int c = 0; c < channelCount; c++)
        {
            if (capture.GetChannel(c).CopyLatest(_levelScratch))
            {
                double sum = 0.0;
                for (int i = 0; i < _levelScratch.Length; i++)
                {
                    double v = _levelScratch[i];
                    sum += v * v;
                }
                levels[c] = Math.Sqrt(sum / _levelScratch.Length);
            }
        }

        ChannelLevelsReady?.Invoke(levels);
    }

    private GccPhatAnalyzer GetAnalyzer(ChannelPair pair, int bufferSize, int fs, int fmin, int fmax)
    {
        if (!_analyzers.TryGetValue(pair, out GccPhatAnalyzer? analyzer))
        {
            analyzer = new GccPhatAnalyzer(bufferSize, fs, fmin, fmax);
            _analyzers[pair] = analyzer;
        }
        return analyzer;
    }

    public void StartListening()
    {
        lock (_audioGate)
        {
            _listening = true;
            if (_capture != null)
            {
                _beamformer = new Beamformer(_bufferSize, _capture.SampleRate);
                _beamformer.SetBand(_beamFmin, _beamFmax);
                BuildBeamWindowLocked(_bufferSize);
                _beamReadPos = _capture.GetChannel(0).TotalWritten; // start from "now"
                RebuildOutputLocked(_capture.SampleRate);
            }
        }
    }

    public void StopListening()
    {
        lock (_audioGate)
        {
            _listening = false;
            DestroyOutputLocked();
            _beamformer = null;
        }
    }

    public bool IsListening
    {
        get { lock (_audioGate) { return _listening; } }
    }

    /// <summary>
    /// Sets the steering azimuth (degrees, 0 = +X, CCW) used to compute per-microphone delays.
    /// </summary>
    public void SetBeamformingAzimuth(double deg)
    {
        lock (_audioGate)
        {
            _beamAzimuthDeg = deg;
        }
    }

    public void SetBeamformingMode(BeamformerMode mode)
    {
        lock (_audioGate)
        {
            _beamformerMode = mode;
        }
    }

    /// <summary>
    /// Sets which capture channels feed the beamformer sum, indexed by channel number
    /// (true = include). Channels outside the array, or unset, are excluded.
    /// </summary>
    public void SetBeamChannels(bool[] include)
    {
        lock (_audioGate)
        {
            _beamChannels = include ?? Array.Empty<bool>();
        }
    }

    /// <summary>Sets the linear output gain applied to the beamformed stream (1.0 = unity).</summary>
    public void SetBeamGain(double linearGain)
    {
        lock (_audioGate)
        {
            _beamGain = linearGain > 0.0 ? linearGain : 0.0;
        }
    }

    /// <summary>
    /// Sets the beamformer spatial passband (Hz). Pass (0, +inf) to disable band-limiting.
    /// Reapplied automatically whenever the beamformer is rebuilt.
    /// </summary>
    public void SetBeamBand(double fmin, double fmax)
    {
        lock (_audioGate)
        {
            _beamFmin = fmin;
            _beamFmax = fmax;
            _beamformer?.SetBand(fmin, fmax);
        }
    }

    public void SetRenderDevice(RenderDeviceInfo? renderDevice)
    {
        lock (_audioGate)
        {
            _renderDevice = renderDevice;
            if (_listening && _capture is not null)
            {
                RebuildOutputLocked(_capture.SampleRate);
            }
        }
    }

    private void RebuildOutputLocked(int sampleRate)
    {
        DestroyOutputLocked();

        var waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);
        int blockMs = Math.Max(1, (int)Math.Ceiling(_bufferSize * 1000.0 / sampleRate));
        int latencyMs = Math.Max(_updateIntervalMs, blockMs);
        int bufferMs = Math.Max(latencyMs * 4, blockMs * 2);

        _bufferedProvider = new BufferedWaveProvider(waveFormat)
        {
            BufferDuration = TimeSpan.FromMilliseconds(bufferMs),
            DiscardOnBufferOverflow = true
        };

        MMDevice? device = _renderDevice?.Device;
        _waveOut = device is null
            ? new WasapiOut(AudioClientShareMode.Shared, true, latencyMs)
            : new WasapiOut(device, AudioClientShareMode.Shared, true, latencyMs);
        _waveOut.Init(_bufferedProvider);
        _waveOut.Play();
    }

    private bool BuildBeamDesignLocked(List<int> included, double[] delays, double[] weights)
    {
        switch (_beamformerMode)
        {
            case BeamformerMode.DifferentialAuto:
                return BuildDifferentialBeamLocked(included, delays, weights);

            case BeamformerMode.DelayAndSum:
            default:
                return BuildDelayAndSumLocked(included, delays, weights);
        }
    }

    private bool BuildDelayAndSumLocked(List<int> included, double[] delays, double[] weights)
    {
        double[] micX = Volatile.Read(ref _micX);
        double[] micY = Volatile.Read(ref _micY);
        double theta = _beamAzimuthDeg * Math.PI / 180.0;
        double ux = Math.Cos(theta);
        double uy = Math.Sin(theta);
        const double speed = 343.0; // m/s
        double weight = _beamGain / included.Count;
        for (int i = 0; i < included.Count; i++)
        {
            int channel = included[i];
            double x = channel < micX.Length ? micX[channel] : 0.0;
            double y = channel < micY.Length ? micY[channel] : 0.0;
            delays[i] = -(x * ux + y * uy) * _capture!.SampleRate / speed;
            weights[i] = weight;
        }

        return true;
    }

    private bool BuildDifferentialBeamLocked(List<int> included, double[] delays, double[] weights)
    {
        double[] micX = Volatile.Read(ref _micX);
        double[] micY = Volatile.Read(ref _micY);
        var x = new double[included.Count];
        var y = new double[included.Count];
        for (int i = 0; i < included.Count; i++)
        {
            int channel = included[i];
            x[i] = channel < micX.Length ? micX[channel] : 0.0;
            y[i] = channel < micY.Length ? micY[channel] : 0.0;
        }

        if (!DifferentialBeamformerDesigner.TryBuildWeights(x, y, _beamAzimuthDeg, weights, out _))
        {
            return false;
        }

        Array.Clear(delays, 0, delays.Length);
        for (int i = 0; i < weights.Length; i++)
        {
            weights[i] *= _beamGain;
        }

        return true;
    }

    private void DestroyOutputLocked()
    {
        _bufferedProvider = null;
        if (_waveOut is null)
        {
            return;
        }

        _waveOut.Stop();
        _waveOut.Dispose();
        _waveOut = null;
    }

    public void Dispose() => Stop();
}
