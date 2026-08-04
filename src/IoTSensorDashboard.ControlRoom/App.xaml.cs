using System.Windows;
using IoTSensorDashboard.Core.Diagnostics;

namespace IoTSensorDashboard.ControlRoom;

public partial class App : Application
{
    private FileDiagnosticLog? _log;

    protected override void OnStartup(StartupEventArgs e)
    {
        _log = new FileDiagnosticLog("controlroom");
        Diag.Current = _log;

        DispatcherUnhandledException += (_, args) =>
        {
            Diag.Error("app", "처리되지 않은 UI 예외", args.Exception);

            MessageBox.Show(
                $"처리되지 않은 오류가 발생했습니다.\n\n{args.Exception.Message}\n\n로그: {_log.Path_}",
                "관제실", MessageBoxButton.OK, MessageBoxImage.Error);

            args.Handled = true;
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Diag.Error("app", "관측되지 않은 Task 예외", args.Exception);
            args.SetObserved();
        };

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _log?.Dispose();
        base.OnExit(e);
    }
}
