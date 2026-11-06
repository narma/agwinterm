namespace Agwinterm.Pty.Tests;

public sealed class OwnerOnlyHostTests
{
    [Theory]
    [InlineData("managed",false)]
    [InlineData("rust",false)]
    [InlineData("ghostty",false)]
    [InlineData("managed",true)]
    public void ShippedHostRejectsRetiredCreationBeforeSpawning(string core,bool singleOwner)
    {
        string id="agwinterm-owner-only-test-"+Guid.NewGuid().ToString("N");
        using var server=new PtyHostServer(id,ghosttyOnly:true);
        using var client=PtyHostClient.Connect(id);
        var error=Assert.Throws<InvalidOperationException>(()=>client.Create("pane",80,24,
            "must-not-spawn.exe",[],emulatorCore:core,singleOwner:singleOwner));
        Assert.Contains("only single-owner Ghostty",error.Message);
        Assert.Empty(client.List());
    }
}
