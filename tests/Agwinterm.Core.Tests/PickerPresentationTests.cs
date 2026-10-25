using System.Text.Json;
using Agwinterm.Core;

namespace Agwinterm.Core.Tests;

public class PickerPresentationTests
{
    private static PickSpec Parse(string json) { using var d = JsonDocument.Parse(json); return PickSpec.Parse(d.RootElement); }
    [Fact] public void InitialSelectionUsesIdentityAndQueryEditsReset()
    {
        var s = new PickSelection(Parse("""{"items":[{"id":"a","label":"same"},{"id":"","label":"same","status":"ready","color":"#123aBc","shape":"diamond"}],"selectedId":""}"""));
        Assert.Equal("", s.Choose()!.Id); Assert.Equal(1, s.Choose()!.Index);
        s.SetQuery("same"); Assert.Equal("a", s.Choose()!.Id);
        s.SetQuery("ready"); Assert.Empty(s.Matches);
    }
    [Fact] public void ExcludedInitialIdFallsBackToFirstMatch()
    {
        var s = new PickSelection(Parse("""{"items":[{"id":"a","label":"apple"},{"id":"b","label":"banana"}],"selectedId":"b","query":"apple"}"""));
        Assert.Equal("a", s.Choose()!.Id);
    }
    [Theory]
    [InlineData("\"selectedId\":\"A\"")]
    [InlineData("\"items\":[{\"id\":\"a\",\"label\":\"x\",\"color\":\"red\"}]")]
    [InlineData("\"items\":[{\"id\":\"a\",\"label\":\"x\",\"shape\":\"triangle\"}]")]
    [InlineData("\"items\":[{\"id\":\"a\",\"label\":\"x\",\"status\":\"bad\\nstatus\"}]")]
    public void InvalidMetadataRefuses(string field) => Assert.Throws<ArgumentException>(() => Parse("{\"items\":[{\"id\":\"a\",\"label\":\"x\"}]," + field + "}"));
}
