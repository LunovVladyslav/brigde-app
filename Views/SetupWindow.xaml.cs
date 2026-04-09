using System.Windows;

namespace GearBoardBridge.Views;

public partial class SetupWindow : Window
{
    public SetupWindow(ViewModels.SetupViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        // Auto-run system check as soon as the window is visible
        Loaded += async (_, _) => await viewModel.RunCheckCommand.ExecuteAsync(null);
    }
}
