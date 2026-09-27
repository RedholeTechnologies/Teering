using Microsoft.Data.Sqlite;

namespace Treering.Core;

/// <summary>칩 평면도의 한 칸 — 영역 안에 든 블록. 크기는 줄 수의 어림값이다.</summary>
public sealed record DieBlock(long Id, string Display, SymbolKind Kind, double Lines);

/// <param name="Lines">상자마다 줄 수의 어림값. 바깥 상자는 없다 — 칩 가장자리의 패드일 뿐이다.</param>
/// <param name="Blocks">우리 상자마다 그 안의 블록. 한 단 아래 설계도의 상자들이다.</param>
public sealed record DieResult(
    BlueprintResult Sheet,
    IReadOnlyDictionary<long, double> Lines,
    IReadOnlyDictionary<long, IReadOnlyList<DieBlock>> Blocks);

/// <summary>
/// 설계도를 칩 평면도로 볼 때 필요한 크기. 영역의 넓이가 코드의 양이다.
///
/// 색인에는 파일 길이가 없다. 정의가 적힌 줄은 있으므로, 파일에서 가장 뒤에 정의된 줄을 그 파일의
/// 길이로 보고, 그 파일에 정의된 심볼들이 길이를 똑같이 나눠 가진다. 파일 하나에 타입 하나인
/// C# 은 거의 그대로이고, 모듈 하나에 함수가 여럿인 Python 은 함수 수만큼 나뉜다. 정확한 줄 수가
/// 아니라 «어디가 무거운가» 를 보는 값이다 — 화면에도 어림값이라고 적는다.
/// </summary>
public static class Die
{
    private const int BlockLimit = 12;

    public static DieResult? Of(
        SqliteConnection db, long? parentId, int? atOrd = null, int? fromOrd = null, bool ownOnly = false)
    {
        var sheet = Blueprint.Of(db, parentId, atOrd, fromOrd, ownOnly);
        if (sheet is null) return null;

        var at = atOrd ?? Latest(db);
        var sizes = Sizes.Measure(db, at);

        var lines = new Dictionary<long, double>();
        var blocks = new Dictionary<long, IReadOnlyList<DieBlock>>();
        foreach (var box in sheet.Boxes.Where(box => !box.Outside))
        {
            lines[box.Id] = sizes.Of(box.Id, box.Kind);
            blocks[box.Id] = BlocksOf(db, box, sizes, ownOnly);
        }

        return new DieResult(sheet, lines, blocks);
    }

    /// <summary>한 상자 안의 블록: 모듈이면 그 무리, 네임스페이스면 한 단 아래 네임스페이스와 타입.</summary>
    private static IReadOnlyList<DieBlock> BlocksOf(SqliteConnection db, BlueprintBox box, Sizes sizes, bool ownOnly)
    {
        var blocks = box.Kind switch
        {
            SymbolKind.Package => Tree.Groups(db, box.Id)
                .Select(group => new DieBlock(group.Id, group.Display, SymbolKind.Namespace, sizes.Of(group.Id, SymbolKind.Namespace))),
            SymbolKind.Namespace => Tree.Children(db, box.Id, ownOnly).Nodes
                .Where(node => node.Kind is SymbolKind.Namespace or SymbolKind.Type)
                .Select(node => new DieBlock(node.Id, node.Display, node.Kind, sizes.Of(node.Id, node.Kind))),
            _ => [],
        };

        return blocks.Where(block => block.Lines > 0)
            .OrderByDescending(block => block.Lines).ThenBy(block => block.Display, StringComparer.Ordinal)
            .Take(BlockLimit).ToList();
    }

    /// <summary>모듈 · 네임스페이스(그 아래 전부) · 타입마다 줄 수의 어림값.</summary>
    private sealed class Sizes
    {
        private readonly Dictionary<long, double> _modules = [];
        private readonly Dictionary<long, double> _namespaces = [];
        private readonly Dictionary<long, double> _types = [];

        public double Of(long id, SymbolKind kind) => kind switch
        {
            SymbolKind.Package => _modules.GetValueOrDefault(id),
            SymbolKind.Namespace => _namespaces.GetValueOrDefault(id),
            SymbolKind.Type => _types.GetValueOrDefault(id),
            _ => 0,
        };

        public static Sizes Measure(SqliteConnection db, int at)
        {
            var sizes = new Sizes();
            using (var command = db.CreateCommand())
            {
                // 타입 · 메서드 · 필드만 센다. 매개변수와 지역 변수는 그것을 담은 멤버의 몫이다.
                command.CommandText = """
                    WITH live AS (
                        SELECT d.file_id, d.line, s.module_id, s.namespace_id, s.type_id
                        FROM definition d JOIN symbol s ON s.id = d.symbol_id
                        WHERE d.born_ord <= $at AND (d.died_ord IS NULL OR d.died_ord > $at) AND s.kind IN (3, 4, 5)
                    ),
                    files AS (SELECT file_id, MAX(line) + 1 AS length, COUNT(*) AS n FROM live GROUP BY file_id)
                    SELECT l.module_id, l.namespace_id, l.type_id, SUM(f.length * 1.0 / f.n)
                    FROM live l JOIN files f ON f.file_id = l.file_id
                    GROUP BY 1, 2, 3
                    """;
                command.Parameters.AddWithValue("$at", at);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var share = reader.GetDouble(3);
                    if (!reader.IsDBNull(0)) Add(sizes._modules, reader.GetInt64(0), share);
                    if (!reader.IsDBNull(1)) Add(sizes._namespaces, reader.GetInt64(1), share);
                    if (!reader.IsDBNull(2)) Add(sizes._types, reader.GetInt64(2), share);
                }
            }

            // 네임스페이스의 크기는 그 아래 네임스페이스까지 전부다 — 폴더가 그 안의 폴더를 품듯이.
            var container = new Dictionary<long, long?>();
            using (var command = db.CreateCommand())
            {
                command.CommandText = "SELECT id, container_id FROM symbol WHERE kind = 2";
                using var reader = command.ExecuteReader();
                while (reader.Read()) container[reader.GetInt64(0)] = reader.IsDBNull(1) ? null : reader.GetInt64(1);
            }

            var own = sizes._namespaces.ToList();
            foreach (var (ns, size) in own)
            {
                var up = container.GetValueOrDefault(ns);
                for (var guard = 0; up is { } parent && container.ContainsKey(parent) && guard < 64; guard++)
                {
                    Add(sizes._namespaces, parent, size);
                    up = container.GetValueOrDefault(parent);
                }
            }

            return sizes;
        }

        private static void Add(Dictionary<long, double> sums, long id, double value) =>
            sums[id] = sums.GetValueOrDefault(id) + value;
    }

    private static int Latest(SqliteConnection db)
    {
        using var command = db.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(ord), 0) FROM snapshot";
        return Convert.ToInt32(command.ExecuteScalar());
    }
}
