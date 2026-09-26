using System.Buffers.Binary;
using System.Collections.Concurrent;

namespace SportsPlatform.Auth.Api.Services;

/// <summary>
/// Moves MP4 metadata before media data so native/web players can start
/// playback without downloading the whole file first.
/// </summary>
public class Mp4StreamingOptimizer
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> FileLocks = new();

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4",
        ".m4v",
        ".mov",
    };

    private static readonly HashSet<string> ContainerBoxes = new(StringComparer.Ordinal)
    {
        "moov",
        "trak",
        "mdia",
        "minf",
        "stbl",
        "edts",
        "dinf",
        "udta",
        "meta",
        "ilst",
    };

    public async Task OptimizeAsync(string path)
    {
        if (!SupportedExtensions.Contains(Path.GetExtension(path)) || !File.Exists(path))
            return;

        var fullPath = Path.GetFullPath(path);
        var fileLock = FileLocks.GetOrAdd(fullPath, _ => new SemaphoreSlim(1, 1));
        await fileLock.WaitAsync();
        var tempPath = $"{path}.{Guid.NewGuid():N}.streaming.tmp";

        try
        {
            if (!File.Exists(path))
                return;

            var bytes = await File.ReadAllBytesAsync(path);
            var atoms = ReadTopLevelAtoms(bytes);
            var ftyp = atoms.FirstOrDefault(a => a.Type == "ftyp");
            var mdat = atoms.FirstOrDefault(a => a.Type == "mdat");
            var moov = atoms.FirstOrDefault(a => a.Type == "moov");

            if (ftyp.Type == null || mdat.Type == null || moov.Type == null)
                return;

            if (moov.Offset < mdat.Offset)
                return;

            var moovBytes = bytes.AsSpan(moov.Offset, checked((int)moov.Size)).ToArray();
            PatchChunkOffsets(moovBytes, moov.Size);

            var output = new byte[bytes.Length];
            var write = 0;

            void CopyAtom(Atom atom)
            {
                Buffer.BlockCopy(bytes, atom.Offset, output, write, checked((int)atom.Size));
                write += checked((int)atom.Size);
            }

            CopyAtom(ftyp);
            Buffer.BlockCopy(moovBytes, 0, output, write, moovBytes.Length);
            write += moovBytes.Length;

            foreach (var atom in atoms)
            {
                if (atom.Offset == ftyp.Offset || atom.Offset == moov.Offset)
                    continue;
                CopyAtom(atom);
            }

            if (write != output.Length)
                return;

            await File.WriteAllBytesAsync(tempPath, output);
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
        finally
        {
            fileLock.Release();
        }
    }

    private static List<Atom> ReadTopLevelAtoms(byte[] bytes)
    {
        var atoms = new List<Atom>();
        var offset = 0;
        while (offset + 8 <= bytes.Length)
        {
            var size32 = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));
            var type = ReadType(bytes, offset + 4);
            long size = size32;
            var headerSize = 8;

            if (size32 == 1)
            {
                if (offset + 16 > bytes.Length) break;
                size = checked((long)BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(offset + 8, 8)));
                headerSize = 16;
            }
            else if (size32 == 0)
            {
                size = bytes.Length - offset;
            }

            if (size < headerSize || offset + size > bytes.Length)
                break;

            atoms.Add(new Atom(offset, size, type));
            offset += checked((int)size);
        }

        return atoms;
    }

    private static void PatchChunkOffsets(byte[] box, long delta)
    {
        PatchChildren(box, 0, box.Length, delta);
    }

    private static void PatchChildren(byte[] bytes, int start, int end, long delta)
    {
        var offset = start;
        while (offset + 8 <= end)
        {
            var size32 = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));
            var type = ReadType(bytes, offset + 4);
            long size = size32;
            var headerSize = 8;

            if (size32 == 1)
            {
                if (offset + 16 > end) return;
                size = checked((long)BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(offset + 8, 8)));
                headerSize = 16;
            }

            if (size < headerSize || offset + size > end)
                return;

            var contentStart = offset + headerSize;
            var boxEnd = checked(offset + (int)size);

            if (type == "stco")
            {
                PatchStco(bytes, contentStart, boxEnd, delta);
            }
            else if (type == "co64")
            {
                PatchCo64(bytes, contentStart, boxEnd, delta);
            }
            else if (ContainerBoxes.Contains(type))
            {
                var childStart = type == "meta" ? contentStart + 4 : contentStart;
                if (childStart < boxEnd)
                    PatchChildren(bytes, childStart, boxEnd, delta);
            }

            offset = boxEnd;
        }
    }

    private static void PatchStco(byte[] bytes, int contentStart, int boxEnd, long delta)
    {
        if (contentStart + 8 > boxEnd) return;
        var count = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(contentStart + 4, 4));
        var offset = contentStart + 8;
        for (var i = 0; i < count && offset + 4 <= boxEnd; i++, offset += 4)
        {
            var oldValue = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));
            BinaryPrimitives.WriteUInt32BigEndian(
                bytes.AsSpan(offset, 4),
                checked((uint)(oldValue + delta)));
        }
    }

    private static void PatchCo64(byte[] bytes, int contentStart, int boxEnd, long delta)
    {
        if (contentStart + 8 > boxEnd) return;
        var count = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(contentStart + 4, 4));
        var offset = contentStart + 8;
        for (var i = 0; i < count && offset + 8 <= boxEnd; i++, offset += 8)
        {
            var oldValue = BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(offset, 8));
            BinaryPrimitives.WriteUInt64BigEndian(
                bytes.AsSpan(offset, 8),
                checked(oldValue + (ulong)delta));
        }
    }

    private static string ReadType(byte[] bytes, int offset) =>
        System.Text.Encoding.ASCII.GetString(bytes, offset, 4);

    private readonly record struct Atom(int Offset, long Size, string Type);
}
