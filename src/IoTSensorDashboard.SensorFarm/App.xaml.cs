using System.Windows;
using IoTSensorDashboard.Core.Diagnostics;

namespace IoTSensorDashboard.SensorFarm;

public partial class App : Application
{
    private FileDiagnosticLog? _log;

    protected override void OnStartup(StartupEventArgs e)
    {
        _log = new FileDiagnosticLog("sensorfarm");
        Diag.Current = _log;

        // 🔒 처리되지 않은 예외를 조용히 죽게 두지 않는다.
        //    "그냥 사라졌다"가 가장 진단하기 어려운 실패다.
        DispatcherUnhandledException += (_, args) =>
        {
            Diag.Error("app", "처리되지 않은 UI 예외", args.Exception);

            MessageBox.Show(
                $"처리되지 않은 오류가 발생했습니다.\n\n{args.Exception.Message}\n\n로그: {_log.Path_}",
                "센서 팜", MessageBoxButton.OK, MessageBoxImage.Error);

            args.Handled = true;
        };

        // 🔑 관측되지 않은 Task 의 예외 — 아무도 안 보는 실패다.
        //    이걸 안 잡으면 "왜 데이터가 안 가지?"의 원인이 영영 안 보인다.
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
