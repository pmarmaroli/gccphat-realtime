using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using GccPhat.Core;
using GccPhat.RealTime.Audio;

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

    public event Action<IReadOnlyList<PairResult>>? ResultsReady;

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
        }
    }

    public void SetPairs(IEnumerable<ChannelPair> pairs)
    {
        lock (_gate)
        {
            _pairs = new List<ChannelPair>(pairs).ToArray();
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
    }

    private void OnTick(object? state)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1)
        {
            return; // previous tick still running
        }

        try
        {
            List<PairResult>? results = AnalyzeOnce();
            if (results is { Count: > 0 })
            {
                ResultsReady?.Invoke(results);
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
        MultichannelCapture capture;
        ChannelPair[] pairs;
        int bufferSize, fs, fmin, fmax;
        double[] frameA, frameB;

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
        }

        double time = _clock.Elapsed.TotalSeconds;
        var results = new List<PairResult>(pairs.Length);

        foreach (ChannelPair pair in pairs)
        {
            if (pair.ChannelA >= capture.ChannelCount || pair.ChannelB >= capture.ChannelCount)
            {
                continue;
            }

            bool haveA = capture.GetChannel(pair.ChannelA).CopyLatest(frameA);
            bool haveB = capture.GetChannel(pair.ChannelB).CopyLatest(frameB);
            if (!haveA || !haveB)
            {
                results.Add(new PairResult(pair, time, 0, 0, 0, 0, 0, 0, 0, Valid: false));
                continue;
            }

            GccPhatAnalyzer analyzer = GetAnalyzer(pair, bufferSize, fs, fmin, fmax);
            DelayEstimate estimate = analyzer.Process(frameA, frameB);
            results.Add(new PairResult(
                pair, time, estimate.DelayMs, estimate.Rms,
                estimate.LevelA, estimate.LevelB, estimate.Coherence,
                estimate.ZeroLagCorrelation, estimate.DifferenceRatio, Valid: true));
        }

        return results;
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

    public void Dispose() => Stop();
}
