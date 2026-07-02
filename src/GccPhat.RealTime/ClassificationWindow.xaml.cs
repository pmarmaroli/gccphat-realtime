using System.Windows;
using GccPhat.RealTime.ViewModels;

namespace GccPhat.RealTime;

public partial class ClassificationWindow : Window
{
    public ClassificationWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
