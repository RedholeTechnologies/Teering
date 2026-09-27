using Microsoft.Data.Sqlite;

namespace Treering.Core;

/// <summary>설계도의 한 장이 무엇을 상자로 삼는가.</summary>
public enum BlueprintLevel
{
    /// <summary>맨 위. 모듈이 상자다.</summary>
    Modules,

    /// <summary>모듈이나 네임스페이스 안. 그 안의 무리·네임스페이스·타입이 상자다.</summary>
    Parts,

    /// <summary>타입 하나. 그 타입과, 그 타입이 쓰고 그 타입을 쓰는 타입들이 상자다.</summary>
    Type,
}

/// <summary>상자 안의 한 줄. 부품 상자면 대표 타입, 타입 상자면 멤버다.</summary>
/// <param name="Shape">멤버일 때만. <see cref="MemberInfo.Shape"/> 와 같은 규칙이다.</param>
public sealed record BlueprintItem(
    long Id, string Display, string? Flavor, string? Shape, string? Signature, EdgeState State);

/// <param name="Role">문서 주석의 첫 문단. 이 상자가 무엇을 맡는가.</param>
/// <param name="RoleFrom">
/// 제 주석이 없어 대표 타입의 주석을 빌려 왔을 때, 그 타입의 이름. C# 은 어셈블리와 네임스페이스에
/// 주석을 달 수 없다 — 빌려 오지 않으면 윗단의 상자가 전부 비고, 빌린 것을 숨기면 거짓말이 된다.
/// </param>
/// <param name="Outside">
/// 지금 보는 것 바깥의 상자. 맨 위에서는 남의 모듈, 안에서는 선이 닿는 바깥 모듈·네임스페이스다.
/// </param>
public sealed record BlueprintBox(
    long Id, string Display, SymbolKind Kind, string? Flavor,
    string? Role, string? RoleFrom,
    IReadOnlyList<BlueprintItem> Items, int More,
    EdgeState State, bool Own, bool Test, bool Outside);

/// <param name="Labels">
/// 선이 무엇으로 이어졌는가. 쓰는 선은 쓰이는 타입, 구현 선은 구현하는 타입, 타입 한 장에서는
/// 가운데 타입의 멤버 — 굵기만으로는 «무엇을» 쓰는지 알 수 없다.
/// </param>
/// <param name="Width">
/// 이 선에 실린 이름이 모두 몇 가지인가. 이름표는 몇 개만 적으므로 따로 센다 — 회로도의 버스 폭이다.
/// </param>
public sealed record BlueprintLink(
    long From, long To, EdgeKind Kind, int Weight, IReadOnlyList<string> Labels, EdgeState State, int Width = 1);

/// <param name="Hidden">너무 많아 뺀 상자 수. 숨기지 않고 알린다.</param>
public sealed record BlueprintResult(
    BlueprintLevel Level, long? Parent,
    IReadOnlyList<BlueprintBox> Boxes, IReadOnlyList<BlueprintLink> Links, int Hidden,
    string? ParentDisplay = null);

/// <summary>
/// 설계도. 지도가 «누가 누구와 얼마나 엮였나» 를 흩어 보인다면, 이것은 한 층을 상자와 선으로
/// 정리해 읽히게 한다 — 상자마다 맡은 일 한 줄과 대표 타입, 선마다 무엇으로 이어졌는지.
///
/// 선은 타입 층에 말아 둔 간선(<c>edge_roll</c>)에서 모은다. 모듈 층의 간선만으로는 선의 이름표를
/// 달 수 없다 — «Order 와 Cart 를 쓴다» 는 타입 층에만 있다.
/// </summary>
public static class Blueprint
{
    private const int ItemLimit = 4;
    private const int MemberLimit = 14;
    private const int InsideLimit = 40;
    private const int OutsideLimit = 8;
    private const int LabelLimit = 3;
    private const int RelatedLimit = 8;

    public static BlueprintResult? Of(
        SqliteConnection db, long? parentId, int? atOrd = null, int? fromOrd = null, bool ownOnly = false)
    {
        var at = atOrd ?? Latest(db);
        var span = new Span(at, fromOrd ?? at);

        if (parentId is not { } id) return Modules(db, span, ownOnly);

        var sheet = KindOf(db, id) switch
        {
            SymbolKind.Package => InsideModule(db, id, span, ownOnly),
            SymbolKind.Namespace => InsideNamespace(db, id, span, ownOnly),
            SymbolKind.Type => AroundType(db, id, span),
            _ => null,
        };
        return sheet is null ? null : sheet with { ParentDisplay = Displays(db, [id]).GetValueOrDefault(id) };
    }

    /// <summary>보는 시점과, 비교할 때 기준 시점.</summary>
    private readonly record struct Span(int At, int From)
    {
        public bool Comparing => From != At;

        public EdgeState StateOf(bool now, bool then) =>
            !Comparing ? EdgeState.Same
            : now && !then ? EdgeState.Born
            : !now && then ? EdgeState.Died
            : EdgeState.Same;

        public void Bind(SqliteCommand command)
        {
            command.Parameters.AddWithValue("$at", At);
            command.Parameters.AddWithValue("$from", From);
        }
    }

    private static string AliveAt(string alias, string ord) =>
        $"EXISTS (SELECT 1 FROM symbol_life l WHERE l.symbol_id = {alias}.id AND l.born_ord <= {ord} " +
        $"AND (l.died_ord IS NULL OR l.died_ord > {ord}))";

    private static string EdgeWindow(string alias, Span span) => span.Comparing
        ? $"{alias}.born_ord <= $at AND ({alias}.died_ord IS NULL OR {alias}.died_ord > $from)"
        : $"{alias}.born_ord <= $at AND ({alias}.died_ord IS NULL OR {alias}.died_ord > $at)";

    private static string EdgeNow(string alias) =>
        $"({alias}.born_ord <= $at AND ({alias}.died_ord IS NULL OR {alias}.died_ord > $at))";

    private static string EdgeThen(string alias) =>
        $"({alias}.born_ord <= $from AND ({alias}.died_ord IS NULL OR {alias}.died_ord > $from))";

    private static string Ids(IEnumerable<long> ids) => "[" + string.Join(',', ids) + "]";

    // ---- the parts of one level --------------------------------------------------------

    /// <summary>한 타입과, 설계도의 어느 상자에 드는가를 가리는 데 필요한 것.</summary>
    private sealed record TypeRow(
        long Id, string Display, string? Flavor, long? Module, long? Namespace, long? Container,
        bool Own, bool Test, bool Now, bool Then);

    /// <summary>맨 위: 모듈마다 상자 하나. 남의 모듈은 우리 모듈이 쓰는 것만, 바깥 상자로.</summary>
    private static BlueprintResult Modules(SqliteConnection db, Span span, bool ownOnly)
    {
        var types = Types(db, span, "1 = 1");
        var modules = Symbols(db, types.Where(type => type.Module is not null).Select(type => type.Module!.Value), span);
        var partOf = new Dictionary<long, long>();
        foreach (var type in types)
        {
            if (type.Module is not { } module || !modules.TryGetValue(module, out var info)) continue;
            if (ownOnly && !info.Own) continue;
            partOf[type.Id] = module;
        }

        return Assemble(db, BlueprintLevel.Modules, null, span, types, partOf,
            outsideOf: (_, _) => null,
            isOutside: part => !modules[part].Own,
            names: new Dictionary<long, string>());
    }

    /// <summary>
    /// 모듈 안: 나무가 쓰는 무리 나누기(<see cref="Tree.Groups"/>) 그대로. TypeScript 앱처럼
    /// 패키지 하나가 전부인 리포도 첫 장이 상자 하나로 끝나지 않는다.
    /// </summary>
    private static BlueprintResult InsideModule(SqliteConnection db, long module, Span span, bool ownOnly)
    {
        var groups = Tree.Groups(db, module);
        var groupOf = new Dictionary<long, long>();
        foreach (var group in groups)
        {
            foreach (var ns in group.Namespaces) groupOf[ns] = group.Id;
        }

        var types = Types(db, span, "s.module_id = $parent", module);
        var partOf = new Dictionary<long, long>();
        foreach (var type in types)
        {
            if (ownOnly && !type.Own) continue;
            if (type.Namespace is { } ns && groupOf.TryGetValue(ns, out var group)) partOf[type.Id] = group;
            // 네임스페이스 없이 모듈에 바로 든 타입은 제가 상자다.
            else if (type.Container == module) partOf[type.Id] = type.Id;
        }

        return Assemble(db, BlueprintLevel.Parts, module, span, types, partOf,
            outsideOf: (theirModule, _) => theirModule == module ? null : theirModule,
            isOutside: _ => false,
            names: groups.ToDictionary(group => group.Id, group => group.Display));
    }

    /// <summary>네임스페이스 안: 나무가 한 단 펼쳐 보이는 것 — 그 아래 네임스페이스와 타입.</summary>
    private static BlueprintResult InsideNamespace(SqliteConnection db, long parent, Span span, bool ownOnly)
    {
        var children = Tree.Children(db, parent, ownOnly).Nodes
            .Where(node => node.Kind is SymbolKind.Namespace or SymbolKind.Type)
            .ToList();
        var parts = children.Select(node => node.Id).ToHashSet();

        var types = Types(db, span, """
            s.namespace_id IN (
                WITH RECURSIVE under(id) AS (
                    SELECT $parent
                    UNION
                    SELECT c.id FROM symbol c JOIN under ON c.container_id = under.id WHERE c.kind = 2
                )
                SELECT id FROM under)
            """, parent);

        // 타입에서 담는 사슬을 올라가다 처음 만나는 상자가 그 타입의 상자다.
        var container = Containers(db, parent);
        foreach (var type in types) container[type.Id] = type.Container;

        var partOf = new Dictionary<long, long>();
        foreach (var type in types)
        {
            if (ownOnly && !type.Own) continue;
            long? at = type.Id;
            for (var guard = 0; at is { } here && guard < 64; guard++)
            {
                if (parts.Contains(here)) { partOf[type.Id] = here; break; }
                if (here == parent) break;
                at = container.GetValueOrDefault(here);
            }
        }

        var module = ModuleOf(db, parent);
        return Assemble(db, BlueprintLevel.Parts, parent, span, types, partOf,
            // 바깥은 다른 모듈이면 그 모듈, 같은 모듈이면 그 타입의 네임스페이스로 접는다.
            outsideOf: (theirModule, theirNamespace) => theirModule != module ? theirModule : theirNamespace,
            isOutside: _ => false,
            names: children.ToDictionary(node => node.Id, node => node.Display));
    }

    /// <summary>
    /// 상자와 선을 짓는다. <paramref name="partOf"/> 는 안에 든 타입이 어느 상자에 드는가,
    /// <paramref name="outsideOf"/> 는 바깥 타입을 어느 바깥 상자로 접는가(모듈, 네임스페이스).
    /// </summary>
    private static BlueprintResult Assemble(
        SqliteConnection db, BlueprintLevel level, long? parent, Span span,
        List<TypeRow> types, Dictionary<long, long> partOf,
        Func<long?, long?, long?> outsideOf, Func<long, bool> isOutside,
        Dictionary<long, string> names)
    {
        var insideParts = partOf.Values.ToHashSet();
        var outsideParts = new HashSet<long>();
        var links = new Dictionary<(long From, long To, EdgeKind Kind), LinkSum>();
        var face = new Dictionary<long, int>();
        var touching = new Dictionary<long, Dictionary<long, int>>();

        foreach (var edge in RolledEdges(db, span, level == BlueprintLevel.Modules ? null : partOf.Keys))
        {
            // 안에서는 우리 코드끼리의 이음새만 본다. 모든 상자가 표준 라이브러리로 선을 뻗으면
            // 설계도가 그 선에 묻힌다. 라이브러리는 맨 위 장에서 본다.
            if (level == BlueprintLevel.Parts
                && ((!partOf.ContainsKey(edge.From) && !edge.FromOwn) || (!partOf.ContainsKey(edge.To) && !edge.ToOwn)))
            {
                continue;
            }

            var from = PartOf(edge.From, edge.FromModule, edge.FromNamespace);
            var to = PartOf(edge.To, edge.ToModule, edge.ToNamespace);
            if (from is not { } a || to is not { } b || a == b) continue;
            var aOutside = !insideParts.Contains(a) || isOutside(a);
            var bOutside = !insideParts.Contains(b) || isOutside(b);
            if (aOutside && bOutside) continue;

            if (!insideParts.Contains(a)) outsideParts.Add(a);
            if (!insideParts.Contains(b)) outsideParts.Add(b);

            var key = (a, b, edge.Kind);
            if (!links.TryGetValue(key, out var sum)) links[key] = sum = new LinkSum();
            sum.Add(edge);

            // 쓰는 선은 쓰이는 타입으로, 구현 선은 구현하는 타입으로 이름을 단다.
            var named = edge.Kind == EdgeKind.Inherit ? edge.From : edge.To;
            sum.Name(named, edge.Weight);

            if (edge.Now)
            {
                face[edge.To] = face.GetValueOrDefault(edge.To) + edge.Weight;
                foreach (var (part, type) in new[] { (a, edge.From), (b, edge.To) })
                {
                    if (!(isOutside(part) || !insideParts.Contains(part))) continue;
                    if (!touching.TryGetValue(part, out var list)) touching[part] = list = [];
                    list[type] = list.GetValueOrDefault(type) + edge.Weight;
                }
            }
        }

        // 무거운 것만 남긴다. 상자 마흔 개가 넘는 설계도는 이미 설계도가 아니다.
        var weightOf = new Dictionary<long, int>();
        foreach (var ((a, b, _), sum) in links)
        {
            weightOf[a] = weightOf.GetValueOrDefault(a) + sum.Weight;
            weightOf[b] = weightOf.GetValueOrDefault(b) + sum.Weight;
        }

        var ownParts = insideParts.Where(part => !isOutside(part)).ToList();
        var theirParts = insideParts.Where(isOutside).Where(weightOf.ContainsKey).Concat(outsideParts).ToList();
        var keptInside = ownParts.OrderByDescending(part => weightOf.GetValueOrDefault(part)).ThenBy(part => part).Take(InsideLimit).ToHashSet();
        var keptOutside = theirParts.OrderByDescending(part => weightOf.GetValueOrDefault(part)).ThenBy(part => part).Take(OutsideLimit).ToHashSet();
        // 바깥 상자는 곁들이는 것이라 몇 개를 뺐는지 세지 않는다. 뺀 것을 알릴 것은 우리 상자다.
        var hidden = ownParts.Count - keptInside.Count;
        var kept = keptInside.Concat(keptOutside).ToHashSet();

        var meta = Symbols(db, kept, span);
        var boxes = new List<BlueprintBox>();
        var byPart = types.Where(type => partOf.ContainsKey(type.Id)).GroupBy(type => partOf[type.Id])
            .ToDictionary(group => group.Key, group => group.ToList());
        var typeById = types.ToDictionary(type => type.Id);

        foreach (var part in kept)
        {
            if (!meta.TryGetValue(part, out var info)) continue;
            var outside = !keptInside.Contains(part);
            var state = span.StateOf(info.Now, info.Then);
            var display = names.GetValueOrDefault(part, info.Display);

            if (outside)
            {
                // 바깥 상자에는 선이 닿는 타입만 적는다 — 그 모듈의 대표가 아니라 여기와 이어진 것이다.
                var involved = touching.GetValueOrDefault(part, [])
                    .OrderByDescending(pair => pair.Value).Take(ItemLimit - 1)
                    .Select(pair => Item(db, typeById, pair.Key, span))
                    .ToList();
                boxes.Add(new BlueprintBox(part, display, info.Kind, info.Flavor, null, null,
                    involved, 0, state, info.Own, info.Test, Outside: true));
                continue;
            }

            // 타입이 곧 상자면 제 이름을 한 번 더 적을 것이 아니라 멤버를 적는다.
            if (info.Kind == SymbolKind.Type)
            {
                var own = Members(db, part, span);
                boxes.Add(new BlueprintBox(part, display, info.Kind, info.Flavor, DocComment.Summary(info.Doc), null,
                    own.Take(ItemLimit).ToList(), Math.Max(0, own.Count - ItemLimit), state, info.Own, info.Test, Outside: false));
                continue;
            }

            // 대표 타입: 다른 상자에서 가장 많이 쓰는 것 — 이 상자가 바깥에 내놓은 얼굴이다.
            var members = byPart.GetValueOrDefault(part, [])
                .OrderByDescending(type => face.GetValueOrDefault(type.Id))
                .ThenBy(type => type.Display, StringComparer.Ordinal)
                .ToList();
            var items = members.DistinctBy(type => type.Display).Take(ItemLimit)
                .Select(type => new BlueprintItem(type.Id, type.Display, type.Flavor, null, null, span.StateOf(type.Now, type.Then)))
                .ToList();

            var role = DocComment.Summary(info.Doc);
            string? roleFrom = null;
            if (role is null)
            {
                foreach (var type in members.Take(10))
                {
                    if (DocComment.Summary(DocOf(db, type.Id)) is not { } borrowed) continue;
                    role = borrowed;
                    roleFrom = type.Display;
                    break;
                }
            }

            boxes.Add(new BlueprintBox(part, display, info.Kind, info.Flavor, role, roleFrom,
                items, Math.Max(0, members.Count - items.Count), state, info.Own, info.Test, Outside: false));
        }

        var result = links
            .Where(pair => kept.Contains(pair.Key.From) && kept.Contains(pair.Key.To))
            .Select(pair => new BlueprintLink(pair.Key.From, pair.Key.To, pair.Key.Kind, pair.Value.Weight,
                pair.Value.Labels(db, LabelLimit), span.StateOf(pair.Value.Now, pair.Value.Then), pair.Value.Width(db)))
            .OrderByDescending(link => link.Weight)
            .ToList();

        return new BlueprintResult(level, parent, boxes.OrderBy(box => box.Outside).ThenBy(box => box.Display, StringComparer.Ordinal).ToList(),
            result, hidden);

        long? PartOf(long type, long? module, long? ns) =>
            partOf.TryGetValue(type, out var part) ? part : outsideOf(module, ns);
    }

    private static BlueprintItem Item(SqliteConnection db, Dictionary<long, TypeRow> known, long id, Span span)
    {
        if (known.TryGetValue(id, out var type))
        {
            return new BlueprintItem(id, type.Display, type.Flavor, null, null, span.StateOf(type.Now, type.Then));
        }

        var info = Symbols(db, [id], span).GetValueOrDefault(id);
        return new BlueprintItem(id, info?.Display ?? "?", info?.Flavor, null, null,
            info is null ? EdgeState.Same : span.StateOf(info.Now, info.Then));
    }

    /// <summary>한 선에 말려 들어간 타입 간선들의 합.</summary>
    private sealed class LinkSum
    {
        private readonly Dictionary<long, int> _names = [];

        public int Weight { get; private set; }
        public bool Now { get; private set; }
        public bool Then { get; private set; }

        public void Add(RolledEdge edge)
        {
            if (edge.Now) Weight += edge.Weight;
            Now |= edge.Now;
            Then |= edge.Then;
        }

        public void Name(long type, int weight) => _names[type] = _names.GetValueOrDefault(type) + weight;

        public int Width(SqliteConnection db) => Math.Max(1, Displays(db, _names.Keys).Values.Distinct().Count());

        public IReadOnlyList<string> Labels(SqliteConnection db, int limit)
        {
            // 이름이 같은 타입이 여럿이면(파일마다 따로 둔 테스트 대역) 이름표에는 한 번만 적는다.
            var displays = Displays(db, _names.Keys);
            return _names
                .GroupBy(pair => displays.GetValueOrDefault(pair.Key, "?"))
                .OrderByDescending(group => group.Sum(pair => pair.Value)).ThenBy(group => group.Key, StringComparer.Ordinal)
                .Take(limit).Select(group => group.Key).ToList();
        }
    }

    private sealed record RolledEdge(
        long From, long To, EdgeKind Kind, int Weight, bool Now, bool Then,
        long? FromModule, long? FromNamespace, long? ToModule, long? ToNamespace, bool FromOwn, bool ToOwn);

    /// <summary>
    /// 타입 층의 간선. <paramref name="touching"/> 을 주면 그 타입에서 나가거나 들어오는 것만.
    /// 양쪽을 한 질의의 OR 로 찾으면 색인을 못 탄다 — 나가는 것과 들어오는 것을 따로 읽는다.
    /// </summary>
    private static List<RolledEdge> RolledEdges(SqliteConnection db, Span span, IEnumerable<long>? touching)
    {
        var edges = new List<RolledEdge>();
        var seen = new HashSet<(long, long, int, long)>();
        var sides = touching is null ? new[] { "1 = 1" }
            : ["r.from_id IN (SELECT value FROM json_each($ids))", "r.to_id IN (SELECT value FROM json_each($ids))"];
        var ids = touching is null ? null : Ids(touching);

        foreach (var side in sides)
        {
            using var command = db.CreateCommand();
            command.CommandText = $"""
                SELECT r.from_id, r.to_id, r.kind, r.weight, {EdgeNow("r")}, {EdgeThen("r")},
                       f.module_id, f.namespace_id, t.module_id, t.namespace_id, r.born_ord, f.own, t.own
                FROM edge_roll r
                JOIN symbol f ON f.id = r.from_id
                JOIN symbol t ON t.id = r.to_id
                WHERE r.level = {(int)Granularity.Type} AND r.kind IN (2, 3) AND r.from_id <> r.to_id
                  AND {EdgeWindow("r", span)} AND {side}
                """;
            span.Bind(command);
            if (ids is not null) command.Parameters.AddWithValue("$ids", ids);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var key = (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt32(2), reader.GetInt64(10));
                if (!seen.Add(key)) continue;
                edges.Add(new RolledEdge(
                    reader.GetInt64(0), reader.GetInt64(1), (EdgeKind)reader.GetInt32(2), reader.GetInt32(3),
                    reader.GetInt32(4) == 1, reader.GetInt32(5) == 1,
                    Nullable(reader, 6), Nullable(reader, 7), Nullable(reader, 8), Nullable(reader, 9),
                    reader.GetInt32(11) == 1, reader.GetInt32(12) == 1));
            }
        }

        return edges;
    }

    private static List<TypeRow> Types(SqliteConnection db, Span span, string scope, long? parent = null)
    {
        using var command = db.CreateCommand();
        command.CommandText = $"""
            SELECT s.id, s.display, s.flavor, s.module_id, s.namespace_id, s.container_id, s.own, s.test,
                   {AliveAt("s", "$at")}, {AliveAt("s", "$from")}
            FROM symbol s
            WHERE s.kind = 3 AND s.display <> '<invalid-global-code>' AND {scope}
            """;
        span.Bind(command);
        if (parent is { } id) command.Parameters.AddWithValue("$parent", id);

        var types = new List<TypeRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var now = reader.GetInt32(8) == 1;
            var then = reader.GetInt32(9) == 1;
            if (!now && !(span.Comparing && then)) continue;
            types.Add(new TypeRow(
                reader.GetInt64(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                Nullable(reader, 3), Nullable(reader, 4), Nullable(reader, 5),
                reader.GetInt32(6) == 1, reader.GetInt32(7) == 1, now, then));
        }

        return types;
    }

    /// <summary>한 네임스페이스 아래 네임스페이스들이 무엇에 담겼나.</summary>
    private static Dictionary<long, long?> Containers(SqliteConnection db, long parent)
    {
        using var command = db.CreateCommand();
        command.CommandText = """
            WITH RECURSIVE under(id, container_id) AS (
                SELECT id, container_id FROM symbol WHERE id = $parent
                UNION
                SELECT c.id, c.container_id FROM symbol c JOIN under ON c.container_id = under.id WHERE c.kind = 2
            )
            SELECT id, container_id FROM under
            """;
        command.Parameters.AddWithValue("$parent", parent);

        var containers = new Dictionary<long, long?>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) containers[reader.GetInt64(0)] = Nullable(reader, 1);
        return containers;
    }

    private sealed record SymbolRow(
        long Id, string Display, SymbolKind Kind, string? Flavor, string? Doc, bool Own, bool Test, bool Now, bool Then);

    private static Dictionary<long, SymbolRow> Symbols(SqliteConnection db, IEnumerable<long> ids, Span span)
    {
        using var command = db.CreateCommand();
        command.CommandText = $"""
            SELECT s.id, s.display, s.kind, s.flavor, s.doc, s.own, s.test,
                   {AliveAt("s", "$at")}, {AliveAt("s", "$from")}
            FROM symbol s
            WHERE s.id IN (SELECT value FROM json_each($ids))
            """;
        command.Parameters.AddWithValue("$ids", Ids(ids.Distinct()));
        span.Bind(command);

        var rows = new Dictionary<long, SymbolRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            rows[id] = new SymbolRow(
                id, reader.GetString(1), (SymbolKind)reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetInt32(5) == 1, reader.GetInt32(6) == 1, reader.GetInt32(7) == 1, reader.GetInt32(8) == 1);
        }

        return rows;
    }

    private static Dictionary<long, string> Displays(SqliteConnection db, IEnumerable<long> ids)
    {
        using var command = db.CreateCommand();
        command.CommandText = "SELECT id, display FROM symbol WHERE id IN (SELECT value FROM json_each($ids))";
        command.Parameters.AddWithValue("$ids", Ids(ids));

        var displays = new Dictionary<long, string>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) displays[reader.GetInt64(0)] = reader.GetString(1);
        return displays;
    }

    // ---- one type ------------------------------------------------------------------------

    /// <summary>
    /// 타입 한 장: 가운데에 그 타입과 멤버, 둘레에 그 타입이 쓰고 그 타입을 쓰는 타입들.
    /// 선의 이름표는 가운데 타입의 어느 멤버로 이어졌는가 — «gateway 로 쓴다», «Place 를 부른다».
    /// </summary>
    private static BlueprintResult AroundType(SqliteConnection db, long type, Span span)
    {
        var self = Symbols(db, [type], span)[type];
        var members = Members(db, type, span);

        // 둘레: 타입 층에 말아 둔 간선에서, 이 타입에 닿는 것.
        var sums = new Dictionary<(long From, long To, EdgeKind Kind), LinkSum>();
        foreach (var edge in RolledEdges(db, span, [type]))
        {
            if (edge.From != type && edge.To != type) continue;
            var key = (edge.From, edge.To, edge.Kind);
            if (!sums.TryGetValue(key, out var sum)) sums[key] = sum = new LinkSum();
            sum.Add(edge);
        }

        var owns = sums.Keys.SelectMany(key => new[] { key.From, key.To }).Distinct()
            .Where(id => id != type).ToHashSet();
        owns.IntersectWith(Symbols(db, owns, span).Values.Where(row => row.Own).Select(row => row.Id));
        var others = sums
            .GroupBy(pair => pair.Key.From == type ? pair.Key.To : pair.Key.From)
            .Select(group => (Other: group.Key,
                Inherits: group.Any(pair => pair.Key.Kind == EdgeKind.Inherit),
                Weight: group.Sum(pair => pair.Value.Weight)))
            // 상속·구현은 무게가 1 이라도 뼈대다. 먼저 넣는다. 그다음 우리 코드 — str·int 가
            // 자리를 다 차지하면 이 타입이 무엇과 짜여 있는지가 안 보인다.
            .OrderByDescending(other => other.Inherits).ThenByDescending(other => owns.Contains(other.Other))
            .ThenByDescending(other => other.Weight).ThenBy(other => other.Other)
            .ToList();
        var kept = others.Take(RelatedLimit).Select(other => other.Other).ToHashSet();

        var (labels, touched) = MemberLinks(db, type, kept, span);

        var boxes = new List<BlueprintBox>
        {
            new(type, self.Display, self.Kind, self.Flavor, DocComment.Summary(self.Doc), null,
                members.Take(MemberLimit).ToList(), Math.Max(0, members.Count - MemberLimit),
                span.StateOf(self.Now, self.Then), self.Own, self.Test, Outside: false),
        };

        var meta = Symbols(db, kept, span);
        foreach (var other in kept)
        {
            if (!meta.TryGetValue(other, out var info)) continue;
            var items = touched.GetValueOrDefault(other, [])
                .OrderByDescending(pair => pair.Value.Weight).Take(ItemLimit - 1)
                .Select(pair => pair.Value.Item)
                .ToList();
            boxes.Add(new BlueprintBox(other, info.Display, info.Kind, info.Flavor, DocComment.Summary(info.Doc), null,
                items, 0, span.StateOf(info.Now, info.Then), info.Own, info.Test, Outside: !info.Own));
        }

        var links = sums
            .Where(pair => kept.Contains(pair.Key.From == type ? pair.Key.To : pair.Key.From))
            .Select(pair =>
            {
                var other = pair.Key.From == type ? pair.Key.To : pair.Key.From;
                var all = labels.GetValueOrDefault((other, pair.Key.Kind), []);
                var names = all
                    .OrderByDescending(label => label.Value).ThenBy(label => label.Key, StringComparer.Ordinal)
                    .Take(LabelLimit).Select(label => label.Key).ToList();
                return new BlueprintLink(pair.Key.From, pair.Key.To, pair.Key.Kind, pair.Value.Weight, names,
                    span.StateOf(pair.Value.Now, pair.Value.Then), Math.Max(1, all.Count));
            })
            .OrderByDescending(link => link.Weight)
            .ToList();

        return new BlueprintResult(BlueprintLevel.Type, type, boxes, links, Math.Max(0, others.Count - kept.Count));
    }

    /// <summary>필드·프로퍼티 먼저, 그다음 메서드. 설계도의 클래스 상자가 그 차례다.</summary>
    private static List<BlueprintItem> Members(SqliteConnection db, long type, Span span)
    {
        using var command = db.CreateCommand();
        command.CommandText = $"""
            SELECT s.id, s.display, s.kind, s.signature, {AliveAt("s", "$at")}, {AliveAt("s", "$from")}
            FROM symbol s
            WHERE s.container_id = $type AND s.kind IN (4, 5) AND s.display <> '<invalid-global-code>'
            """;
        command.Parameters.AddWithValue("$type", type);
        span.Bind(command);

        var members = new List<BlueprintItem>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var now = reader.GetInt32(4) == 1;
            var then = reader.GetInt32(5) == 1;
            if (!now && !(span.Comparing && then)) continue;
            var kind = (SymbolKind)reader.GetInt32(2);
            var signature = reader.IsDBNull(3) ? null : reader.GetString(3);
            var id = reader.GetInt64(0);
            var display = reader.GetString(1);
            members.Add(new BlueprintItem(id, display, null, new MemberInfo(id, display, kind, signature).Shape,
                signature, span.StateOf(now, then)));
        }

        return members
            .OrderBy(member => member.Shape == "method" ? 1 : 0)
            .ThenBy(member => member.Display, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// 멤버 층에서 본 이음새. 이름표는 가운데 타입 쪽 멤버, 둘레 상자에 적을 것은 저쪽 멤버다.
    ///
    /// 메서드 본문의 참조는 메서드가 아니라 그 매개변수에 붙는다(<see cref="SymbolDetail"/> 가 겪은 일).
    /// 그래서 담긴 것이 타입 바로 밑이 아니면 한 단 올려 멤버로 본다.
    /// </summary>
    private static (Dictionary<(long Other, EdgeKind Kind), Dictionary<string, int>> Labels,
        Dictionary<long, Dictionary<long, (BlueprintItem Item, int Weight)>> Touched)
        MemberLinks(SqliteConnection db, long type, HashSet<long> others, Span span)
    {
        var labels = new Dictionary<(long, EdgeKind), Dictionary<string, int>>();
        var touched = new Dictionary<long, Dictionary<long, (BlueprintItem, int)>>();
        if (others.Count == 0) return (labels, touched);

        var rows = new List<(long Mine, long Theirs, long Other, EdgeKind Kind, int Weight)>();
        foreach (var outgoing in new[] { true, false })
        {
            var (near, far) = outgoing ? ("from_id", "to_id") : ("to_id", "from_id");
            using var command = db.CreateCommand();
            command.CommandText = $"""
                SELECT CASE WHEN m.container_id = $type THEN m.id
                            WHEN m.id = $type THEN NULL ELSE m.container_id END,
                       CASE WHEN o.container_id = o.type_id THEN o.id
                            WHEN o.id = o.type_id THEN NULL ELSE o.container_id END,
                       o.type_id, e.kind, e.weight
                FROM edge_life e
                JOIN symbol m ON m.id = e.{near}
                JOIN symbol o ON o.id = e.{far}
                WHERE e.{near} IN (SELECT id FROM symbol WHERE type_id = $type)
                  AND +o.type_id IN (SELECT value FROM json_each($others))
                  AND e.kind IN (2, 3) AND {EdgeWindow("e", span)}
                """;
            command.Parameters.AddWithValue("$type", type);
            command.Parameters.AddWithValue("$others", Ids(others));
            span.Bind(command);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add((Nullable(reader, 0) ?? 0, Nullable(reader, 1) ?? 0, reader.GetInt64(2),
                    (EdgeKind)reader.GetInt32(3), reader.GetInt32(4)));
            }
        }

        var names = Named(db, rows.SelectMany(row => new[] { row.Mine, row.Theirs }).Where(id => id != 0), span);
        foreach (var (mine, theirs, other, kind, weight) in rows)
        {
            if (mine != 0 && names.TryGetValue(mine, out var label))
            {
                if (!labels.TryGetValue((other, kind), out var counts)) labels[(other, kind)] = counts = [];
                counts[label.Display + (label.Shape == "method" ? "()" : "")] =
                    counts.GetValueOrDefault(label.Display + (label.Shape == "method" ? "()" : "")) + weight;
            }

            if (theirs != 0 && names.TryGetValue(theirs, out var item))
            {
                if (!touched.TryGetValue(other, out var list)) touched[other] = list = [];
                list[theirs] = (item, list.TryGetValue(theirs, out var seen) ? seen.Item2 + weight : weight);
            }
        }

        return (labels, touched);
    }

    private static Dictionary<long, BlueprintItem> Named(SqliteConnection db, IEnumerable<long> ids, Span span)
    {
        using var command = db.CreateCommand();
        command.CommandText = $"""
            SELECT s.id, s.display, s.kind, s.signature, {AliveAt("s", "$at")}, {AliveAt("s", "$from")}
            FROM symbol s
            WHERE s.id IN (SELECT value FROM json_each($ids)) AND s.kind IN (4, 5)
            """;
        command.Parameters.AddWithValue("$ids", Ids(ids.Distinct()));
        span.Bind(command);

        var items = new Dictionary<long, BlueprintItem>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            var display = reader.GetString(1);
            var kind = (SymbolKind)reader.GetInt32(2);
            var signature = reader.IsDBNull(3) ? null : reader.GetString(3);
            items[id] = new BlueprintItem(id, display, null, new MemberInfo(id, display, kind, signature).Shape, signature,
                span.StateOf(reader.GetInt32(4) == 1, reader.GetInt32(5) == 1));
        }

        return items;
    }

    // ---- small reads -----------------------------------------------------------------------

    private static long? Nullable(SqliteDataReader reader, int column) =>
        reader.IsDBNull(column) ? null : reader.GetInt64(column);

    private static int Latest(SqliteConnection db)
    {
        using var command = db.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(ord), 0) FROM snapshot";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static SymbolKind? KindOf(SqliteConnection db, long id)
    {
        using var command = db.CreateCommand();
        command.CommandText = "SELECT kind FROM symbol WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() is long kind ? (SymbolKind)kind : null;
    }

    private static long? ModuleOf(SqliteConnection db, long id)
    {
        using var command = db.CreateCommand();
        command.CommandText = "SELECT module_id FROM symbol WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() is long module ? module : null;
    }

    private static string? DocOf(SqliteConnection db, long id)
    {
        using var command = db.CreateCommand();
        command.CommandText = "SELECT doc FROM symbol WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() as string;
    }
}
