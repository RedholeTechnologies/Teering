using Microsoft.Data.Sqlite;
using Treering.Core;

namespace Treering.Core.Tests;

/// <summary>
/// The sample project is what someone sees first, before they have imported anything. If it comes
/// up empty, or its history shows nothing changing, the first look says the tool does nothing.
/// </summary>
[Collection("TREERING_HOME")]
public sealed class SampleTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), $"tr-home-{Guid.NewGuid():N}");
    private readonly string? _previousHome = Environment.GetEnvironmentVariable("TREERING_HOME");

    public SampleTests() => Environment.SetEnvironmentVariable("TREERING_HOME", _home);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Environment.SetEnvironmentVariable("TREERING_HOME", _previousHome);
        try { Directory.Delete(_home, recursive: true); } catch { /* a temp folder left behind is harmless */ }
    }

    private static BlueprintResult Sheet(ProjectInfo sample, int? at = null, int? from = null)
    {
        using var db = GraphDb.Open(sample.Db);
        return Blueprint.Of(db, null, at, from)!;
    }

    [Fact]
    public void The_sample_is_listed_as_a_project()
    {
        var sample = Sample.Install();

        Assert.Contains(Projects.List(), project => project.Id == sample.Id && project.Name == Sample.Name);
    }

    [Fact]
    public void Its_top_sheet_has_the_shops_modules_and_the_tests_apart()
    {
        var sheet = Sheet(Sample.Install());

        var ours = sheet.Boxes.Where(box => !box.Outside).ToList();
        Assert.Equal(["Acme.Billing", "Acme.Data", "Acme.Shop", "Acme.Shop.Tests", "Acme.Web"], ours.Select(box => box.Display).Order());
        Assert.True(ours.Single(box => box.Display == "Acme.Shop.Tests").Test);
        Assert.Contains(sheet.Links, link => link.Kind == EdgeKind.Inherit);
    }

    [Fact]
    public void Its_history_shows_the_design_changing()
    {
        var sample = Sample.Install();

        var sheet = Sheet(sample, at: 1, from: 0);
        var billing = sheet.Boxes.Single(box => box.Display == "Acme.Billing");
        var web = sheet.Boxes.Single(box => box.Display == "Acme.Web");
        var data = sheet.Boxes.Single(box => box.Display == "Acme.Data");

        Assert.Equal(EdgeState.Born, billing.State);
        Assert.Contains(sheet.Links, link => link.From == web.Id && link.To == data.Id && link.State == EdgeState.Died);
    }

    [Fact]
    public void Its_regions_have_sizes_for_the_chip()
    {
        var sample = Sample.Install();

        using var db = GraphDb.Open(sample.Db);
        var die = Die.Of(db, null)!;

        Assert.All(die.Sheet.Boxes.Where(box => !box.Outside), box => Assert.True(die.Lines[box.Id] > 0, box.Display));
    }

    [Fact]
    public void Adding_it_again_starts_it_afresh()
    {
        var first = Sample.Install();
        var again = Sample.Install();

        Assert.Equal(first.Id, again.Id);
        using var db = GraphDb.Open(again.Db);
        using var command = db.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM snapshot";
        Assert.Equal(2L, command.ExecuteScalar());
    }
}
