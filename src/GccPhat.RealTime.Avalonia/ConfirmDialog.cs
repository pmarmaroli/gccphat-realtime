using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace GccPhat.RealTime;

/// <summary>Minimal Yes/No or OK modal dialog — Avalonia has no built-in MessageBox.</summary>
internal sealed class ConfirmDialog : Window
{
    public ConfirmDialog(string message, string title, bool yesNo)
    {
        Title = title;
        Width = 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        if (Application.Current?.TryFindResource("BgBrush", out object? bg) == true && bg is IBrush bgBrush)
        {
            Background = bgBrush;
        }

        var messageText = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0),
            Spacing = 8
        };

        if (yesNo)
        {
            var yes = new Button { Content = "Yes" };
            yes.Click += (_, _) => Close(true);
            var no = new Button { Content = "No" };
            no.Click += (_, _) => Close(false);
            buttons.Children.Add(yes);
            buttons.Children.Add(no);
        }
        else
        {
            var ok = new Button { Content = "OK" };
            ok.Click += (_, _) => Close(true);
            buttons.Children.Add(ok);
        }

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Children = { messageText, buttons }
        };
    }
}
