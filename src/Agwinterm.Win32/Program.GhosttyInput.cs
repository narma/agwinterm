using System.Text;
using Agwinterm.Core;
using Agwinterm.Pty;
using static Agwinterm.Win32.Win32;

namespace Agwinterm.Win32;

internal partial class Program
{
    private sealed record NativeKeyTarget(Pane Pane, string Text, uint Unshifted, uint Consumed, bool AltGr, string Unicode)
    {
        public ISession Session => Pane.S;
    }
    private readonly Dictionary<int, NativeKeyTarget[]> _ghosttyKeysDown = new();
    private int _ghosttyCharsToSkip;
    private char _ghosttyHighSurrogate;
    private sealed record NativeKeyText(int Vk, int Chars, string Text, uint Unshifted, bool AltGr, string Unicode);
    private NativeKeyText? _ghosttyKeyText;

    // TranslateMessage mutates Windows' dead-key state. Capture the text before it
    // runs, without consuming the composition, then use it during WM_KEYDOWN.
    private static NativeKeyText ReadNativeKeyText(int vk)
    {
        byte[] state = new byte[256]; GetKeyboardState(state);
        char[] translated = new char[16];
        uint scan = MapVirtualKeyW((uint)vk, 0); nint layout = GetKeyboardLayout(0);
        int chars = ToUnicodeEx((uint)vk, scan, state, translated, translated.Length, 4, layout);
        string unicode = chars > 0 ? new string(translated, 0, Math.Min(chars, translated.Length)) : "";
        bool altGr = (state[VK_CONTROL] & 0x80) != 0 && (state[0xA5] & 0x80) != 0;
        if (!altGr)
        {
            state[VK_CONTROL] = state[0xA2] = state[0xA3] = 0;
            state[VK_MENU] = state[0xA4] = state[0xA5] = 0;
        }
        int length = ToUnicodeEx((uint)vk, scan, state, translated, translated.Length, 4, layout);
        string text = length > 0 && translated[0] >= ' ' && translated[0] != '\x7f'
            ? new string(translated, 0, Math.Min(length, translated.Length)) : "";
        state[VK_CONTROL] = state[0xA2] = state[0xA3] = 0;
        state[VK_MENU] = state[0xA4] = state[0xA5] = 0;
        state[VK_SHIFT] = state[0xA0] = state[0xA1] = state[0x14] = 0;
        int baseLength = ToUnicodeEx((uint)vk, scan, state, translated, translated.Length, 4, layout);
        uint unshifted = baseLength > 0 && Rune.TryGetRuneAt(new string(translated, 0, Math.Min(baseLength, translated.Length)), 0, out var rune)
            && !Rune.IsControl(rune) ? (uint)rune.Value : 0;
        return new(vk, chars, text, unshifted, altGr && text.Length != 0, unicode);
    }

    private static string Win32KeyTextSequence(int vk, bool down, bool ctrl, bool shift, bool alt, string unicode)
    {
        if (unicode.Length <= 1) return Win32KeySeq(vk, down, ctrl, shift, alt, unicode.Length == 0 ? 0 : unicode[0]);
        // A failed dead-key combination can commit two characters; Windows input
        // records carry one UTF-16 unit each, so preserve every committed unit.
        var sequence = new StringBuilder();
        foreach (char character in unicode) sequence.Append(Win32KeySeq(vk, down, ctrl, shift, alt, character));
        return sequence.ToString();
    }

    private bool TryGhosttyCharacter(char character)
    {
        if (ActiveSurface() is not { S.Emulator: GhosttyTerminalCore } surface) return false;
        if (char.IsHighSurrogate(character)) { _ghosttyHighSurrogate = character; return true; }
        string text = char.IsLowSurrogate(character) && _ghosttyHighSurrogate != 0
            ? new string([_ghosttyHighSurrogate, character]) : character.ToString();
        _ghosttyHighSurrogate = '\0';
        if (!Rune.TryGetRuneAt(text, 0, out var rune)) return true;
        List<Pane> targets;
        if (BroadcastActive)
        {
            lock (_workspaces) targets = _active!.Ws.Sessions.SelectMany(session => session.Panes).ToList();
        }
        else targets = [surface];
        foreach (var pane in targets)
        {
            if (pane.ReadOnly || pane.S.InputClosed) continue;
            byte[] bytes;
            lock (pane.S.SyncRoot)
                bytes = pane.S.Emulator is GhosttyTerminalCore core
                    ? core.EncodeKey(0, 1, 0, text, (uint)rune.Value) : Encoding.UTF8.GetBytes(text);
            if (bytes.Length != 0 && SessionInput.TryWrite(pane.S, bytes))
            { pane.ScrollOffset = 0; pane.ClearSel(); }
        }
        return true;
    }

    private bool TryGhosttyKeyDown(int vk, bool ctrl, bool alt, bool shift)
    {
        var surface = ActiveSurface();
        _ghosttyKeysDown.TryGetValue(vk, out var held);
        if (held is null && surface?.S.Emulator is not GhosttyTerminalCore) return false;
        List<Pane> targets;
        if (held is not null) targets = held.Select(target => target.Pane).ToList();
        else if (BroadcastActive)
        {
            lock (_workspaces) targets = _active!.Ws.Sessions.SelectMany(session => session.Panes).ToList();
            // Preserve the transitional mixed-core broadcast route until all
            // receivers share native encoding; never silently omit a receiver.
            if (targets.Any(p => p.S.Emulator is not GhosttyTerminalCore)) return false;
        }
        else targets = [surface!];
        if (held is null && surface!.ReadOnly && !BroadcastActive) { ShowToast("pane is read-only"); return true; }
        var keyText = _ghosttyKeyText is { } captured && captured.Vk == vk ? captured : ReadNativeKeyText(vk);
        _ghosttyKeyText = null;
        if (keyText.Chars < 0) return true; // Windows owns the pending dead-key composition
        string text = keyText.Text;
        uint unshifted = keyText.Unshifted;
        // Windows synthesizes Ctrl+Alt for AltGr. These are layout modifiers,
        // not terminal Ctrl/Alt: Ghostty's legacy C0 encoder uses raw modifiers.
        if (keyText.AltGr) ctrl = alt = false;
        uint mods = (shift ? 1u : 0) | (ctrl ? 2u : 0) | (alt ? 4u : 0)
            | (KeyDown(0x5B) || KeyDown(0x5C) ? 8u : 0)
            | ((GetKeyState(0x14) & 1) != 0 ? 16u : 0) | ((GetKeyState(0x90) & 1) != 0 ? 32u : 0);
        uint action = _ghosttyKeysDown.ContainsKey(vk) ? 2u : 1u, consumed = 0;
        var sent = new List<NativeKeyTarget>();
        foreach (var pane in targets)
        {
            if (pane.ReadOnly || pane.S.InputClosed) continue;
            byte[] bytes;
            lock (pane.S.SyncRoot)
            {
                var core = (GhosttyTerminalCore)pane.S.Emulator;
                bytes = core.Win32InputMode ? Encoding.UTF8.GetBytes(Win32KeyTextSequence(vk, true, ctrl, shift, alt, keyText.Unicode))
                    : core.EncodeKey((uint)vk, action, mods, text, unshifted, consumed);
            }
            if (bytes.Length > 0)
            {
                if (!SessionInput.TryWrite(pane.S, bytes)) continue;
                if (!Keymap.IsModifierKey(vk)) { pane.ScrollOffset = 0; pane.ClearSel(); }
            }
            sent.Add(new(pane, text, unshifted, consumed, keyText.AltGr, keyText.Unicode));
        }
        if (held is null) _ghosttyKeysDown[vk] = sent.ToArray();
        _ghosttyCharsToSkip += Math.Max(0, keyText.Chars);
        return true;
    }

    private void GhosttyKeyUp(int vk)
    {
        if (!_ghosttyKeysDown.Remove(vk, out var targets)) return;
        bool ctrl = KeyDown(VK_CONTROL), shift = KeyDown(VK_SHIFT), alt = KeyDown(VK_MENU);
        uint mods = (shift ? 1u : 0) | (ctrl ? 2u : 0) | (alt ? 4u : 0)
            | (KeyDown(0x5B) || KeyDown(0x5C) ? 8u : 0)
            | ((GetKeyState(0x14) & 1) != 0 ? 16u : 0) | ((GetKeyState(0x90) & 1) != 0 ? 32u : 0);
        foreach (var target in targets)
        {
            if (target.Session.InputClosed) continue;
            try
            {
                byte[] bytes;
                lock (target.Session.SyncRoot)
                {
                    var core = (GhosttyTerminalCore)target.Session.Emulator;
                    bytes = core.Win32InputMode ? Encoding.UTF8.GetBytes(Win32KeyTextSequence(vk, false,
                        ctrl && !target.AltGr, shift, alt && !target.AltGr, target.Unicode))
                        : core.EncodeKey((uint)vk, 0, target.AltGr ? mods & ~6u : mods, target.Text, target.Unshifted, target.Consumed);
                }
                if (bytes.Length > 0) SessionInput.TryWrite(target.Session, bytes);
            }
            catch (ObjectDisposedException) { } // the originating pane closed while the key was held
        }
    }
}
