using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using GccPhat.RealTime.Mvvm;

namespace GccPhat.RealTime;

/// <summary>Avalonia-backed <see cref="IUserPrompt"/>, using <see cref="ConfirmDialog"/>.
/// <see cref="IUserPrompt"/> is a synchronous interface (matching the WPF MessageBox it replaces),
/// but Avalonia dialogs are async — pump the dispatcher until the dialog task completes rather
/// than blocking the UI thread outright.</summary>
public sealed class AvaloniaUserPrompt : IUserPrompt
{
    public bool ConfirmYesNo(string message, string title) => ShowModal(message, title, yesNo: true);

    public void ShowWarning(string message, string title) => ShowModal(message, title, yesNo: false);

    private static bool ShowModal(string message, string title, bool yesNo)
    {
        Window? owner = FindOwnerWindow();
        if (owner is null)
        {
            return false; // no window to own the dialog; safe default (don't proceed)
        }

        var dialog = new ConfirmDialog(message, title, yesNo);
        var task = dialog.ShowDialog<bool>(owner);
        while (!task.IsCompleted)
        {
            Dispatcher.UIThread.RunJobs();
        }
        return task.GetAwaiter().GetResult();
    }

    private static Window? FindOwnerWindow()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return null;
        }
        return desktop.Windows.FirstOrDefault(w => w.IsActive) ?? desktop.MainWindow;
    }
}
