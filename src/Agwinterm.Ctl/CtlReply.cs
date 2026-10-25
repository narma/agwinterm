using System.Text.Json;

namespace Agwinterm.Ctl;

/// <summary>Ordinary replies: output format never determines success.</summary>
public static class CtlReply
{
    public static int Print(string response, bool json, bool textField, TextWriter output, TextWriter error)
    {
        try
        {
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("ok", out var ok) ||
                ok.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new JsonException("reply must contain a boolean ok field");
            if (json) output.WriteLine(response);
            else if (ok.GetBoolean())
            {
                if (root.TryGetProperty("result", out var result))
                {
                    if (textField && result.ValueKind == JsonValueKind.Object &&
                        result.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                        output.WriteLine(text.GetString());
                    else output.WriteLine(result.ValueKind == JsonValueKind.String ? result.GetString() : result.GetRawText());
                }
            }
            else error.WriteLine(root.TryGetProperty("error", out var message) && message.ValueKind == JsonValueKind.String
                ? message.GetString() : "control request refused");
            return ok.GetBoolean() ? 0 : 1;
        }
        catch (JsonException ex)
        {
            error.WriteLine("invalid control reply: " + ex.Message);
            return 1;
        }
    }
}
