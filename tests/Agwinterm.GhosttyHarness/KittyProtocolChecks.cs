using System.Runtime.InteropServices;
using System.Text;
using Agwinterm.Core;
using Agwinterm.Win32;

internal static class KittyProtocolChecks
{
    internal static void Run(Action<bool, string> check)
    {
        using var vt = new GhosttyVt(20, 5);
        string Key(uint vk, uint action = 1, uint mods = 0, string text = "", uint unshifted = 0)
            => Encoding.UTF8.GetString(vt.EncodeKey(vk, action, mods, text, unshifted));
        vt.Feed("\u001b[=11u"u8);
        foreach (uint vk in new uint[] { 0x91, 0x2c, 0x13, 0x6c, 0x0c, 0x10, 0xA1 })
            Console.WriteLine($"Kitty probe vk={vk:X2}: {Convert.ToHexString(vt.EncodeKey(vk,1,0))}");
        nint de = LoadKeyboardLayoutW("00000407", 0);
        byte[] state = new byte[256];
        state[0x11] = state[0xA2] = state[0x12] = state[0xA5] = 0x80;
        var altGr = NativeKeyText.Translate(0x51, state, 0x10, de);
        Console.WriteLine($"Kitty AltGr+Q text={altGr.Text} unshifted={altGr.Unshifted}: {Convert.ToHexString(vt.EncodeKey(0x51,1,0,altGr.Text,altGr.Unshifted))}");
        check(altGr.Text == "@" && altGr.Unshifted == '@' && Key(0x51, text: altGr.Text, unshifted: altGr.Unshifted) == "\u001b[64u",
            "report-all preserves the AltGr layout level in the primary key");
        foreach (var (vk, code) in new (uint, int)[] { (0x91,57359),(0x2c,57361),(0x13,57362),(0x6c,57416),(0x0c,57427) })
        {
            check(Key(vk) == $"\u001b[{code}u", $"Kitty functional key {vk:X2}");
            check(Key(vk,2) == $"\u001b[{code};1:2u", $"Kitty functional repeat {vk:X2}");
            check(Key(vk,0) == $"\u001b[{code};1:3u", $"Kitty functional release {vk:X2}");
        }
        foreach (var (vk, data, resolved, code, mod) in new (int, int, int, int, uint)[] {
            (0x10,0x002a0001,0xA0,57441,1),(0x10,0x00360001,0xA1,57447,1),
            (0x11,0x001d0001,0xA2,57442,2),(0x11,0x011d0001,0xA3,57448,2),
            (0x12,0x00380001,0xA4,57443,4),(0x12,0x01380001,0xA5,57449,4) })
        {
            int actual = NativeKeyText.ResolveKey(vk, data);
            check(actual == resolved && NativeKeyText.ResolveKey(vk, unchecked((nint)(data | 0xc0000000u))) == resolved,
                $"Windows modifier identity survives key-up {resolved:X2}");
            uint encodedMod = mod == 2 ? 5u : mod == 4 ? 3u : 2u;
            check(Key((uint)actual,mods:mod) == $"\u001b[{code};{encodedMod}u", $"sided modifier press {resolved:X2}");
            check(Key((uint)actual,0) == $"\u001b[{code};1:3u", $"sided modifier release {resolved:X2}");
            check(Key((uint)actual,0,mod) == $"\u001b[{code};{encodedMod}:3u", $"opposite modifier remains held {resolved:X2}");
        }
        foreach (int flags in new[] { 0, 1, 3, 7, 8, 11, 15, 31 })
        {
            vt.Feed(Encoding.ASCII.GetBytes($"\u001b[={flags}u"));
            bool all = (flags & 8) != 0, events = (flags & 2) != 0, associated = (flags & 16) != 0;
            check(Key(0x41,text:"a",unshifted:'a') == (all ? associated ? "\u001b[97;;97u" : "\u001b[97u" : "a"), $"text/report-all/associated flags={flags}");
            string colon = (flags & 4) != 0 ? "59:58" : "59";
            string expectedColon = all ? $"\u001b[{colon};2{(associated ? ";58" : "")}u" : ":";
            check(Encoding.UTF8.GetString(vt.EncodeKey(0xBA,1,1,":",';',1)) == expectedColon, $"shifted text/alternates/associated flags={flags}");
            check(Key(0x41,mods:2,text:"a",unshifted:'a') == (flags == 0 ? "\u0001" : "\u001b[97;5u"), $"Ctrl never gains associated text flags={flags}");
            check(Key(0x74,2) == (events ? "\u001b[15;1:2~" : "\u001b[15~"), $"F5 repeat flags={flags}");
            check(Key(0x74,0) == (events ? "\u001b[15;1:3~" : ""), $"F5 release flags={flags}");
            check(Key(0x1b) == (flags == 0 ? "\u001b" : "\u001b[27u"), $"Escape disambiguation flags={flags}");
            foreach (var (vk, cp, legacy) in new (uint,int,string)[] { (13,13,"\r"),(9,9,"\t"),(8,127,"\x7f") })
            {
                check(Key(vk) == (all ? $"\u001b[{cp}u" : legacy), $"recovery key press {vk}, flags={flags}");
                check(Key(vk,0) == (all && events ? $"\u001b[{cp};1:3u" : ""), $"recovery key release {vk}, flags={flags}");
            }
        }
        vt.Feed("\u001b[=11u"u8);
        check(Key(0x41,mods:48,text:"A",unshifted:'a') == "\u001b[97;193u", "CapsLock and NumLock bits use Kitty positions");
        vt.Feed("\u001b[=31u"u8);
        check(Key(0x41,0,0,"a",'a') == "\u001b[97;1:3u", "release never includes associated text");
        check(Key(0x51,1,0,altGr.Text,altGr.Unshifted) == "\u001b[64::113;;64u", "AltGr associated text and base-layout alternate");

        var host = new Host(); vt.Host = host;
        vt.Feed("\u001b[=4u"u8);
        Console.WriteLine($"Kitty alternate-only Ctrl+A: {Convert.ToHexString(vt.EncodeKey(0x41,1,2,"a",'a'))}");
        check(Key(0x41,mods:2,text:"a",unshifted:'a') == "\u0001" && Key(0x1b) == "\u001b",
            "alternate-key reporting alone does not enable disambiguation");
        void Flags(string sequence, int expected)
        {
            host.Replies = "";
            vt.Feed(Encoding.ASCII.GetBytes(sequence + "\u001b[?u"));
            check(host.Replies == $"\u001b[?{expected}u", $"Kitty flags negotiation expected={expected}");
        }
        Flags("", 4);
        Flags("\u001bc", 0);
        Flags("\u001b[=1u", 1);
        Flags("\u001b[=2;2u", 3);
        Flags("\u001b[=1;3u", 2);
        Flags("\u001b[>7u", 7);
        Flags("\u001b[>31u", 31);
        Flags("\u001b[<u", 7);
        Flags("\u001b[?1049h", 0);
        Flags("\u001b[>11u", 11);
        Flags("\u001b[?1049l", 7);
        Flags("\u001b[<u", 2);
        Flags("\u001b[<99u", 0);
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint LoadKeyboardLayoutW(string name, uint flags);
}
