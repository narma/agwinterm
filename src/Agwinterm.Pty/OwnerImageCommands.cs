using System.Text.Json;

namespace Agwinterm.Pty;

internal static class OwnerImageCommands
{
    public static bool Supports(string command)
        => command is "image.show" or "image.sixel" or "image.clear" or "image.frame" or "image.frameshm";

    public static string Dispatch(ControlServer server,string request)
    {
        using var json=JsonDocument.Parse(request);
        if(!json.RootElement.TryGetProperty("cmd",out var command) || command.ValueKind != JsonValueKind.String ||
            !Supports(command.GetString()!)) throw new InvalidDataException("Unsupported owner image operation.");
        return server.Dispatch(request);
    }
}
