using System;
using Avalonia.Threading;
using GccPhat.RealTime.Mvvm;

namespace GccPhat.RealTime;

/// <summary>Avalonia-backed <see cref="IUiDispatcher"/>, wrapping <see cref="Dispatcher"/>/<see cref="DispatcherTimer"/>.</summary>
public sealed class AvaloniaDispatcher : IUiDispatcher
{
    public void Post(Action action) => Dispatcher.UIThread.Post(action);

    public void PostDelayed(Action action, TimeSpan delay)
    {
        var timer = new DispatcherTimer { Interval = delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        timer.Start();
    }

    public IUiTimer CreateRepeatingTimer(TimeSpan interval, Action tick) => new AvaloniaUiTimer(interval, tick);

    private sealed class AvaloniaUiTimer : IUiTimer
    {
        private readonly DispatcherTimer _timer;

        public AvaloniaUiTimer(TimeSpan interval, Action tick)
        {
            _timer = new DispatcherTimer { Interval = interval };
            _timer.Tick += (_, _) => tick();
        }

        public void Start() => _timer.Start();
        public void Stop() => _timer.Stop();
        public void Dispose() => _timer.Stop();
    }
}
