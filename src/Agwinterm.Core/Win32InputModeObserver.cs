namespace Agwinterm.Core;

/// <summary>
/// Observes ConPTY's DEC mode 9001 and the application's authenticated OSC 777
/// completion message, which upstream recognizes but deliberately does not expose.
/// Ghostty remains the VT parser; this
/// bounded observer never consumes or rewrites bytes. String payloads are skipped
/// so escape-like text inside OSC/DCS/APC cannot change the Windows input mode.
/// </summary>
internal sealed class Win32InputModeObserver
{
    private enum State { Ground, Escape, Csi, String, StringEscape }
    private State _state;
    private readonly byte[] _parameters = new byte[128];
    private int _length;
    private bool _overflow, _osc;
    private readonly byte[] _oscBytes = new byte[256];
    private int _oscLength;
    private bool _oscOverflow;
    public readonly Queue<(string Token, long Sequence)> Completions = new();
    public bool Enabled { get; private set; }
    public byte[] Save()
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write((byte)_state); writer.Write(Enabled); writer.Write(_overflow); writer.Write(_osc);
        writer.Write(_oscOverflow); writer.Write(_length); writer.Write(_oscLength);
        writer.Write(_parameters); writer.Write(_oscBytes);
        return stream.ToArray();
    }
    public void Restore(byte[] bytes)
    {
        if (bytes.Length != 397 || bytes[0] > (byte)State.StringEscape) throw new InvalidDataException("Invalid Windows sidecar state.");
        using var reader = new BinaryReader(new MemoryStream(bytes));
        var state = (State)reader.ReadByte(); bool enabled = reader.ReadBoolean(), overflow = reader.ReadBoolean(), osc = reader.ReadBoolean(), oscOverflow = reader.ReadBoolean();
        int length = reader.ReadInt32(), oscLength = reader.ReadInt32();
        if (length is < 0 or > 128 || oscLength is < 0 or > 256) throw new InvalidDataException("Invalid Windows sidecar length.");
        reader.ReadExactly(_parameters); reader.ReadExactly(_oscBytes);
        _state = state; Enabled = enabled; _overflow = overflow; _osc = osc; _oscOverflow = oscOverflow; _length = length; _oscLength = oscLength;
        Completions.Clear();
    }
    public void Feed(ReadOnlySpan<byte> bytes)
    {
        foreach (byte b in bytes)
        {
            if (b is 0x18 or 0x1a) { _state = State.Ground; continue; }
            switch (_state)
            {
                case State.Ground:
                    if (b == 0x1b) _state = State.Escape;
                    break;
                case State.Escape:
                    if (b == '[') { _state = State.Csi; _length = 0; _overflow = false; }
                    else if (b is (byte)']' or (byte)'P' or (byte)'_' or (byte)'^' or (byte)'X')
                    { _osc = b == ']'; _oscLength = 0; _oscOverflow = false; _state = State.String; }
                    else { if (b == 'c') Enabled = false; _state = b == 0x1b ? State.Escape : State.Ground; }
                    break;
                case State.Csi:
                    if (b == 0x1b) { _state = State.Escape; break; }
                    if (b is >= 0x40 and <= 0x7e)
                    {
                        if (!_overflow && (b is (byte)'h' or (byte)'l') && _length > 1 && _parameters[0] == '?')
                        {
                            var values = _parameters.AsSpan(1, _length - 1);
                            int number = 0; bool valid = true, found = false;
                            foreach (byte value in values)
                            {
                                if (value == ';') { found |= number == 9001; number = 0; }
                                else if (value is >= (byte)'0' and <= (byte)'9' && number < 100000)
                                    number = number * 10 + value - '0';
                                else { valid = false; break; }
                            }
                            if (valid && (found || number == 9001)) Enabled = b == 'h';
                        }
                        _state = State.Ground;
                    }
                    else if (b >= 0x20) { if (_length < _parameters.Length) _parameters[_length++] = b; else _overflow = true; }
                    break;
                case State.String:
                    if (b == 0x1b) _state = State.StringEscape;
                    else if (_osc && b == 7) { CompleteOsc(); _state = State.Ground; }
                    else if (_osc) { if (_oscLength < _oscBytes.Length) _oscBytes[_oscLength++] = b; else _oscOverflow = true; }
                    break;
                case State.StringEscape:
                    if (b == '\\') { CompleteOsc(); _state = State.Ground; }
                    else if (_osc && b == 7) { _oscOverflow = true; _state = State.Ground; }
                    else { _oscOverflow = true; _state = b == 0x1b ? State.StringEscape : State.String; }
                    break;
            }
        }
    }
    private void CompleteOsc()
    {
        if (!_osc || _oscOverflow || Completions.Count >= 4096) return;
        var value = _oscBytes.AsSpan(0, _oscLength);
        if (!value.StartsWith("777;agwinterm;command-finished;"u8)) return;
        if (ShellCompletion.TryParse(System.Text.Encoding.UTF8.GetString(value[4..]), out string token, out long sequence))
            Completions.Enqueue((token, sequence));
    }
}
