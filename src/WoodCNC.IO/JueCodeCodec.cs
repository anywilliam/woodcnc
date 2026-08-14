namespace WoodCNC.IO;

public static class JueCodeCodec
{
    public const byte XorKey = 0x46;

    public static byte[] Transform(ReadOnlySpan<byte> bytes)
    {
        var output = new byte[bytes.Length];
        for (var i = 0; i < bytes.Length; i++)
        {
            output[i] = (byte)(bytes[i] ^ XorKey);
        }

        return output;
    }
}

