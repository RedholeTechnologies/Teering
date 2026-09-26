using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Scip;
using Treering.Core;

namespace Treering.Core.Tests;

/// <summary>
/// 패키지가 하나뿐인 코드베이스 — TypeScript 앱이 흔히 그렇다. 첫 화면을 거품 하나로 끝내지
/// 않도록 볼 만한 가지로 나누는 것, 그리고 폴더로 들어갔을 때 그 아래가 다 나오는 것.
/// </summary>
public sealed class GroupTests : IDisposable
{
    private const string Prefix = "scip-typescript npm web 1.0.0 ";

    private readonly string _scipPath = Path.Combine(Path.GetTempPath(), $"tr-{Guid.NewGuid():N}.scip");
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"tr-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _scipPath, _dbPath })
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static Occurrence Define(string descriptors)
    {
        var occurrence = new Occurrence { Symbol = Prefix + descriptors, SymbolRoles = (int)SymbolRole.Definition };
#pragma warning disable CS0612, CS0618
        occurrence.Range.Add(0);
        occurrence.Range.Add(0);
        occurrence.Range.Add(3);
#pragma warning restore CS0612, CS0618
        return occurrence;
    }

    /// <summary>
    /// web/                         (one package)
    ///   next.config.ts   Config
    ///   src/app/page.tsx Page        src/app/layout.tsx Layout
    ///   src/lib/db.ts    Db          src/lib/auth.ts    Auth
    /// src holds four of the five types, so it is split into app and lib.
    /// </summary>
    private void Load(bool withChain = false)
    {
        var index = new Scip.Index { Metadata = new Metadata { ProjectRoot = "file:///web" } };
        var files = new List<(string, string)>
        {
            ("`next.config.ts`/", "Config#"),
            ("src/app/`page.tsx`/", "Page#"),
            ("src/app/`layout.tsx`/", "Layout#"),
            ("src/lib/`db.ts`/", "Db#"),
            ("src/lib/`auth.ts`/", "Auth#"),
        };
        // A folder chain that goes one way down: deep/er/x.ts.
        if (withChain) files.Add(("deep/er/`x.ts`/", "X#"));

        foreach (var (file, type) in files)
        {
            var document = new Document { RelativePath = file.Replace("`", "").TrimEnd('/'), Language = "TypeScript" };
            document.Occurrences.Add(Define(file + type));
            index.Documents.Add(document);
        }

        using (var stream = File.Create(_scipPath))
        {
            index.WriteTo(stream);
        }

        using var db = GraphDb.Open(_dbPath);
        Loader.Load(db, _scipPath, ord: 0, indexer: "test");
    }

    private static long Id(SqliteConnection db, string keySuffix)
    {
        using var command = db.CreateCommand();
        command.CommandText = "SELECT id FROM symbol WHERE key LIKE '%' || $suffix";
        command.Parameters.AddWithValue("$suffix", keySuffix);
        return (long)command.ExecuteScalar()!;
    }

    [Fact]
    public void A_branch_holding_most_of_the_package_is_split_further()
    {
        Load();
        using var db = GraphDb.Open(_dbPath);

        var groups = Tree.Groups(db, Id(db, "package web"));

        Assert.Equal(
            ["next.config.ts", "src/app", "src/lib"],
            groups.Select(group => group.Display).Order(StringComparer.Ordinal).ToList());
    }

    [Fact]
    public void Every_namespace_falls_in_the_nearest_group_above_it()
    {
        Load();
        using var db = GraphDb.Open(_dbPath);

        var app = Tree.Groups(db, Id(db, "package web")).Single(group => group.Display == "src/app");

        Assert.Contains(Id(db, "src/app/`page.tsx`/"), app.Namespaces);
        Assert.Contains(Id(db, "src/app/`layout.tsx`/"), app.Namespaces);
        Assert.DoesNotContain(Id(db, "src/lib/`db.ts`/"), app.Namespaces);
    }

    [Fact]
    public void Going_inside_a_folder_reaches_the_types_in_its_files()
    {
        Load();
        using var db = GraphDb.Open(_dbPath);

        // src/ holds no type itself - its files do. Counting only what is directly in it
        // made every folder open onto an empty screen.
        var seeds = Subgraph.SeedsUnder(db, Id(db, "web src/"), Granularity.Type);

        Assert.Equal(4, seeds.Count);
    }

    [Fact]
    public void A_one_way_folder_chain_is_one_step_joined_with_a_slash()
    {
        Load(withChain: true);
        using var db = GraphDb.Open(_dbPath);

        var top = Tree.Children(db, Id(db, "package web"), ownOnly: false).Nodes.Select(node => node.Display).ToList();

        // Folders are paths: src.lib is what nobody calls it.
        Assert.Contains("deep/er/x.ts", top);
        Assert.Contains("src", top);
    }
}
