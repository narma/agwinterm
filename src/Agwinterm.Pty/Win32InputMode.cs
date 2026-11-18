namespace Agwinterm.Pty;

internal static class Win32InputMode
{
    internal static string EncodeKey(uint virtualKey, uint scanCode, int unicode, bool down, uint controlState)
    {
        // Console.ReadKey filters the generic modifier VKs used in KEY_EVENT_RECORD.
        // The scan code distinguishes the Shift keys, and ENHANCED_KEY marks right Ctrl/Alt.
        if (virtualKey is 0xA3 or 0xA5) controlState |= 0x100;
        uint consoleKey = virtualKey switch
        {
            0xA0 or 0xA1 => 0x10, // VK_SHIFT
            0xA2 or 0xA3 => 0x11, // VK_CONTROL
            0xA4 or 0xA5 => 0x12, // VK_MENU
            _ => virtualKey,
        };
        return $"\x1b[{consoleKey};{scanCode};{unicode};{(down ? 1 : 0)};{controlState};1_";
    }
}
