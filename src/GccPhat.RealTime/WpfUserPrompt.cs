using System.Windows;
using GccPhat.RealTime.Mvvm;

namespace GccPhat.RealTime;

/// <summary>WPF-backed <see cref="IUserPrompt"/>, wrapping <see cref="MessageBox"/>.</summary>
public sealed class WpfUserPrompt : IUserPrompt
{
    public bool ConfirmYesNo(string message, string title)
        => MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

    public void ShowWarning(string message, string title)
        => MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
}
