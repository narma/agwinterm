using System.Runtime.InteropServices;
using System.Text;
using Agwinterm.Core;
using Agwinterm.Win32;

internal static class KeyboardChecks
{
    internal static void Run(Action<bool, string> check)
    {
        // Load layouts without activating them or sending input to any desktop.
        nint us = LoadKeyboardLayoutW("00000409", 0);
        nint ru = LoadKeyboardLayoutW("00000419", 0);
        nint de = LoadKeyboardLayoutW("00000407", 0);
        check(us != 0 && ru != 0 && de != 0, "keyboard fixture layouts available");
        using var vt = new GhosttyVt(20, 5);
        NativeKeyText Translate(int vk, nint layout, uint mods, bool caps = false)
        {
            byte[] state = new byte[256];
            if ((mods & 1) != 0) state[0x10] = state[0xA0] = 0x80;
            if ((mods & 2) != 0) state[0x11] = state[0xA2] = 0x80;
            if ((mods & 4) != 0) state[0x12] = state[0xA4] = 0x80;
            if ((mods & 64) != 0) { state[0x11] = state[0xA2] = state[0x12] = state[0xA5] = 0x80; }
            if (caps) state[0x14] = 1;
            return NativeKeyText.Translate(vk, state, MapVirtualKeyExW((uint)vk, 0, layout), layout);
        }
        foreach (int flags in new[] { 0, 3 })
        {
            vt.Feed(Encoding.ASCII.GetBytes($"\u001b[={flags}u"));
            // Reproduce the original event and isolate the consumed-modifier
            // contract against the real native DLL, recording exact wire bytes.
            foreach (uint consumed in new uint[] { 0, 1 })
            foreach (uint action in new uint[] { 1, 2, 0 })
            {
                byte[] bytes = vt.EncodeKey(0xBA, action, 1, ":", ';', consumed);
                Console.WriteLine($"Shift+; flags={flags} text=: unshifted=59 mods=1 consumed={consumed} action={action}: {Convert.ToHexString(bytes)}");
                string expected = action == 0 ? (flags == 0 ? "" : "\u001b[59;2:3u")
                    : flags == 0 || consumed == 1 ? ":"
                    : action == 2 ? "\u001b[59;2:2u" : "\u001b[59;2u";
                check(Encoding.UTF8.GetString(bytes) == expected, "Shift punctuation native consumed-modifier contract");
            }
            foreach (var (vk, plain, shifted) in new (int, string, string)[] {
                (0xBA,";",":"),(0xBB,"=","+"),(0xBC,",","<"),(0xBD,"-","_"),
                (0xBE,".",">"),(0xBF,"/","?"),(0xC0,"`","~"),(0xDB,"[","{"),
                (0xDC,"\\","|"),(0xDD,"]","}"),(0xDE,"'","\""),
                (0x30,"0",")"),(0x31,"1","!"),(0x32,"2","@"),(0x33,"3","#"),
                (0x34,"4","$"),(0x35,"5","%"),(0x36,"6","^"),(0x37,"7","&"),
                (0x38,"8","*"),(0x39,"9","("),(0x41,"a","A") })
            foreach (uint mods in new uint[] { 0, 1 })
            {
                var key = Translate(vk, us, mods);
                string expected = mods == 0 ? plain : shifted;
                check(key.Text == expected && key.Unshifted == plain[0] && key.Consumed == mods,
                    $"Windows translation {expected}, flags={flags}");
                foreach (uint action in new uint[] { 1, 2, 0 })
                {
                    string bytes = Encoding.UTF8.GetString(vt.EncodeKey((uint)vk, action, mods, key.Text, key.Unshifted, key.Consumed));
                    string output = action != 0 ? expected : flags == 0 ? "" : $"\u001b[{(int)plain[0]};{mods+1}:3u";
                    check(bytes == output, $"layout to encoder {expected}, flags={flags}, action={action}");
                }
            }
            foreach (var (vk, layout, mods, caps, expected, consumed) in new (int, nint, uint, bool, string, uint)[] {
                (0x41,ru,0,false,"ф",0),(0x41,ru,1,false,"Ф",1),
                (0x41,us,0,true,"A",0),(0x41,us,1,true,"a",1),
                (0x51,de,64,false,"@",0),(0x20,us,1,false," ",0) })
            {
                var key = Translate(vk, layout, mods, caps);
                check(key.Text == expected && key.Consumed == consumed, $"layout/CapsLock/AltGr translation {expected}");
                if (vk == 0x20) continue; // Shift+Space remains a shortcut modifier.
                uint normalized = (mods & 63) | (caps ? 16u : 0);
                check(Encoding.UTF8.GetString(vt.EncodeKey((uint)vk,1,normalized,key.Text,key.Unshifted,key.Consumed)) == expected,
                    $"layout/CapsLock/AltGr bytes {expected}, flags={flags}");
            }
            foreach (uint mods in new uint[] { 3, 5, 7 })
            {
                var key = Translate(0xBA, us, mods);
                check(key.Text == ":" && key.Consumed == 1 && !key.AltGr, "Ctrl/left Alt are not consumed by translation");
                if (flags == 0)
                {
                    string expected = mods == 3 ? "\u001b[58;5u" : mods == 5 ? "\u001b:" : "\u001b[58;7u";
                    check(Encoding.UTF8.GetString(vt.EncodeKey(0xBA,1,mods,key.Text,key.Unshifted,key.Consumed)) == expected,
                        $"legacy Ctrl/Alt punctuation unchanged {mods}");
                    continue;
                }
                uint kittyMods = 1 + 1u + ((mods & 2) != 0 ? 4u : 0) + ((mods & 4) != 0 ? 2u : 0);
                foreach (uint action in new uint[] { 1, 2, 0 })
                {
                    string suffix = action == 1 ? "" : action == 2 ? ":2" : ":3";
                    check(Encoding.UTF8.GetString(vt.EncodeKey(0xBA,action,mods,key.Text,key.Unshifted,key.Consumed)) == $"\u001b[59;{kittyMods}{suffix}u",
                        $"Ctrl/Alt shortcut preserves modifiers and event type {mods}/{action}");
                }
            }
        }
        vt.Feed("\u001b[=7u"u8);
        check(Encoding.UTF8.GetString(vt.EncodeKey(0xBA,0,0,":",';',1)) == "\u001b[59;1:3u", "Shift released before punctuation does not insert text");
        check(Encoding.UTF8.GetString(vt.EncodeKey(0xBA,1,3,":",';',1)) == "\u001b[59:58;6u", "requested alternate key preserved");
        vt.Feed("\u001b[=11u"u8);
        check(Encoding.UTF8.GetString(vt.EncodeKey(0xBA,1,1,":",';',1)) == "\u001b[59;2u", "report all still encodes shifted text as CSI-u");
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint LoadKeyboardLayoutW(string name, uint flags);
    [DllImport("user32.dll")] private static extern uint MapVirtualKeyExW(uint code, uint type, nint layout);
}
