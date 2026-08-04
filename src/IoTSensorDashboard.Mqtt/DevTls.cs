using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace IoTSensorDashboard.Mqtt;

/// <summary>
/// 브로커가 쓸 자체서명 인증서를 <b>런타임에 메모리에서</b> 만든다.
///
/// 왜 파일로 두지 않나: 파일로 두면 "인증서를 먼저 설치하세요"가 생기고,
/// 그 순간 브로커를 앱에 내장한 이유(exe 만 실행하면 됨)가 사라진다.
///
/// 자체서명이 이 맥락에서 괜찮은 이유:
///   브로커가 루프백에만 바인딩되어 통신이 컴퓨터 밖으로 나가지 않는다.
///   인증서의 "신원 확인" 기능이 필요한 이유가 중간자 방지인데, 중간이 없으면 할 일이 없다.
///   암호화와 무결성은 그대로 작동한다.
/// </summary>
public static class DevTls
{
    /// <summary>serverAuth — 이 인증서를 서버 용도로 쓴다는 표시.</summary>
    private const string ServerAuthOid = "1.3.6.1.5.5.7.3.1";

    /// <summary>
    /// CN=localhost · SAN(localhost, 127.0.0.1) · RSA 2048 · SHA256 · 유효기간 5년.
    /// </summary>
    public static X509Certificate2 CreateSelfSigned()
    {
        using var rsa = RSA.Create(2048);

        var request = new CertificateRequest(
            "CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        // SAN(Subject Alternative Name) — 어떤 이름/주소로 접속했을 때 이 인증서가 유효한지.
        // 최신 TLS 구현은 CN 이 아니라 SAN 을 본다. 없으면 이름 검증이 무조건 실패한다.
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());

        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension([new Oid(ServerAuthOid)], critical: false));

        // CA 가 아니다 — 이 인증서로 다른 인증서를 발급할 수 없다.
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(certificateAuthority: false, false, 0, critical: false));

        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                critical: false));

        var now = DateTimeOffset.UtcNow;

        // 시작 시각을 하루 앞당긴다 — 기계 간 시계 오차로 "아직 유효하지 않음"이 되는 것을 막는다.
        using var generated = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(5));

        // ⚠️ 여기가 함정이다. 반드시 PFX 로 한 번 내보냈다 다시 읽어야 한다.
        //
        //    X509KeyStorageFlags.EphemeralKeySet(메모리 전용 키)을 쓰면
        //    Windows 의 TLS 구현(Schannel)이 서버 쪽에서 그 키를 쓰지 못한다.
        //
        //    증상이 고약하다 — 오류 메시지가 아니라 연결이 그냥 끊긴다(EOF).
        //    "방화벽인가? 포트가 막혔나?" 를 한참 뒤지게 된다.
        return new X509Certificate2(
            generated.Export(X509ContentType.Pfx),
            (string?)null,
            X509KeyStorageFlags.Exportable);
    }
}
