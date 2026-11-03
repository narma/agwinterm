using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;

// Data-only protocol. No host paths, script names, credentials or commands cross it.
public static class HyperVUiTransfer
{
    public const long MaxArchiveBytes = 4L * 1024 * 1024 * 1024;
    static void ReadExactly(Stream stream, byte[] buffer, int count, int seconds)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);
        int offset = 0;
        while (offset < count)
        {
            TimeSpan remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) throw new TimeoutException("UI transfer timed out.");
            int read = stream.ReadAsync(buffer, offset, count - offset).WaitAsync(remaining).GetAwaiter().GetResult();
            if (read == 0) throw new EndOfStreamException("UI worker connection closed.");
            offset += read;
        }
    }
    static void Write(Stream stream, byte[] data, int count) =>
        stream.WriteAsync(data, 0, count).WaitAsync(TimeSpan.FromSeconds(120)).GetAwaiter().GetResult();
    public static void SendText(Stream stream, string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length > 65536) throw new InvalidDataException("Oversized protocol header.");
        Write(stream, BitConverter.GetBytes(bytes.Length), 4); Write(stream, bytes, bytes.Length);
    }
    public static string ReceiveText(Stream stream, int seconds)
    {
        byte[] length = new byte[4]; ReadExactly(stream, length, 4, seconds);
        int size = BitConverter.ToInt32(length, 0);
        if (size < 0 || size > 65536) throw new InvalidDataException("Invalid protocol header.");
        byte[] bytes = new byte[size]; ReadExactly(stream, bytes, size, 120);
        return Encoding.UTF8.GetString(bytes);
    }
    public static void SendFile(Stream stream, string path)
    {
        using var file = File.OpenRead(path);
        if (file.Length > MaxArchiveBytes) throw new InvalidDataException("UI archive exceeds 4 GiB.");
        Write(stream, BitConverter.GetBytes(file.Length), 8);
        byte[] buffer = new byte[1024 * 1024]; int read;
        DateTime deadline = DateTime.UtcNow.AddMinutes(10);
        while ((read = file.Read(buffer, 0, buffer.Length)) != 0) {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("UI archive transfer timed out.");
            Write(stream, buffer, read);
        }
    }
    public static void ReceiveFile(Stream stream, string path)
    {
        byte[] length = new byte[8]; ReadExactly(stream, length, 8, 120);
        long size = BitConverter.ToInt64(length, 0);
        if (size < 0 || size > MaxArchiveBytes) throw new InvalidDataException("Invalid UI archive size.");
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        byte[] buffer = new byte[1024 * 1024];
        DateTime deadline = DateTime.UtcNow.AddMinutes(10);
        while (size > 0) {
            int seconds = (int)Math.Min(120, (deadline - DateTime.UtcNow).TotalSeconds);
            if (seconds <= 0) throw new TimeoutException("UI archive transfer timed out.");
            int count = (int)Math.Min(size, buffer.Length); ReadExactly(stream, buffer, count, seconds); file.Write(buffer, 0, count); size -= count;
        }
    }
    static void NoLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Links are forbidden in UI archives and evidence: " + path);
    }
    public static void ValidateTree(string root)
    {
        NoLink(root);
        var pending = new Stack<string>(); pending.Push(root);
        long bytes = 0; int count = 0;
        while (pending.Count > 0)
            foreach (string path in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                NoLink(path);
                if (++count > 50000) throw new InvalidDataException("Too many UI evidence files.");
                if (Directory.Exists(path)) pending.Push(path);
                else if ((bytes += new FileInfo(path).Length) > 8L * 1024 * 1024 * 1024) throw new InvalidDataException("UI evidence exceeds 8 GiB.");
            }
    }
    public static void Pack(string directory, string path)
    {
        ValidateTree(directory);
        ZipFile.CreateFromDirectory(directory, path, CompressionLevel.NoCompression, false);
    }
    public static void Extract(string archive, string directory)
    {
        Directory.CreateDirectory(directory); NoLink(directory);
        string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(archive);
        if (zip.Entries.Count > 50000) throw new InvalidDataException("Too many archive entries.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long bytes = 0;
        foreach (var entry in zip.Entries)
        {
            string name = entry.FullName.Replace('\\', '/');
            bool folder = name.EndsWith("/", StringComparison.Ordinal);
            string relative = name.TrimEnd('/');
            if (relative.Length == 0 || name.Length > 1024 || name.StartsWith("/", StringComparison.Ordinal) || !names.Add(relative))
                throw new InvalidDataException("Invalid or duplicate archive path.");
            foreach (string part in relative.Split('/'))
            {
                string stem = part.Split('.')[0].ToUpperInvariant();
                if (part.Length == 0 || part == "." || part == ".." || part != part.TrimEnd(' ', '.') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                    stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL" || stem == "CONIN$" || stem == "CONOUT$" ||
                    (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsNumber(stem[3])))
                    throw new InvalidDataException("Unsafe archive path.");
            }
            if ((((uint)entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Archive links are forbidden.");
            if ((bytes += entry.Length) > 8L * 1024 * 1024 * 1024) throw new InvalidDataException("Expanded archive exceeds 8 GiB.");
            string target = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Archive escapes its destination.");
            string parent = folder ? target : Path.GetDirectoryName(target);
            string current = root.TrimEnd(Path.DirectorySeparatorChar);
            foreach (string part in Path.GetRelativePath(current, parent).Split(Path.DirectorySeparatorChar))
            {
                if (part == ".") continue;
                current = Path.Combine(current, part);
                if (Directory.Exists(current)) NoLink(current); else Directory.CreateDirectory(current);
            }
            if (!folder) {
                using var source = entry.Open(); using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                byte[] buffer = new byte[65536]; long written = 0; int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) != 0) {
                    written += read;
                    if (written > entry.Length) throw new InvalidDataException("Archive entry exceeds its declared size.");
                    output.Write(buffer, 0, read);
                }
                if (written != entry.Length) throw new InvalidDataException("Truncated archive entry.");
            }
        }
    }
}
