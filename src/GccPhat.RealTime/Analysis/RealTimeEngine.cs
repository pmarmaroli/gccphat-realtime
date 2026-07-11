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

    private ICaptureSource? _capture;
    private Timer? _timer;
    private int _busy;

    private int _bufferSize = 4096;
    private int _fmin = 200;
    private int _fmax = 8000;

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
    private int[] _localizerPairIndices = Array.Empty<int>();
    private bool _hemisphereMode;
    private const double HemiElStepDeg = 5.0;

    public event Action<IReadOnlyList<PairResult>>? ResultsReady;
    public event Action<double[]>? ChannelLevelsReady;
    public event Action<SrpEstimate>? AzimuthReady;
    public event Action<ClassificationResult[]>? ClassificationReady;

    // YAMNet classification (runs on its own background thread).
    private YamNetClassifier? _classifier;
    private int _classChannel;
    private volatile bool _classRunning;
    private Thread? _classThread;
    private const int ClassWindowSeconds = 1; // 1 s input → 16 000 samples at 16 kHz
    private const int ClassHopMs = 480;       // fire every half-patch

    private const int LevelWindow = 2048;
    private readonly double[] _levelScratch = new double[LevelWindow];

    // Activity gate: downstream analysis/beamforming/classification only run while the loudest
    // channel is at or above this threshold. NegativeInfinity means the gate is always open.
    private double _levelThresholdDb = double.NegativeInfinity;
    private volatile bool _levelGateOpen = true;

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

    /// <summary>Capture sample rate (Hz) of the currently running session, or 0 if not running.</summary>
    public int SampleRate
    {
        get { lock (_gate) { return _capture?.SampleRate ?? 0; } }
    }

    /// <summary>
    /// Copies the most recent <c>dest.Length</c> samples of the given capture channel into
    /// <paramref name="dest"/>. Returns false if capture isn't running, the channel index is out of
    /// range, or not enough samples have been captured yet. Safe to call from any thread concurrently
    /// with the engine's own analysis tick (the underlying ring buffer is lock-guarded) — intended
    /// for one-shot UI-triggered reads (e.g. array sync calibration), not per-tick analysis.
    /// </summary>
    public bool TryCopyLatestChannel(int channel, double[] dest)
    {
        ICaptureSource? capture;
        lock (_gate) { capture = _capture; }
        if (capture is null || channel < 0 || channel >= capture.ChannelCount)
        {
            return false;
        }
        return capture.GetChannel(channel).CopyLatest(dest);
    }

    public void Configure(int bufferSize, int fmin, int fmax)
    {
        lock (_gate)
        {
            _bufferSize = bufferSize;
            _fmin = fmin;
            _fmax = fmax;
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
        _localizerPairIndices = Array.Empty<int>();
        if (_pairs.Length < 1 || _micX.Length < 2 || _micX.Length != _micY.Length)
        {
            return;
        }

        var srpPairs = new List<(int a, int b)>(_pairs.Length);
        var pairIndices = new List<int>(_pairs.Length);
        var seenBaselines = new HashSet<(int A, int B)>();
        for (int i = 0; i < _pairs.Length; i++)
        {
            if (_pairs[i].ChannelA >= _micX.Length || _pairs[i].ChannelB >= _micX.Length)
            {
                continue;
            }

            if (double.IsNaN(_micX[_pairs[i].ChannelA]) || double.IsNaN(_micY[_pairs[i].ChannelA])
                || double.IsNaN(_micX[_pairs[i].ChannelB]) || double.IsNaN(_micY[_pairs[i].ChannelB]))
            {
                continue;
            }

            if (!seenBaselines.Add(_pairs[i].UnorderedKey))
            {
                continue;
            }

            srpPairs.Add((_pairs[i].ChannelA, _pairs[i].ChannelB));
            pairIndices.Add(i);
        }

        if (srpPairs.Count < 1)
        {
            return;
        }

        int fs = _capture?.SampleRate ?? 48000;
        var localizer = new SrpPhatLocalizer(_micX, _micY, srpPairs.ToArray(), fs);

        // A single pair only yields a usable estimate when the array is collinear: the localizer
        // then restricts its search to the front hemisphere, resolving the mirror ambiguity the
        // same way the UI's front/back ambiguity handling does. For 2+ pairs, or a non-collinear
        // array reduced to just 1 pair, don't build a localizer — the result would be an unflagged
        // coin-flip between two mirror-symmetric directions.
        if (srpPairs.Count < 2 && !localizer.HasFrontBackAmbiguity)
        {
            return;
        }

        _localizer = localizer;
        _localizerPairIndices = pairIndices.ToArray();
        _srpCorr = new double[srpPairs.Count][];
        for (int i = 0; i < srpPairs.Count; i++)
        {
            _srpCorr[i] = new double[_bufferSize];
        }
    }

    public void Start(ICaptureSource capture)
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

            // Tick once per window's worth of audio, so each estimate is a fresh, essentially
            // non-overlapping block rather than a sliding read at an independently-chosen cadence.
            int intervalMs = Math.Max(1, (int)Math.Round(_bufferSize * 1000.0 / capture.SampleRate));
            _timer = new Timer(OnTick, null, intervalMs, intervalMs);
            IsRunning = true;
        }

        bool classifierReady;
        lock (_gate) { classifierReady = _classifier is { IsAvailable: true }; }
        if (classifierReady) EnsureClassThreadRunning();
    }

    public void Stop()
    {
        StopClassThread();

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
            double[] levels = PublishChannelLevels();
            _levelGateOpen = IsLevelGateOpen(levels);

            if (_levelGateOpen)
            {
                List<PairResult>? results = AnalyzeOnce();
                if (results is { Count: > 0 })
                {
                    ResultsReady?.Invoke(results);
                }

                // Beamform output runs independently of GCC-PHAT pairs.
                SynthesizeBeamOutput();
            }
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
        ICaptureSource capture;
        ChannelPair[] pairs;
        int bufferSize, fs, fmin, fmax;
        double[] frameA, frameB;
        SrpPhatLocalizer? localizer;
        double[][] srpCorr;
        int[] localizerPairIndices;

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
            localizerPairIndices = _localizerPairIndices;
        }

        bool hemisphereMode;
        lock (_gate) { hemisphereMode = _hemisphereMode; }

        double time = _clock.Elapsed.TotalSeconds;
        var results = new List<PairResult>(pairs.Length);
        bool srpUsable = localizer is not null
                         && srpCorr.Length == localizerPairIndices.Length
                         && localizerPairIndices.Length >= 1;
        int srpPairSlot = 0;

        for (int i = 0; i < pairs.Length; i++)
        {
            ChannelPair pair = pairs[i];
            bool pairUsedByLocalizer = srpUsable
                                       && srpPairSlot < localizerPairIndices.Length
                                       && localizerPairIndices[srpPairSlot] == i;
            if (pair.ChannelA >= capture.ChannelCount || pair.ChannelB >= capture.ChannelCount)
            {
                if (pairUsedByLocalizer)
                {
                    srpUsable = false;
                }
                continue;
            }

            bool haveA = capture.GetChannel(pair.ChannelA).CopyLatest(frameA);
            bool haveB = capture.GetChannel(pair.ChannelB).CopyLatest(frameB);
            if (!haveA || !haveB)
            {
                results.Add(new PairResult(pair, time, 0, 0, 0, 0, 0, 0, 0, Valid: false));
                if (pairUsedByLocalizer)
                {
                    srpUsable = false;
                }
                continue;
            }

            GccPhatAnalyzer analyzer = GetAnalyzer(pair, bufferSize, fs, fmin, fmax);
            DelayEstimate estimate = analyzer.Process(frameA, frameB);
            results.Add(new PairResult(
                pair, time, estimate.DelayMs, estimate.Rms,
                estimate.LevelA, estimate.LevelB, estimate.Coherence,
                estimate.ZeroLagCorrelation, estimate.DifferenceRatio, Valid: true));

            if (pairUsedByLocalizer)
            {
                analyzer.CrossCorrelation(frameA, frameB, srpCorr[srpPairSlot]);
                srpPairSlot++;
            }
        }

        if (srpUsable && localizer is not null && srpPairSlot == localizerPairIndices.Length)
        {
            var coarsePowers = new double[localizer.CoarseCount];
            SrpEstimate estimate = localizer.Estimate(srpCorr, bufferSize / 2, coarsePowers);

            if (hemisphereMode)
            {
                int nEl = (int)(90.0 / HemiElStepDeg);
                var hemiPowers = new double[nEl, localizer.CoarseCount];
                localizer.ScanHemisphere(srpCorr, bufferSize / 2, hemiPowers, HemiElStepDeg);
                estimate = estimate with { HemispherePowers = hemiPowers, HemiElStepDeg = HemiElStepDeg };
            }

            AzimuthReady?.Invoke(estimate);
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

    private double[] PublishChannelLevels()
    {
        ICaptureSource? capture;
        lock (_gate)
        {
            capture = _capture;
        }
        if (capture is null)
        {
            return Array.Empty<double>();
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
        return levels;
    }

    /// <summary>
    /// Sets the activity gate threshold (dBFS, loudest channel). While the loudest channel is
    /// below this level, delay estimation, localization, beamforming, and classification are all
    /// skipped. Pass <see cref="double.NegativeInfinity"/> to always run (gate disabled).
    /// </summary>
    public void SetLevelThresholdDb(double thresholdDb) => Volatile.Write(ref _levelThresholdDb, thresholdDb);

    private bool IsLevelGateOpen(double[] levels)
    {
        double thresholdDb = Volatile.Read(ref _levelThresholdDb);
        if (double.IsNegativeInfinity(thresholdDb))
        {
            return true;
        }

        double maxRms = 0.0;
        for (int i = 0; i < levels.Length; i++)
        {
            if (levels[i] > maxRms)
            {
                maxRms = levels[i];
            }
        }

        double maxDb = maxRms > 1e-7 ? 20.0 * Math.Log10(maxRms) : double.NegativeInfinity;
        return maxDb >= thresholdDb;
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

    public void SetHemisphereMode(bool enabled)
    {
        lock (_gate) { _hemisphereMode = enabled; }
    }

    /// <summary>
    /// Attach a YAMNet classifier. Pass null to disable. The engine starts/stops its
    /// classification thread automatically when capture is running.
    /// </summary>
    public void SetClassifier(YamNetClassifier? classifier, int channel = 0)
    {
        lock (_gate) { _classifier = classifier; _classChannel = channel; }
        bool shouldRun;
        lock (_gate) { shouldRun = classifier is { IsAvailable: true } && IsRunning; }
        if (shouldRun) EnsureClassThreadRunning();
        else StopClassThread();
    }

    private void EnsureClassThreadRunning()
    {
        if (_classRunning) return;
        _classRunning = true;
        _classThread = new Thread(ClassificationLoop)
        {
            IsBackground = true,
            Name = "YAMNet-infer",
            Priority = ThreadPriority.BelowNormal
        };
        _classThread.Start();
    }

    private void StopClassThread()
    {
        _classRunning = false;
        _classThread = null;
    }

    private void ClassificationLoop()
    {
        while (_classRunning)
        {
            Thread.Sleep(ClassHopMs);

            if (!_levelGateOpen) continue;

            YamNetClassifier? classifier;
            int channel;
            ICaptureSource? capture;
            int sampleRate;
            lock (_gate)
            {
                classifier = _classifier;
                channel = _classChannel;
                capture = _capture;
                sampleRate = _capture?.SampleRate ?? 0;
            }

            if (classifier is null || !classifier.IsAvailable || capture is null || sampleRate == 0) continue;
            if (channel >= capture.ChannelCount) continue;

            int windowSamples = sampleRate * ClassWindowSeconds;
            var scratch = new double[windowSamples];
            if (!capture.GetChannel(channel).CopyLatest(scratch)) continue;

            try
            {
                float[] audio16k = AudioResampler.ResampleTo16kHz(scratch, sampleRate);
                ClassificationResult[] results = classifier.Classify(audio16k);
                ClassificationReady?.Invoke(results);
            }
            catch
            {
                // Never let an inference error crash the classification thread.
            }
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
        int bufferMs = blockMs * 4;

        _bufferedProvider = new BufferedWaveProvider(waveFormat)
        {
            BufferDuration = TimeSpan.FromMilliseconds(bufferMs),
            DiscardOnBufferOverflow = true
        };

        MMDevice? device = _renderDevice?.Device;
        _waveOut = device is null
            ? new WasapiOut(AudioClientShareMode.Shared, true, blockMs)
            : new WasapiOut(device, AudioClientShareMode.Shared, true, blockMs);
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
        double weight = _beamGain / included.Count;
        for (int i = 0; i < included.Count; i++)
        {
            int channel = included[i];
            double x = channel < micX.Length ? micX[channel] : 0.0;
            double y = channel < micY.Length ? micY[channel] : 0.0;
            delays[i] = BeamPatternCalculator.SteeringDelaySeconds(x, y, _beamAzimuthDeg) * _capture!.SampleRate;
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
