using System;

namespace GccPhat.RealTime.Mvvm;

/// <summary>A running timer that ticks on the UI thread until stopped/disposed.</summary>
public interface IUiTimer : IDisposable
{
    void Start();
    void Stop();
}

/// <summary>Abstracts UI-thread marshalling so shared ViewModels don't depend on WPF's Dispatcher
/// (or Avalonia's) directly.</summary>
public interface IUiDispatcher
{
    /// <summary>Runs <paramref name="action"/> on the UI thread as soon as possible.</summary>
    void Post(Action action);

    /// <summary>Runs <paramref name="action"/> on the UI thread once, after <paramref name="delay"/>.</summary>
    void PostDelayed(Action action, TimeSpan delay);

    /// <summary>Creates a repeating UI-thread timer (not started).</summary>
    IUiTimer CreateRepeatingTimer(TimeSpan interval, Action tick);
}
