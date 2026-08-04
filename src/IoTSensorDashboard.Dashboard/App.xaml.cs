using System.Windows;

namespace IoTSensorDashboard.Dashboard;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(
                $"처리되지 않은 오류가 발생했습니다.\n\n{args.Exception.Message}",
                "종합 상황판", MessageBoxButton.OK, MessageBoxImage.Error);

            args.Handled = true;
        };

        base.OnStartup(e);
    }
}
