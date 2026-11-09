namespace MonRem.Protocol;

public static class ProtocolConstants
{
    public const uint ProtocolVersion = 1;

    /// <summary>ALPN do app (Android/PC).</summary>
    public const string AlpnApp = "monrem/1";

    /// <summary>ALPN dos painéis ESP32 via Wi-Fi.</summary>
    public const string AlpnDevice = "monrem-device/1";

    public const int DefaultControlPort = 47800;
    public const int DefaultMediaPort = 47801;
    public const int DefaultInputPort = 47802;

    public const string MdnsServiceType = "_monrem._tcp";

    public const int PinDigits = 6;
    public const int MinStreamId = 1;
    public const int MaxStreamId = 254;

    public static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan ClientPongTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan HostPingTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan ResumeWindow = TimeSpan.FromSeconds(30);
}
