namespace IoTSensorDashboard.Core.Storage;

/// <summary>
/// 집계 한 칸의 조회 결과.
///
/// <para><see cref="Rows"/> 가 따로 있는 이유: 합계만으로는 "0 이 나온 이유"를 알 수 없다.
/// 데이터가 없어서 0 인지, 실제로 0 명이 지나가서 0 인지는 완전히 다른 사실이다.
/// 행 수가 있으면 화면이 그 둘을 구분해 말할 수 있다.</para>
/// </summary>
/// <param name="Key">묶은 기준 — 센서 ID 또는 날짜.</param>
/// <param name="Direction">방향. 방향 없는 이벤트는 빈 문자열.</param>
/// <param name="Sum">카운트 합.</param>
/// <param name="Rows">이 합을 만든 원본 이벤트 수.</param>
public sealed record CountBucket(string Key, string? Direction, long Sum, long Rows);
