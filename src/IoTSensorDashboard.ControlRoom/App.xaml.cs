using System.Windows;

namespace IoTSensorDashboard.ControlRoom;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // 처리되지 않은 예외를 조용히 죽게 두지 않는다.
        // 🔒 "그냥 사라졌다"가 가장 진단하기 어려운 실패다.
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(
                $"처리되지 않은 오류가 발생했습니다.\n\n{args.Exception.Message}",
                "관제실", MessageBoxButton.OK, MessageBoxImage.Error);

            args.Handled = true;
        };

        base.OnStartup(e);
    }
}
