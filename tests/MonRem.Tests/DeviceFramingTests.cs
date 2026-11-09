using System.Text;
using MonRem.Protocol;
using MonRem.Protocol.V1;

namespace MonRem.Tests;

public class DeviceFramingTests
{
    [Theory]
    [InlineData("00", "0101")]
    [InlineData("0000", "010101")]
    [InlineData("11220033", "0311220233")]
    [InlineData("11223344", "0511223344")]
    [InlineData("11000000", "0211010101")]
    public void Cobs_matches_reference_vectors(string rawHex, string encodedHex)
    {
        var raw = Convert.FromHexString(rawHex);
        var buffer = new byte[raw.Length + 4];
        var n = Cobs.Encode(raw, buffer);
        Assert.Equal(encodedHex, Convert.ToHexString(buffer[..n]), ignoreCase: true);

        var decoded = new byte[n];
        Assert.Equal(raw, decoded[..Cobs.Decode(buffer[..n], decoded)]);
    }

    [Fact]
    public void Cobs_handles_full_254_byte_block()
    {
        var raw = Enumerable.Range(1, 254).Select(i => (byte)i).ToArray();
        var buffer = new byte[300];
        var n = Cobs.Encode(raw, buffer);
        Assert.Equal(255, n);
        Assert.Equal(0xFF, buffer[0]);

        var withZeroAfter = raw.Append((byte)0).Append((byte)7).ToArray();
        n = Cobs.Encode(withZeroAfter, buffer);
        var decoded = new byte[n];
        Assert.Equal(withZeroAfter, decoded[..Cobs.Decode(buffer[..n], decoded)]);
    }

    [Fact]
    public void Crc16_ccitt_false_check_value() => Assert.Equal(0x29B1, Crc16.Compute(Encoding.ASCII.GetBytes("123456789")));

    [Fact]
    public async Task Reader_skips_corrupted_frames_and_noise()
    {
        var good1 = DeviceSerialFraming.Encode(new DeviceFrame { Seq = 1, Ping = new DevicePing { TDeviceMs = 10 } });
        var good2 = DeviceSerialFraming.Encode(new DeviceFrame { Seq = 2, Ping = new DevicePing { TDeviceMs = 20 } });
        var corrupt = (byte[])good1.Clone();
        corrupt[3] ^= 0x40;

        byte[] stream = [0, 0, .. corrupt, .. good1, 0x55, 0x66, 0, .. good2];
        var reader = new DeviceSerialReader(new MemoryStream(stream));

        Assert.Equal(1u, (await reader.ReadAsync(CancellationToken.None))!.Seq);
        Assert.Equal(2u, (await reader.ReadAsync(CancellationToken.None))!.Seq);
        Assert.Null(await reader.ReadAsync(CancellationToken.None));
        Assert.Equal(2, reader.DroppedFrames); // o corrompido e o lixo 55 66
    }

    [Fact]
    public async Task Largest_descriptor_page_fits_serial_frame()
    {
        var page = new DeviceDescriptor { Page = 0, Pages = 1 };
        for (uint i = 0; i < 12; i++)
            page.Elements.Add(new Element { Id = 60000 + i, Kind = ElementKind.Text, Name = new string('X', 24), Min = -1000, Max = 100000, Rows = 4, Cols = 40, Digits = 8, Detents = 4 });
        var frame = new DeviceFrame { Seq = uint.MaxValue, IoDescriptor = page };
        Assert.True(frame.CalculateSize() <= DeviceSerialFraming.MaxPayload);

        var reader = new DeviceSerialReader(new MemoryStream(DeviceSerialFraming.Encode(frame)));
        Assert.Equal(frame, await reader.ReadAsync(CancellationToken.None));
    }
}
