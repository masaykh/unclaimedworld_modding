using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace UW.Tools.MgfxTranscode;

/// <summary>
/// Minimal reader/writer for the XNB container, only as much as is needed to reach an
/// effect's MGFX blob and put a rewritten one back.
///
/// Layout (from the shipped MonoGame 3.6 ContentManager.GetContentReaderFromXnb, which is
/// unchanged through 3.8.5.1):
///
///   'X' 'N' 'B'            3 bytes
///   platform               1 byte   ('w' for Windows - all 550 of the game's XNBs)
///   version                1 byte   (5)
///   flags                  1 byte   (0x80 LZX, 0x40 LZ4, 0x01 HiDef)
///   fileSize               int32    total size of the file including this header
///   [decompressedSize]     int32    present only when a compression flag is set
///   payload                        compressed or raw:
///     typeReaderCount      7-bit encoded int
///     per reader:          string name, int32 version
///     sharedResourceCount  7-bit encoded int
///     primary object:      7-bit encoded type id, then the object's own data
///                          (for EffectReader: int32 length, then that many MGFX bytes)
/// </summary>
internal sealed class XnbFile
{
    public const byte FlagHiDef = 0x01;
    public const byte FlagCompressedLz4 = 0x40;
    public const byte FlagCompressedLzx = 0x80;

    public byte Platform { get; private set; }
    public byte Version { get; private set; }
    public byte Flags { get; private set; }
    public bool WasCompressed { get; private set; }

    /// <summary>Type reader table, in file order. Index 0 is type id 1.</summary>
    public List<(string Name, int Version)> TypeReaders { get; } = new();

    public int SharedResourceCount { get; private set; }

    /// <summary>1-based type id of the primary object, as stored (0 means null).</summary>
    public int PrimaryTypeId { get; private set; }

    /// <summary>The decompressed payload, everything after the container header.</summary>
    private byte[] _payload = Array.Empty<byte>();

    /// <summary>Offset within <see cref="_payload"/> of the primary object's data.</summary>
    private int _primaryDataOffset;

    public static XnbFile Read(string path)
    {
        byte[] raw = File.ReadAllBytes(path);
        var xnb = new XnbFile();

        if (raw.Length < 10 || raw[0] != 'X' || raw[1] != 'N' || raw[2] != 'B')
            throw new InvalidDataException($"{Path.GetFileName(path)}: not an XNB file.");

        xnb.Platform = raw[3];
        xnb.Version = raw[4];
        xnb.Flags = raw[5];
        int fileSize = BitConverter.ToInt32(raw, 6);

        if (xnb.Version != 5 && xnb.Version != 4)
            throw new InvalidDataException($"{Path.GetFileName(path)}: unsupported XNB version {xnb.Version}.");
        if (fileSize != raw.Length)
            throw new InvalidDataException(
                $"{Path.GetFileName(path)}: header says {fileSize} bytes but the file is {raw.Length}.");

        bool lzx = (xnb.Flags & FlagCompressedLzx) != 0;
        bool lz4 = (xnb.Flags & FlagCompressedLz4) != 0;
        xnb.WasCompressed = lzx || lz4;

        if (lzx)
            throw new NotSupportedException(
                $"{Path.GetFileName(path)}: LZX-compressed XNB. None of the game's 550 assets use LZX " +
                "(9 use LZ4, the rest are uncompressed), so no LZX decoder was written.");

        if (lz4)
        {
            int decompressedSize = BitConverter.ToInt32(raw, 10);
            using var compressed = new MemoryStream(raw, 14, raw.Length - 14, writable: false);
            using var decoder = new Lz4DecoderStream(compressed);
            var buffer = new byte[decompressedSize];
            int read = 0;
            while (read < decompressedSize)
            {
                int n = decoder.Read(buffer, read, decompressedSize - read);
                if (n <= 0)
                    throw new InvalidDataException(
                        $"{Path.GetFileName(path)}: LZ4 stream ended after {read} of {decompressedSize} bytes.");
                read += n;
            }
            xnb._payload = buffer;
        }
        else
        {
            xnb._payload = new byte[raw.Length - 10];
            Array.Copy(raw, 10, xnb._payload, 0, xnb._payload.Length);
        }

        // Walk the reader table so we know where the primary object's data begins.
        using var ms = new MemoryStream(xnb._payload, writable: false);
        using var reader = new BinaryReader(ms, Encoding.UTF8);

        int readerCount = Read7BitEncodedInt(reader);
        for (int i = 0; i < readerCount; i++)
            xnb.TypeReaders.Add((reader.ReadString(), reader.ReadInt32()));

        xnb.SharedResourceCount = Read7BitEncodedInt(reader);
        xnb.PrimaryTypeId = Read7BitEncodedInt(reader);
        xnb._primaryDataOffset = (int)ms.Position;

        return xnb;
    }

    public string PrimaryReaderName =>
        PrimaryTypeId >= 1 && PrimaryTypeId <= TypeReaders.Count
            ? TypeReaders[PrimaryTypeId - 1].Name
            : "(none)";

    /// <summary>
    /// The primary object's MGFX blob. Asserts the container really does hold a single
    /// length-prefixed effect and nothing after it.
    /// </summary>
    public byte[] ReadEffectBlob()
    {
        if (!PrimaryReaderName.StartsWith("Microsoft.Xna.Framework.Content.EffectReader", StringComparison.Ordinal))
            throw new InvalidDataException($"Primary object is '{PrimaryReaderName}', not an EffectReader.");

        int length = BitConverter.ToInt32(_payload, _primaryDataOffset);
        int start = _primaryDataOffset + 4;

        if (length < 10 || start + length > _payload.Length)
            throw new InvalidDataException($"Effect blob length {length} does not fit the payload.");

        int trailing = _payload.Length - (start + length);
        if (trailing != 0)
            throw new InvalidDataException(
                $"{trailing} unexpected byte(s) after the effect blob; this rewriter assumes the " +
                "effect is the only object in the container.");

        var blob = new byte[length];
        Array.Copy(_payload, start, blob, 0, length);
        return blob;
    }

    /// <summary>
    /// Writes the file back with a replacement effect blob, always UNCOMPRESSED.
    /// MonoGame reads uncompressed XNBs on every platform, and writing raw avoids needing an
    /// LZ4 *compressor* just to repack the single compressed effect (multiTex.xnb).
    /// </summary>
    public void WriteWithEffectBlob(string path, byte[] blob)
    {
        using var body = new MemoryStream();
        // Everything up to and including the primary type id is unchanged.
        body.Write(_payload, 0, _primaryDataOffset);
        body.Write(BitConverter.GetBytes(blob.Length), 0, 4);
        body.Write(blob, 0, blob.Length);

        byte[] payload = body.ToArray();
        byte flags = (byte)(Flags & ~(FlagCompressedLz4 | FlagCompressedLzx));

        using var outStream = new FileStream(path, FileMode.Create, FileAccess.Write);
        outStream.WriteByte((byte)'X');
        outStream.WriteByte((byte)'N');
        outStream.WriteByte((byte)'B');
        outStream.WriteByte(Platform);
        outStream.WriteByte(Version);
        outStream.WriteByte(flags);
        outStream.Write(BitConverter.GetBytes(10 + payload.Length), 0, 4);
        outStream.Write(payload, 0, payload.Length);
    }

    /// <summary>
    /// BinaryReader.Read7BitEncodedInt equivalent. Reimplemented rather than using the
    /// protected member so the reader table walk stays explicit.
    /// </summary>
    private static int Read7BitEncodedInt(BinaryReader reader)
    {
        int result = 0;
        int shift = 0;
        while (true)
        {
            if (shift == 35)
                throw new InvalidDataException("Malformed 7-bit encoded int in XNB.");
            byte b = reader.ReadByte();
            result |= (b & 0x7F) << shift;
            shift += 7;
            if ((b & 0x80) == 0)
                return result;
        }
    }
}
