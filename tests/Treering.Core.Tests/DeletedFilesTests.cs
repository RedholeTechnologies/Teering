using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Scip;
using Treering.Core;

namespace Treering.Core.Tests;

/// <summary>
/// A file deleted between two updates. The new index does not contain it, so unless the update
/// says so, nothing it defined ever dies - after a folder is moved the map keeps the whole old
/// layout next to the new one, and "our code" counts both.
/// </summary>
public sealed class DeletedFilesTests : IDisposable
{
    private const string Prefix = "scip-dotnet nuget Demo 1.0.0.0 ";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tr-root-{Guid.NewGuid():N}");
    private readonly string _scipPath = Path.Combine(Path.GetTempPath(), $"tr-{Guid.NewGuid():N}.scip");
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"tr-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _scipPath, _dbPath })
        {
            if (File.Exists(path)) File.Delete(path);
        }

        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>The project folder on disk, holding exactly these files.</summary>
    private void OnDisk(params string[] files)
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        Directory.CreateDirectory(_root);
        foreach (var file in files) File.WriteAllText(Path.Combine(_root, file), "class X {}");
    }

    private static Document Source(string path, string type, string prefix = Prefix)
    {
        var document = new Document { RelativePath = path, Language = "C#" };
        foreach (var (descriptors, line) in new[] { ($"A/{type}#", 0), ($"A/{type}#Run().", 1) })
        {
            var occurrence = new Occurrence { Symbol = prefix + descriptors, SymbolRoles = (int)SymbolRole.Definition };
#pragma warning disable CS0612, CS0618
            occurrence.Range.Add(line);
            occurrence.Range.Add(0);
            occurrence.Range.Add(3);
#pragma warning restore CS0612, CS0618
            document.Occurrences.Add(occurrence);
        }

        return document;
    }

    private void Write(int ord, bool partial, IEnumerable<string>? gone, params Document[] documents)
    {
        var index = new Scip.Index { Metadata = new Metadata { ProjectRoot = new Uri(_root).AbsoluteUri } };
        index.Documents.AddRange(documents);
        using (var stream = File.Create(_scipPath)) index.WriteTo(stream);

        using var db = GraphDb.Open(_dbPath);
        Loader.Load(db, _scipPath, ord, indexer: "test", partial, gone);
    }

    private bool Alive(string display)
    {
        using var db = GraphDb.Open(_dbPath);
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM symbol s JOIN symbol_life l ON l.symbol_id = s.id
            WHERE s.display = $display AND s.kind = 3 AND l.died_ord IS NULL
            """;
        command.Parameters.AddWithValue("$display", display);
        return Convert.ToInt32(command.ExecuteScalar()) > 0;
    }

    private string Under(string root, params string[] parts) => Path.Combine([root, .. parts]);

    [Fact]
    public void What_a_deleted_file_defined_dies_with_it()
    {
        Write(0, partial: false, gone: null, Source("Left.cs", "Left"), Source("Right.cs", "Right"));

        // Right.cs was deleted; only Left.cs is left to index.
        Write(1, partial: true, gone: [Under(_root, "Right.cs")], Source("Left.cs", "Left"));

        Assert.False(Alive("Right"));
        Assert.True(Alive("Left"));
    }

    [Fact]
    public void Without_being_told_a_partial_update_keeps_what_it_did_not_see()
    {
        // The other side of the promise: a file merely absent from a partial index may belong
        // to a project that was not re-indexed this time. It is not ours to retire.
        Write(0, partial: false, gone: null, Source("Left.cs", "Left"), Source("Right.cs", "Right"));

        Write(1, partial: true, gone: [], Source("Left.cs", "Left"));

        Assert.True(Alive("Right"));
    }

    [Fact]
    public void A_file_git_never_tracked_is_retired_once_it_is_gone_from_disk()
    {
        // Indexed while untracked, then deleted: no status line and no diff ever names it.
        OnDisk("Left.cs", "Right.cs");
        Write(0, partial: false, gone: null, Source("Left.cs", "Left"), Source("Right.cs", "Right"));

        OnDisk("Left.cs");
        Write(1, partial: true, gone: [], Source("Left.cs", "Left"));

        Assert.False(Alive("Right"));
        Assert.True(Alive("Left"));
    }

    [Fact]
    public void When_the_index_does_not_match_the_disk_nothing_is_swept_away()
    {
        // An index made on another machine, or a root that means something else here: its own
        // documents are not on disk either, so "missing" proves nothing.
        OnDisk();
        Write(0, partial: false, gone: null, Source("Left.cs", "Left"), Source("Right.cs", "Right"));

        Write(1, partial: true, gone: [], Source("Left.cs", "Left"));

        Assert.True(Alive("Right"));
    }

    [Fact]
    public void Another_projects_file_is_not_swept_away_for_being_missing_here()
    {
        // Its path is relative to its own project's root, so under this root it looks missing.
        OnDisk("Left.cs");
        Write(0, partial: false, gone: null,
            Source("Left.cs", "Left"), Source("Right.cs", "Right", prefix: "scip-dotnet nuget Other 1.0.0.0 "));

        Write(1, partial: true, gone: [], Source("Left.cs", "Left"));

        Assert.True(Alive("Right"));
    }

    [Fact]
    public void A_deleted_file_is_found_whichever_separator_the_indexer_wrote()
    {
        // scip-python on Windows writes "pkg\right.py"; git and the file system give "pkg/right.py".
        Write(0, partial: false, gone: null, Source(@"pkg\Left.cs", "Left"), Source(@"pkg\Right.cs", "Right"));

        Write(1, partial: true, gone: [Under(_root, "pkg", "Right.cs")], Source(@"pkg\Left.cs", "Left"));

        Assert.False(Alive("Right"));
    }

    [Fact]
    public void A_file_of_the_same_name_deleted_in_another_project_is_left_alone()
    {
        // Stored paths are relative to each index's own root. Another project in the same repo
        // can hold a Right.cs too; deleting that one must not take this one's code with it.
        Write(0, partial: false, gone: null, Source("Left.cs", "Left"), Source("Right.cs", "Right"));

        var elsewhere = Path.Combine(Path.GetTempPath(), $"tr-other-{Guid.NewGuid():N}");
        Write(1, partial: true, gone: [Under(elsewhere, "Right.cs")], Source("Left.cs", "Left"));

        Assert.True(Alive("Right"));
    }
}
