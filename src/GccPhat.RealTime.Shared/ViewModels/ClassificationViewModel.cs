using System;
using System.Collections.ObjectModel;
using GccPhat.RealTime.Analysis;
using GccPhat.RealTime.Mvvm;

namespace GccPhat.RealTime.ViewModels;

public sealed class ClassificationEntryViewModel
{
    public string Label { get; init; } = string.Empty;
    public float Score { get; init; }
    public string ScoreText => $"{Score * 100:F1}%";
    public double BarWidth => Math.Clamp(Score * 240.0, 2.0, 240.0);
}

public sealed class ClassificationViewModel : ObservableObject
{
    private string _statusText = "Waiting for model…";
    private string _channelText = "Ch 0";
    private string _updateTimeText = string.Empty;

    public ObservableCollection<ClassificationEntryViewModel> TopResults { get; } = new();

    public string StatusText
    {
        get => _statusText;
        set { if (SetProperty(ref _statusText, value)) OnPropertyChanged(nameof(HasStatus)); }
    }

    public bool HasStatus => !string.IsNullOrEmpty(_statusText);

    public string ChannelText
    {
        get => _channelText;
        set => SetProperty(ref _channelText, value);
    }

    public string UpdateTimeText
    {
        get => _updateTimeText;
        set => SetProperty(ref _updateTimeText, value);
    }

    public void UpdateResults(ClassificationResult[] results, int channel)
    {
        ChannelText = $"Ch {channel}";
        TopResults.Clear();
        foreach (ClassificationResult r in results)
            TopResults.Add(new ClassificationEntryViewModel { Label = r.Label, Score = r.Score });
        UpdateTimeText = $"Updated {DateTime.Now:HH:mm:ss.f}";
        if (results.Length > 0)
            StatusText = string.Empty;
    }
}
