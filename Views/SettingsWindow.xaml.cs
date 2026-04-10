using System.Windows;
using GearBoardBridge.ViewModels;

namespace GearBoardBridge.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.SettingsSaved += Close;
    }
}
