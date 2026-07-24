namespace GccPhat.RealTime.Mvvm;

/// <summary>Abstracts simple modal prompts so shared ViewModels don't depend on WPF's MessageBox
/// (or Avalonia's equivalent) directly.</summary>
public interface IUserPrompt
{
    bool ConfirmYesNo(string message, string title);
    void ShowWarning(string message, string title);
}
