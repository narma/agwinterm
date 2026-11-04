namespace Agwinterm.Core.Tests;

public class ShellCompletionTests : IDisposable
{
    private const string Token = "0123456789abcdef0123456789abcdef";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "agwinterm-completion-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

    [Fact]
    public void PrintedUntrustedAndReplayedMarkersCannotAdvanceTheCheckpoint()
    {
        string path = Path.Combine(_directory, Token);
        var host = new ShellCompletion(Token, path);
        Assert.False(host.Accept("fedcba9876543210fedcba9876543210", 999, true));
        Assert.False(host.Accept("", 1, true));
        Assert.True(host.Accept(Token, 2, true));
        Assert.False(host.Accept(Token, 1, true));
        Assert.False(host.Accept(Token, 2, true));
        Assert.Equal(2, ShellCompletion.Read(path));
        // An attached UI consumes the host's current checkpoint, but not an old
        // transcript from before it attached. Neither overwrites durable state.
        var ui = new ShellCompletion(Token, path);
        Assert.False(ui.Accept(Token, 1, false));
        Assert.True(ui.Accept(Token, 2, false));
        Assert.Equal(2, ui.Checkpoint);
    }

    [Fact]
    public void HostCompletionSurvivesTheUiAndHostLifetimes()
    {
        string path = Path.Combine(_directory, Token);
        var host = new ShellCompletion(Token, path);
        host.Accept(Token, 1, true); // initial prompt
        long bindingGeneration = host.Checkpoint;
        Assert.Equal(1, bindingGeneration);
        host.Accept(Token, 2, true); // agent exits while the UI is detached
        Assert.True(ShellCompletion.Read(path) > bindingGeneration);
        Assert.Equal(2, new ShellCompletion(Token, path).Checkpoint);
    }

    [Fact]
    public async Task ReplayReadinessWaitsForTheActualFirstPrompt()
    {
        var completion = new ShellCompletion(Token, Path.Combine(_directory, Token));
        Task<bool> ready = completion.WaitForInitialPromptAsync(TimeSpan.FromSeconds(5));
        Assert.False(ready.IsCompleted);
        Assert.False(completion.Accept("wrong", 1, false));
        Assert.False(ready.IsCompleted);
        Assert.True(completion.Accept(Token, 1, true));
        Assert.True(await ready);
        Assert.True(await completion.WaitForInitialPromptAsync(TimeSpan.Zero));
        // The same workload object remains ready through a transport replacement;
        // a genuinely new shell gets its own token, counter and readiness gate.
        var fresh = new ShellCompletion("fedcba9876543210fedcba9876543210", Path.Combine(_directory, "fresh"));
        Assert.False(await fresh.WaitForInitialPromptAsync(TimeSpan.FromMilliseconds(10)));
    }

    [Theory]
    [InlineData("agwinterm;command-finished")]
    [InlineData("agwinterm;command-finished;0123456789abcdef0123456789abcdef;-1")]
    [InlineData("agwinterm;command-finished;0123456789abcdef0123456789abcdef;1;extra")]
    public void MalformedOutputIsNotACompletion(string text) => Assert.False(ShellCompletion.TryParse(text, out _, out _));
}
