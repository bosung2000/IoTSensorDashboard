namespace IoTSensorDashboard.Core.Storage;

/// <summary>
/// 데이터 파일의 위치. exe 가 어디 있든 항상 같은 곳을 본다(I6).
///
/// 📌 왜 빌드 출력 폴더 옆에 두지 않나:
///    Debug/Release 빌드마다 다른 DB 를 보게 되고, 재빌드하면 데이터가 사라진 것처럼 보인다.
///    리포트가 프로세스를 넘어 성립하려면 어디서 실행하든 같은 파일이어야 한다.
/// </summary>
public static class DbPaths
{
    public const string AppFolderName = "IoTSensorDashboard";
    public const string EventsDbFileName = "events.db";
    public const string EscalationLogFileName = "escalations.log";

    /// <summary>%LOCALAPPDATA%\IoTSensorDashboard — 이 기계에만 있는 데이터.</summary>
    public static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppFolderName);

    /// <summary>%APPDATA%\IoTSensorDashboard — 사용자 설정(로밍 대상).</summary>
    public static string SettingsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        AppFolderName);

    /// <summary>이벤트 · 장애 이력 · 감사 로그가 함께 사는 파일.</summary>
    public static string EventsDb => Path.Combine(DataDirectory, EventsDbFileName);

    public static string EscalationLog => Path.Combine(DataDirectory, EscalationLogFileName);

    /// <param name="app">controlroom · sensorfarm · dashboard</param>
    public static string Prefs(string app) => Path.Combine(SettingsDirectory, $"prefs-{app}.json");

    public static string ConnectionString(string? dbPath = null) =>
        $"Data Source={dbPath ?? EventsDb}";

    /// <summary>데이터 폴더가 없으면 만든다. 이미 있으면 아무 일도 하지 않는다.</summary>
    public static void EnsureDataDirectory() => Directory.CreateDirectory(DataDirectory);
}
