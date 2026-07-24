using System;
using System.Windows;
using System.Windows.Threading;
using GccPhat.RealTime.Mvvm;

namespace GccPhat.RealTime;

/// <summary>WPF-backed <see cref="IUiDispatcher"/>, wrapping <see cref="Dispatcher"/>/<see cref="DispatcherTimer"/>.</summary>
public sealed class WpfDispatcher : IUiDispatcher
{
    public void Post(Action action) => Application.Current.Dispatcher.BeginInvoke(action);

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

    public IUiTimer CreateRepeatingTimer(TimeSpan interval, Action tick) => new WpfUiTimer(interval, tick);

    private sealed class WpfUiTimer : IUiTimer
    {
        private readonly DispatcherTimer _timer;

        public WpfUiTimer(TimeSpan interval, Action tick)
        {
            _timer = new DispatcherTimer { Interval = interval };
            _timer.Tick += (_, _) => tick();
        }

        public void Start() => _timer.Start();
        public void Stop() => _timer.Stop();
        public void Dispose() => _timer.Stop();
    }
}
