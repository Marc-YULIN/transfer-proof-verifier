using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace TransferProof.Core.Domain;

/// <summary>Synthetic sample metadata only; this is not evidence of an authentic bank transfer.</summary>
public static class PngMetadata
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];
    private const string Keyword = "x-transfer-proof";
    public static byte[] Write(byte[] png, TransferExtraction extraction)
    {
        using var input = new MemoryStream(png);
        if (!ReadSignature(input)) throw new InvalidDataException("Invalid PNG signature.");
        using var output = new MemoryStream(); output.Write(Signature);
        while (ReadChunk(input) is { } chunk)
        {
            if (chunk.Type == "IEND")
            {
                WriteChunk(output, "tEXt", Encoding.UTF8.GetBytes(Keyword + "\0" + JsonSerializer.Serialize(extraction)));
                WriteChunk(output, chunk.Type, chunk.Data);
                return output.ToArray();
            }
            WriteChunk(output, chunk.Type, chunk.Data);
        }
        throw new InvalidDataException("PNG has no end chunk.");
    }
    public static TransferExtraction? Read(Stream stream)
    {
        if (!ReadSignature(stream)) return null;
        while (ReadChunk(stream) is { } chunk)
        {
            if (chunk.Type == "tEXt")
            {
                var text = Encoding.UTF8.GetString(chunk.Data);
                if (text.StartsWith(Keyword + "\0", StringComparison.Ordinal))
                    return JsonSerializer.Deserialize<TransferExtraction>(text[(Keyword.Length + 1)..]);
            }
            if (chunk.Type == "IEND") return null;
        }
        return null;
    }
    private static bool ReadSignature(Stream stream)
    {
        Span<byte> bytes = stackalloc byte[8];
        return stream.ReadAtLeast(bytes, 8, false) == 8 && bytes.SequenceEqual(Signature);
    }
    private static (string Type, byte[] Data)? ReadChunk(Stream stream)
    {
        Span<byte> header = stackalloc byte[8];
        if (stream.ReadAtLeast(header, 8, false) == 0) return null;
        var length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length > 64 * 1024 * 1024) throw new InvalidDataException("PNG chunk too large.");
        var data = new byte[length]; stream.ReadExactly(data);
        Span<byte> crc = stackalloc byte[4]; stream.ReadExactly(crc);
        var type = header[4..].ToArray();
        if (Crc32([.. type, .. data]) != BinaryPrimitives.ReadUInt32BigEndian(crc)) throw new InvalidDataException("PNG checksum mismatch.");
        return (Encoding.ASCII.GetString(type), data);
    }
    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        Span<byte> number = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(number, (uint)data.Length); stream.Write(number);
        var typeBytes = Encoding.ASCII.GetBytes(type); stream.Write(typeBytes); stream.Write(data);
        BinaryPrimitives.WriteUInt32BigEndian(number, Crc32([.. typeBytes, .. data])); stream.Write(number);
    }
    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = uint.MaxValue;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
        }
        return ~crc;
    }
}
