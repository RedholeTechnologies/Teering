using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Scip;
using Treering.Core;

namespace Treering.Core.Tests;

/// <summary>
/// 「주입받는 것 · 주입되는 곳」 은 생성자가 받는 타입이다. 생성자의 이름도, 매개변수를
/// 적는 순서도 언어마다 다르다 — 둘 다 견뎌야 한다.
/// </summary>
public sealed class InjectionTests : IDisposable
{
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

    private static Occurrence At(string symbol, int line, int column, bool definition)
    {
        var occurrence = new Occurrence
        {
            Symbol = symbol,
            SymbolRoles = definition ? (int)SymbolRole.Definition : 0,
        };
#pragma warning disable CS0612, CS0618
        occurrence.Range.Add(line);
        occurrence.Range.Add(column);
        occurrence.Range.Add(column + 3);
#pragma warning restore CS0612, CS0618
        return occurrence;
    }

    /// <summary>
    /// <paramref name="ctor"/> takes a <c>Repo</c>. With <paramref name="nameFirst"/> the parameter's
    /// name is written before its type, as in <c>constructor(private repo: Repo)</c>; without it,
    /// the type comes first, as in <c>Service(Repo repo)</c>.
    /// </summary>
    private void Load(string prefix, string ctor, bool nameFirst)
    {
        var service = new Document { RelativePath = "Service.x", Language = "any" };
        service.Occurrences.Add(At(prefix + "A/Service#", 0, 0, definition: true));
        service.Occurrences.Add(At(prefix + $"A/Service#{ctor}().", 1, 2, definition: true));

        var parameter = At(prefix + $"A/Service#{ctor}().(repo)", 1, nameFirst ? 14 : 24, definition: true);
        var type = At(prefix + "A/Repo#", 1, nameFirst ? 20 : 14, definition: false);
        if (nameFirst) { service.Occurrences.Add(parameter); service.Occurrences.Add(type); }
        else { service.Occurrences.Add(type); service.Occurrences.Add(parameter); }

        var repo = new Document { RelativePath = "Repo.x", Language = "any" };
        repo.Occurrences.Add(At(prefix + "A/Repo#", 0, 0, definition: true));

        var index = new Scip.Index { Metadata = new Metadata { ProjectRoot = "file:///demo" } };
        index.Documents.Add(service);
        index.Documents.Add(repo);

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

    private (List<string> Takes, List<string> Into) Read()
    {
        using var db = GraphDb.Open(_dbPath);
        var service = SymbolDetail.Of(db, Id(db, "A/Service#"))!;
        var repo = SymbolDetail.Of(db, Id(db, "A/Repo#"))!;
        return (service.Injects.Select(item => item.Display).ToList(),
                repo.InjectedInto.Select(item => item.Display).ToList());
    }

    [Fact]
    public void CSharp_constructor_type_before_name()
    {
        Load("scip-dotnet nuget Demo 1.0.0.0 ", "`.ctor`", nameFirst: false);

        var (takes, into) = Read();

        Assert.Equal(["Repo"], takes);
        Assert.Equal(["Service"], into);
    }

    [Fact]
    public void TypeScript_constructor_name_before_type()
    {
        Load("scip-typescript npm demo 1.0.0 ", "`<constructor>`", nameFirst: true);

        var (takes, into) = Read();

        Assert.Equal(["Repo"], takes);
        Assert.Equal(["Service"], into);
    }

    [Fact]
    public void Python_constructor()
    {
        Load("scip-python python demo 1.0.0 ", "__init__", nameFirst: true);

        var (takes, into) = Read();

        Assert.Equal(["Repo"], takes);
        Assert.Equal(["Service"], into);
    }
}
