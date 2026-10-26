using System.Text;

namespace Agwinterm.Ctl;

public static class HudCliInput
{
    public static string ReadFile(string path)
    {
        const int maximum = 64 * 1024;
        using var file = File.OpenRead(path);
        var bytes = new byte[maximum + 1]; int count = 0, n;
        while (count < bytes.Length && (n = file.Read(bytes, count, bytes.Length - count)) != 0) count += n;
        if (count > maximum) throw new ArgumentException("HUD message file exceeds 64 KiB");
        int start = count >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? 3 : 0;
        return new UTF8Encoding(false, true).GetString(bytes, start, count - start);
    }
}
