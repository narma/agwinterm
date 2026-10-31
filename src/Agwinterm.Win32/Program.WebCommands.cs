using System.Text.Json;
using Agwinterm.Core;
using Agwinterm.Pty;

namespace Agwinterm.Win32;

internal partial class Program
{
    // Captured by every UI mutation hop, including hops into another window.
    // Only bridge dispatch installs this scope; ordinary pipe clients are unaffected.
    private static readonly AsyncLocal<Action?> WebCommandGuard = new();
    private static T GuardWebCommand<T>(Action? guard, Func<T> action)
    {
        guard?.Invoke();
        var previous = WebCommandGuard.Value;
        WebCommandGuard.Value = guard;
        try { return action(); }
        finally { WebCommandGuard.Value = previous; }
    }
    private const string DeclarativeWebScript = """
        (() => {
          if(window!==top) return;
          async function send(element, submitter) {
            let result, failed=false;
            try {
              const args=JSON.parse(element.dataset.agtermArgs || '{}');
              if(!args || typeof args!=='object' || Array.isArray(args)) throw Error('Arguments must be an object');
              const seen=new Set();
              if(element.tagName==='FORM') for(const control of element.elements) {
                if(!control.name || control.matches(':disabled')) continue;
                if(control.type==='file') throw Error('File fields are not supported');
                if(control.type==='radio'&&!control.checked) continue;
                if(['submit','button','reset','image'].includes(control.type)&&control!==submitter) continue;
                if(control.type==='number'&&control.value==='') continue;
                if(seen.has(control.name)) throw Error('Duplicate form field: '+control.name);
                seen.add(control.name);
                if(control.tagName==='SELECT'&&control.multiple&&control.selectedOptions.length>1) throw Error('Multiple values are not supported');
                args[control.name]=control.type==='checkbox'?control.checked:control.type==='number'?control.valueAsNumber:control.value;
                if(typeof args[control.name]==='number'&&!Number.isFinite(args[control.name])) throw Error('Invalid number');
              }
              const options={args};if(element.dataset.agtermTarget) options.target=element.dataset.agtermTarget;
              result=await agterm.request(element.dataset.agterm, options);
            } catch(error) {result=String(error.message||error);failed=true;}
            if(element.dataset.agtermInto) {
              const target=document.querySelector(element.dataset.agtermInto);
              if(target) target.textContent=failed?result:typeof result?.text==='string'?result.text:JSON.stringify(result);
            }
          }
          document.addEventListener('click',e=>{
            const button=e.target.closest?.('button[data-agterm],input[type=button][data-agterm]');
            if(!button||button.matches(':disabled')||button.form&&button.type!=='button')return;
            e.preventDefault();send(button,null);
          },true);
          document.addEventListener('submit',e=>{
            if(!e.target.matches('form[data-agterm]'))return;e.preventDefault();send(e.target,e.submitter);
          },true);
        })();
        """;

    private async Task BindLocalCommands(Ses owner, WebPage page, string source)
    {
        var host = page.Host;
        bool IsCurrent() => !owner.Closing && OwnsWebPage(owner, page) && ReferenceEquals(page.Host, host);
        var browser = host.Page;
        browser.Settings.IsWebMessageEnabled = true;
        string origin = new Uri(source).GetLeftPart(UriPartial.Authority);
        bool Authorized(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.GetLeftPart(UriPartial.Authority) == origin;
        await browser.AddScriptToExecuteOnDocumentCreatedAsync(ReviewScript(source, sameOrigin: true) + DeclarativeWebScript);
        if (!IsCurrent()) return;
        browser.DOMContentLoaded += (_, _) =>
        {
            if (!IsCurrent() || !Authorized(browser.Source)) return;
            page.BridgeToken = Guid.NewGuid().ToString("N");
            browser.PostWebMessageAsJson(JsonSerializer.Serialize(new { kind = "review-bind", token = page.BridgeToken }));
        };
        browser.WebMessageReceived += (_, e) =>
        {
            string? id = null;
            try
            {
                if (!IsCurrent() || page.BridgeToken is null || !Authorized(e.Source))
                    throw new ArgumentException("local document is no longer authorized");
                var request = WebBridgeRequest.Parse(e.WebMessageAsJson, page.BridgeToken);
                id = request.Id;
                long generation = page.DocumentGeneration;
                _ = Task.Run(() =>
                {
                    string reply;
                    try
                    {
                        var envelope = InvokeOnUiQueued(() =>
                        {
                            if (!IsCurrent() || page.DocumentGeneration != generation) throw new ArgumentException("stale page request");
                            return BuildWebCommand(page, request);
                        });
                        if (envelope is null) reply = JsonSerializer.Serialize(new { ok = true, result = new { pageID = page.Id } });
                        else
                        {
                            Action guard = () =>
                            {
                                if (!IsCurrent() || page.DocumentGeneration != generation) throw new ArgumentException("stale page request");
                            };
                            WebCommandGuard.Value = guard;
                            try
                            {
                                using var dispatcher = new ControlServer(this, this, sessionCommandExecutor: fn => InvokeOnUiQueued(fn));
                                reply = dispatcher.Dispatch(envelope);
                            }
                            finally { WebCommandGuard.Value = null; }
                        }
                    }
                    catch (Exception ex) { reply = JsonSerializer.Serialize(new { ok = false, error = ex.Message }); }
                    PostVerb(() =>
                    {
                        if (!IsCurrent() || page.DocumentGeneration != generation) return;
                        using var result = JsonDocument.Parse(reply);
                        var fields = result.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
                        fields["id"] = request.Id;
                        browser.PostWebMessageAsJson(JsonSerializer.Serialize(fields));
                    });
                });
            }
            catch (Exception ex) when (ex is ArgumentException or JsonException or InvalidOperationException)
            {
                if (!IsCurrent()) return;
                if (id is null)
                {
                    try { using var rejected = JsonDocument.Parse(e.WebMessageAsJson); if (rejected.RootElement.TryGetProperty("id", out var v) && v.ValueKind == JsonValueKind.String && v.GetString()!.Length <= 64) id = v.GetString(); }
                    catch (JsonException) { }
                }
                browser.PostWebMessageAsJson(JsonSerializer.Serialize(new { id, ok = false, error = ex.Message }));
            }
        };
    }

    private string? BuildWebCommand(WebPage page, WebBridgeRequest request)
    {
        string command = request.Method;
        var args = request.Args.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
        if (command == "session.overlay.submit")
        {
            if (request.Target is not null && request.Target != page.Owner.Id || request.Window is not null && request.Window != Id)
                throw new ArgumentException("submit must address the originating page");
            if (!request.Args.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.String ||
                System.Text.Encoding.UTF8.GetByteCount(value.GetString()!) > ReviewBridgeRequest.MaxValueBytes)
                throw new ArgumentException("submit requires a string value no larger than 1 MiB");
            if (!CompleteWebOutcome(page, value.GetString())) throw new ArgumentException("page has already completed");
            PostVerb(() => { if (OwnsWebPage(page.Owner, page)) CloseWebPage(page.Owner, page.Pane); });
            return null;
        }
        string? target = request.Target, window = request.Window;
        bool overlay = command.StartsWith("session.overlay.", StringComparison.Ordinal);
        if (overlay)
        {
            string action = command[16..];
            if (args.ContainsKey("action")) throw new ArgumentException("duplicate overlay action");
            args["action"] = action; command = "session.overlay";
            if (request.Target is null && request.Window is null && page.Pane is not null && !args.ContainsKey("pane"))
                args["pane"] = OverlayPanes.Word(page.Owner.AllPanes.ToList().IndexOf(page.Pane));
            if (action == "reload" && !args.ContainsKey("current")) args["current"] = true;
        }
        bool global = command.StartsWith("browser.", StringComparison.Ordinal) || command is "recent.list" or "recent.clear" || command.StartsWith("hooks.", StringComparison.Ordinal);
        if (target is null && window is null && (command.StartsWith("session.", StringComparison.Ordinal) && command is not ("session.new" or "session.go") || command is "notify" or "font" || command.StartsWith("font.", StringComparison.Ordinal))) target = page.Owner.Id;
        if (request.Target is null && request.Window is null && page.Pane is not null && WebBridgeRequest.TargetsPane(command)) target = page.Pane.Id;
        if (target is null && command.StartsWith("window.", StringComparison.Ordinal) && command is not ("window.new" or "window.list")) target = window ?? Id;
        if (!global && request.Target is null && window is null && !command.StartsWith("window.", StringComparison.Ordinal)) window = Id;
        var envelope = new Dictionary<string, object?> { ["cmd"] = command, ["args"] = args };
        if (target is not null) envelope["target"] = target;
        if (window is not null) envelope["window"] = window;
        return JsonSerializer.Serialize(envelope);
    }
}
