using System.Runtime.InteropServices;
using System.Text;

namespace Agwinterm.Win32;

internal sealed record NativeKeyText(int Vk, int Chars, string Text, uint Unshifted, bool AltGr, string Unicode, uint Consumed)
{
    internal int Key { get; init; } = Vk;

    // WM_KEY* uses generic modifier VKs. The scan/extended bits identify the
    // actual key, including when both sides are held at the same time.
    internal static int ResolveKey(int vk, nint keyData)
    {
        uint scan = (uint)((long)keyData >> 16) & 0xff;
        bool extended = ((long)keyData & 0x01000000) != 0;
        return vk switch {
            0x10 => scan == 0x36 ? 0xA1 : 0xA0,
            0x11 => extended ? 0xA3 : 0xA2,
            0x12 => extended ? 0xA5 : 0xA4,
            _ => vk
        };
    }
    // The supplied keyboard snapshot is private to this translation. Flag 4
    // keeps all probes from changing Windows' pending dead-key composition.
    internal static NativeKeyText Translate(int vk, byte[] state, uint scan, nint layout)
    {
        char[] translated = new char[16];
        string TranslateText(out int count)
        {
            count = ToUnicodeEx((uint)vk, scan, state, translated, translated.Length, 4, layout);
            return count > 0 ? new string(translated, 0, Math.Min(count, translated.Length)) : "";
        }
        string unicode = TranslateText(out int chars);
        bool altGr = (state[0x11] & 0x80) != 0 && (state[0xA5] & 0x80) != 0;
        if (!altGr)
        {
            state[0x11] = state[0xA2] = state[0xA3] = 0;
            state[0x12] = state[0xA4] = state[0xA5] = 0;
        }
        string text = TranslateText(out _);
        if (text.Length != 0 && (text[0] < ' ' || text[0] == '\x7f')) text = "";

        // libghostty needs the modifiers consumed in producing UTF-8 text.
        // Probe the active layout without Shift, preserving CapsLock and AltGr;
        // comparing to the base codepoint would confuse those layout states.
        bool shift = (state[0x10] & 0x80) != 0;
        state[0x10] = state[0xA0] = state[0xA1] = 0;
        uint consumed = shift && text.Length != 0 && text != TranslateText(out _) ? 1u : 0;

        // "Unshifted" means the active layout level without Shift/CapsLock,
        // not without AltGr. Ctrl+Alt was removed above for ordinary shortcuts.
        // Dropping AltGr here turns e.g. German @ back into q in report-all mode.
        if (text.Length == 0)
        {
            state[0x11] = state[0xA2] = state[0xA3] = 0;
            state[0x12] = state[0xA4] = state[0xA5] = 0;
        }
        state[0x14] = 0;
        string baseText = TranslateText(out _);
        uint unshifted = baseText.Length > 0 && Rune.TryGetRuneAt(baseText, 0, out var rune) && !Rune.IsControl(rune) ? (uint)rune.Value : 0;
        return new(vk, chars, text, unshifted, altGr && text.Length != 0, unicode, consumed);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ToUnicodeEx(uint vk, uint scan, byte[] state, [Out] char[] text, int capacity, uint flags, nint layout);
}
