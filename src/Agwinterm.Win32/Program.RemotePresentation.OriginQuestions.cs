using System.Text.Json;
using Agwinterm.Core;
using Agwinterm.Pty;

namespace Agwinterm.Win32;

internal partial class Program
{
    private sealed record OriginQuestion(PendingAsk Request, AgtermOriginPresentationScope Scope,
        AgtermPresentationBroker.Subscription Subscriber, long Owner, Action<AskOutcome>? Completed);
    private OriginQuestion? _originQuestion;

    private bool TryDelegateOriginQuestion(PendingAsk request, Action<AskOutcome>? completed)
    {
        if (request.Session is null) return false;
        foreach (var (scope, entry) in _originPresentations.Where(p => p.Key.Session == request.Session).ToArray())
        {
            if (!OriginPresentationCurrent(scope, entry) || _presentationBroker.Presenter(scope) is not { } presenter) continue;
            try
            {
                var payload = AgtermQuestionAdapter.ToOrigin(request.Id, presenter.Owner, request.Spec, presenter.Subscriber.Kinds);
                _originQuestion = new(request, scope, presenter.Subscriber, presenter.Owner, completed);
                if (_presentationBroker.SendPresenter(scope, presenter.Subscriber, presenter.Owner, "ask.request", payload))
                { entry.Session.PresentationError = null; return true; }
                _originQuestion = null;
            }
            catch (IOException ex)
            { _originQuestion = null; entry.Session.PresentationError = "Question remains local: " + ex.Message; }
        }
        return false;
    }

    private void FinishOriginQuestion(string id)
    {
        if (_originQuestion is not { } question || question.Request.Id != id) return;
        _originQuestion = null;
        _presentationBroker.SendPresenter(question.Scope, question.Subscriber, question.Owner, "ask.dismiss",
            JsonSerializer.SerializeToElement(new { id, owner = question.Owner }));
    }

    private void ReceiveOriginQuestion(AgtermOriginPresentationScope scope, AgtermPresentationBroker.Subscription subscriber,
        OriginPresentation entry, AgtermPresentationFrame frame)
    {
        if (!OriginPresentationCurrent(scope, entry)) throw new IOException("Question origin no longer current");
        if (_originQuestion is not { } pending || pending.Scope != scope || !ReferenceEquals(pending.Subscriber, subscriber)) return;
        var payload = frame.Body;
        if (payload.GetProperty("id").GetString() != pending.Request.Id || payload.GetProperty("owner").GetInt64() != pending.Owner
            || !_presentationBroker.Authorizes(scope, subscriber, pending.Owner)) return;
        if (frame.Kind == "ask.rejected") { ReturnOriginQuestion(scope, subscriber); return; }
        if (frame.Kind != "ask.resolve") throw new IOException("Unsupported question response");
        var outcome = AgtermQuestionAdapter.Resolve(pending.Request.Spec, pending.Request.Id, pending.Owner, payload);
        bool resolved = Asks.Resolve(pending.Request.Id, outcome);
        FinishOriginQuestion(pending.Request.Id);
        if (resolved) pending.Completed?.Invoke(outcome);
    }

    private void ReturnOriginQuestion(AgtermOriginPresentationScope scope, AgtermPresentationBroker.Subscription subscriber)
    {
        if (_originQuestion is not { } pending || pending.Scope != scope || !ReferenceEquals(pending.Subscriber, subscriber)) return;
        FinishOriginQuestion(pending.Request.Id);
        if (Asks.Find(pending.Request.Id)?.Outcome.Result != "pending") return;
        if (_uiGone.IsCancellationRequested || !AllSessions().Any(s => s.Id == pending.Request.Session && !s.Closing))
        { Asks.Resolve(pending.Request.Id, new("cancelled", "owner-closed")); return; }
        try
        {
            if (NativeDialogOpen || _setOpen || _editing is not null || _dashboardOpen || _menuHwnd != IntPtr.Zero)
                throw new InvalidOperationException("Local modal slot unavailable");
            ShowLocalQuestion(pending.Request, false, () => Asks.Find(pending.Request.Id)?.Outcome.Result == "pending", pending.Completed, retainOnFailure: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Asks.Resolve(pending.Request.Id, new("cancelled", "closed"));
        }
    }
}
