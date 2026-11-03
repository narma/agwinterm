using System.Text.Json;
using Agwinterm.Core;
using Agwinterm.Pty;

namespace Agwinterm.Win32;

internal partial class Program
{
    private sealed class ReplicaQuestion(Ses session, PresentationReplica replica, AgtermQuestion question)
    {
        public readonly Ses Session = session;
        public readonly PresentationReplica Replica = replica;
        public readonly AgtermQuestion Question = question;
        public string? LocalId;
        public bool Suppress;
    }
    private ReplicaQuestion? _replicaQuestion;

    private bool ApplyRemoteQuestion(Ses session, PresentationReplica replica, AgtermPresentationFrame frame,
        IReadOnlyDictionary<Guid, string> mapping)
    {
        var sender = replica.Sender ?? throw new IOException("Missing response channel");
        if (frame.Kind == "ask.dismiss")
        {
            if (_replicaQuestion is { } active && ReferenceEquals(active.Replica, replica)
                && frame.Body.GetProperty("id").GetString() == active.Question.Id && frame.Body.GetProperty("owner").GetInt64() == active.Question.Owner)
                DismissReplicaQuestion(replica);
            return true;
        }
        if (frame.Kind == "overlay.request")
        {
            // Basic role messages have no feature flag. Refuse until the scoped job channel is implemented.
            sender.Send("overlay.rejected", JsonSerializer.SerializeToElement(new { job = frame.Body.GetProperty("job").GetString() }));
            return true;
        }
        if (frame.Kind != "ask.request") return false;
        string id = frame.Body.GetProperty("id").GetString() ?? throw new IOException("Missing remote question ID");
        long owner = frame.Body.GetProperty("owner").GetInt64();
        if (id.Length is < 1 or > 256 || owner < 1) throw new IOException("Invalid question reference");
        try
        {
            if (!sender.Granted) throw new IOException("No presenter authority");
            var question = AgtermQuestionAdapter.FromOrigin(frame.Body, mapping, sender.Kinds);
            if (_replicaQuestion is not null) throw new IOException("Window already presents a remote question");
            var active = new ReplicaQuestion(session, replica, question);
            _replicaQuestion = active;
            try
            {
                active.LocalId = OpenAskCore(question.Spec, false, () => PresentationCurrent(session, replica), outcome =>
                {
                    if (active.Suppress) return;
                    if (ReferenceEquals(_replicaQuestion, active)) _replicaQuestion = null;
                    if (!PresentationCurrent(session, replica)) return;
                    long attempt = session.PresentationQuestion.Begin(question.Id);
                    try { sender.Send("ask.resolve", AgtermQuestionAdapter.Answer(question, outcome)); }
                    catch (IOException) { session.PresentationQuestion.Observe(attempt, "uncertain"); }
                    _ = ReadRemoteQuestionOutcomeAsync(session, replica, question, attempt);
                }, session.Id);
            }
            catch { if (ReferenceEquals(_replicaQuestion, active)) _replicaQuestion = null; throw; }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
        {
            sender.Send("ask.rejected", JsonSerializer.SerializeToElement(new { id, owner }));
            session.PresentationError = "Remote question refused: " + ex.Message;
        }
        return true;
    }

    private void DismissReplicaQuestion(PresentationReplica replica)
    {
        if (_replicaQuestion is not { } active || !ReferenceEquals(active.Replica, replica)) return;
        active.Suppress = true; _replicaQuestion = null;
        if (active.LocalId is { } id)
        {
            Asks.Resolve(id, new("cancelled", "owner-closed"));
            if (_nativeAsk?.Request.Id == id) { var ui = _nativeAsk; _nativeAsk = null; ui.Dispose(); }
        }
        RequestRedraw();
    }

    private async Task ReadRemoteQuestionOutcomeAsync(Ses session, PresentationReplica replica, AgtermQuestion question, long deliveryAttempt)
    {
        var ct = replica.Stop.Token;
        try
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200 + attempt * 500), ct).ConfigureAwait(false);
                var result = await _remoteOperations.ReadQuestionResultAsync(replica.Bindings[0], question.Id, ct).ConfigureAwait(false);
                string status = result.GetProperty("result").GetString()!;
                if (status == "pending") continue;
                if (status == "answered")
                {
                    var answer = new Dictionary<string, object?> { ["id"] = question.Id, ["owner"] = question.Owner, ["button"] = result.GetProperty("id").GetString() };
                    foreach (var name in new[] { "text", "selected" }) if (result.TryGetProperty(name, out var value)) answer[name] = value.Clone();
                    _ = AgtermQuestionAdapter.Resolve(question.Spec, question.Id, question.Owner, JsonSerializer.SerializeToElement(answer));
                }
                await PresentationUiAsync(() =>
                {
                    if (PresentationCurrent(session, replica)) session.PresentationQuestion.Observe(deliveryAttempt, status);
                }, ct).ConfigureAwait(false);
                return;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or JsonException or KeyNotFoundException or ArgumentException or FormatException or OperationCanceledException)
        { /* Read failure leaves uncertainty; never resend the answer. */ }
        try { await PresentationUiAsync(() =>
        {
            if (PresentationCurrent(session, replica)) session.PresentationQuestion.Observe(deliveryAttempt, "uncertain");
        }, ct).ConfigureAwait(false); } catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
}
