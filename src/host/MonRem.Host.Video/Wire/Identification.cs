using System.Text;

namespace MonRem.Host.Video.Wire;

/// <summary>Identificação enviada pelo cliente ao conectar (SPEC 13.3 e 15.2).</summary>
public sealed record Identification(int Width, int Height, int Fps, string DeviceId, string DeviceName)
{
    public const int BodySize = 334;
    private const int NameOffset = 78;

    public static bool TryParse(WireMessage message, out Identification? result)
    {
        result = null;
        if (message.Type != MessageType.Identification || message.Body.Length != BodySize)
            return false;

        result = new Identification(
            (int)message.Ints[13], (int)message.Ints[22], (int)message.Ints[8],
            ReadString(message.Body, 0), ReadString(message.Body, NameOffset));
        return true;
    }

    public static WireMessage Create(int width, int height, int fps, string deviceId, string deviceName)
    {
        var body = new byte[BodySize];
        Encoding.Unicode.GetBytes(deviceId + "\0").CopyTo(body, 0);
        Encoding.Unicode.GetBytes(deviceName + "\0").CopyTo(body, NameOffset);
        return WireMessage.Create(MessageType.Identification, body,
            (2, 4), (3, 8), (5, 3), (6, 3), (7, 2), (8, (uint)fps), (9, 0x00010005), (11, 0x0004003C),
            (12, 1), (13, (uint)width), (22, (uint)height), (31, 1));
    }

    private static string ReadString(byte[] body, int offset)
    {
        var chars = Encoding.Unicode.GetString(body, offset, body.Length - offset);
        var end = chars.IndexOf('\0');
        return end < 0 ? chars : chars[..end];
    }
}
