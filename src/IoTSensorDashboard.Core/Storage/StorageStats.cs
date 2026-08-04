namespace IoTSensorDashboard.Core.Storage;

/// <summary>
/// 저장소가 자기 상태를 스스로 보고한 것.
///
/// 🔑 이 값을 화면에 「회수 가능 N MB」로 띄운다 — 저장소가 자기 상태를 숨기지 않는다.
///    파일이 커지는데 이유를 말할 수 없으면, 사용자는 "고장났나" 하고 앱을 끈다.
/// </summary>
public readonly record struct StorageStats(long FileBytes, long UsedBytes, long FreeBytes, int AutoVacuumMode)
{
    /// <summary>파일 중 빈 페이지 비율. 0.88 이면 파일의 88% 가 빈 공간이라는 뜻이다.</summary>
    public double WasteRatio => FileBytes <= 0 ? 0 : (double)FreeBytes / FileBytes;
}
