using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Bot.Core.Config;
using Bot.Core.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bot.Core.Memory;

/// <summary>待入库的事件。字段长度由调用方先按配置截断。</summary>
public sealed record EventDraft(
    string TimeText,
    long? TimeNorm,
    string Location,
    string Detail,
    IReadOnlyList<string> Keywords,
    IReadOnlyList<int> PersonIds,
    int Importance = 3,
    bool Pinned = false,
    string LocationLabel = "");

public sealed record RetrievedEvent(EventRow Event, double Score, double KeywordNorm, double PersonNorm, double LocationScore);

public sealed class EventStore
{
    private const string SelectColumns = """
        e.event_id, e.time_text, e.time_norm, e.location, e.location_label, e.detail, e.importance, e.pinned, e.status,
        e.last_used_at, e.injected_count, e.content_hash, e.created_at, e.archived_at,
        COALESCE((SELECT GROUP_CONCAT(word, char(31)) FROM event_keyword k WHERE k.event_id=e.event_id), '') AS words,
        COALESCE((SELECT GROUP_CONCAT(person_id, ',') FROM event_person p WHERE p.event_id=e.event_id), '') AS persons
        """;

    private readonly Db _db;
    private readonly IClock _clock;
    private readonly MemoryOpLog _opLog;
    private readonly ILogger<EventStore> _log;

    public EventStore(Db db, IClock clock, MemoryOpLog opLog, ILogger<EventStore>? log = null)
    {
        _db = db;
        _clock = clock;
        _opLog = opLog;
        _log = log ?? NullLogger<EventStore>.Instance;
    }

    /// <summary>
    /// 某个群可检索的事件：本群 + 跨群全局（location 为空）。
    /// <para>
    /// 按 event_id <b>倒序</b> 取，命中上限时丢的是最旧的记忆而不是最新的——
    /// 正序取会让"第 5001 条之后的事件永远不参与检索"，而新事件恰恰最该被检索到。
    /// </para>
    /// </summary>
    public IReadOnlyList<EventRow> LoadActive(string chatId, int limit = 5000)
    {
        var sql = $"""
            SELECT {SelectColumns} FROM event e
            WHERE e.status='active' AND (e.location='' OR e.location=@c)
            ORDER BY e.event_id DESC LIMIT @n
            """;
        var rows = _db.Query(sql, MapRow, ("@c", chatId), ("@n", limit));
        if (rows.Count >= limit)
        {
            _log.LogWarning("群 {Chat} 活跃事件达到检索上限 {Limit}，更早的事件本次不参与打分", chatId, limit);
        }

        return rows;
    }

    public IReadOnlyList<EventRow> LoadByIds(IReadOnlyList<long> ids)
    {
        if (ids.Count == 0)
        {
            return Array.Empty<EventRow>();
        }

        return Load($"e.event_id IN ({string.Join(',', ids.Select((_, i) => "@p" + i))})", ids.Count,
            ids.Select((id, i) => ("@p" + i, (object?)id)).ToArray());
    }

    public IReadOnlyList<EventRow> LoadAll(string? status = null, int limit = 500) =>
        status is null
            ? Load("1=1", limit)
            : Load("e.status=@s", limit, ("@s", status));

    public EventRow? GetById(long eventId)
    {
        var rows = Load("e.event_id=@id", 1, ("@id", eventId));
        return rows.Count == 0 ? null : rows[0];
    }

    /// <summary>按 content_hash 去重写入。已存在则返回既有 id，不重复插入。</summary>
    public long Add(EventDraft draft, string batchId, string source)
    {
        var hash = ContentHash(draft.Detail);
        var existing = _db.ScalarLong("SELECT event_id FROM event WHERE content_hash=@h", ("@h", hash));
        if (existing > 0)
        {
            return existing;
        }

        var now = TimeUtil.Unix(_clock.UtcNow);
        var eventId = _db.InTransaction((connection, transaction) =>
        {
            long id;
            using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText =
                    """
                    INSERT INTO event(time_text, time_norm, location, location_label, detail, importance, pinned, status,
                                      last_used_at, injected_count, content_hash, created_at)
                    VALUES(@tt, @tn, @loc, @locLabel, @detail, @imp, @pin, 'active', NULL, 0, @hash, @now)
                    RETURNING event_id
                    """;
                insert.Parameters.AddWithValue("@tt", draft.TimeText);
                insert.Parameters.AddWithValue("@tn", (object?)draft.TimeNorm ?? DBNull.Value);
                insert.Parameters.AddWithValue("@loc", draft.Location);
                insert.Parameters.AddWithValue("@locLabel", draft.LocationLabel);
                insert.Parameters.AddWithValue("@detail", draft.Detail);
                insert.Parameters.AddWithValue("@imp", draft.Importance);
                insert.Parameters.AddWithValue("@pin", draft.Pinned ? 1 : 0);
                insert.Parameters.AddWithValue("@hash", hash);
                insert.Parameters.AddWithValue("@now", now);
                id = Convert.ToInt64(insert.ExecuteScalar());
            }

            foreach (var word in draft.Keywords.Select(w => w.Trim()).Where(w => w.Length > 0).Distinct())
            {
                using var keyword = connection.CreateCommand();
                keyword.Transaction = transaction;
                keyword.CommandText = "INSERT OR IGNORE INTO event_keyword(event_id, word) VALUES(@id, @w)";
                keyword.Parameters.AddWithValue("@id", id);
                keyword.Parameters.AddWithValue("@w", word);
                keyword.ExecuteNonQuery();
            }

            foreach (var personId in draft.PersonIds.Where(pid => pid > 0).Distinct())
            {
                using var person = connection.CreateCommand();
                person.Transaction = transaction;
                person.CommandText = "INSERT OR IGNORE INTO event_person(event_id, person_id) VALUES(@id, @p)";
                person.Parameters.AddWithValue("@id", id);
                person.Parameters.AddWithValue("@p", personId);
                person.ExecuteNonQuery();
            }

            return id;
        });

        _opLog.Append(batchId, "insert", "event", eventId, null,
            new { EventId = eventId, draft.Detail, draft.Location }, source);
        return eventId;
    }

    /// <summary>前缀重建时记录注入信号：last_used_at 只在轮换后重建前缀时更新（文档 3.4.4）。</summary>
    public void TouchInjected(IReadOnlyList<long> eventIds)
    {
        if (eventIds.Count == 0)
        {
            return;
        }

        var now = TimeUtil.Unix(_clock.UtcNow);
        foreach (var id in eventIds)
        {
            _db.Exec("UPDATE event SET last_used_at=@t, injected_count=injected_count+1 WHERE event_id=@id",
                ("@t", now), ("@id", id));
        }
    }

    /// <summary>归档而非删除：超 ttl 未被注入、非置顶、importance ≤ 3 → archived。</summary>
    public int ArchiveStale(int ttlDays)
    {
        var cutoff = TimeUtil.Unix(_clock.UtcNow) - (ttlDays * 86400L);
        return _db.Exec(
            """
            UPDATE event SET status='archived', archived_at=@now
            WHERE status='active' AND pinned=0 AND importance<=3
              AND COALESCE(last_used_at, created_at) < @cutoff
            """,
            ("@now", TimeUtil.NowUnix()),
            ("@cutoff", cutoff));
    }

    /// <summary>归档满 archive_purge_days 才物理删除——这是全库唯一允许删除的位置。</summary>
    public int PurgeArchived(int purgeDays)
    {
        var cutoff = TimeUtil.Unix(_clock.UtcNow) - (purgeDays * 86400L);
        return _db.Exec(
            "DELETE FROM event WHERE status='archived' AND archived_at IS NOT NULL AND archived_at < @cutoff",
            ("@cutoff", cutoff));
    }

    /// <summary>被显式检索命中一次即复活。</summary>
    public int Revive(IReadOnlyList<long> eventIds)
    {
        var count = 0;
        foreach (var id in eventIds)
        {
            count += _db.Exec("UPDATE event SET status='active', archived_at=NULL WHERE event_id=@id AND status='archived'",
                ("@id", id));
        }

        return count;
    }

    /// <summary>看板/记忆浏览用的全文检索。trigram 分词要求查询词 ≥3 字符，短词退化为 LIKE。</summary>
    public IReadOnlyList<EventRow> Search(string query, int limit = 50)
    {
        var trimmed = query?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return Array.Empty<EventRow>();
        }

        if (trimmed.Length < 3)
        {
            return Load("e.detail LIKE @q", limit, ("@q", "%" + trimmed + "%"));
        }

        try
        {
            var ids = _db.Query(
                "SELECT rowid FROM event_fts WHERE event_fts MATCH @q ORDER BY rank LIMIT @n",
                reader => reader.GetInt64(0),
                ("@q", "\"" + trimmed.Replace("\"", "\"\"") + "\""),
                ("@n", limit));

            return ids.Count == 0 ? Array.Empty<EventRow>() : LoadByIds(ids);
        }
        catch (SqliteException ex)
        {
            _log.LogWarning(ex, "FTS 检索失败，退化为 LIKE：{Query}", trimmed);
            return Load("e.detail LIKE @q", limit, ("@q", "%" + trimmed + "%"));
        }
    }

    public static string ContentHash(string detail)
    {
        var normalized = Regex.Replace(detail.Trim(), @"\s+", " ");
        var bytes = Encoding.UTF8.GetBytes(normalized);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private IReadOnlyList<EventRow> Load(string where, int limit, params (string Name, object? Value)[] args)
    {
        var sql = $"SELECT {SelectColumns} FROM event e WHERE {where} ORDER BY e.event_id ASC LIMIT @n";
        var all = args.Append(("@n", (object?)limit)).ToArray();
        return _db.Query(sql, MapRow, all);
    }

    private static EventRow MapRow(SqliteDataReader reader)
    {
        var wordsRaw = reader.GetString(14);
        var personsRaw = reader.GetString(15);
        var personIds = personsRaw.Length == 0
            ? Array.Empty<int>()
            : personsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
        var words = wordsRaw.Length == 0
            ? Array.Empty<string>()
            : wordsRaw.Split('\u001F', StringSplitOptions.RemoveEmptyEntries);

        return new EventRow(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetInt64(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetInt32(6),
            reader.GetInt64(7) == 1,
            reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetInt64(9),
            reader.GetInt32(10),
            reader.GetString(11),
            reader.GetInt64(12),
            reader.IsDBNull(13) ? null : reader.GetInt64(13),
            personIds,
            words);
    }
}

/// <summary>
/// 检索评分（文档 3.4.3）：全部归一化到 [0,1]，避免长 detail / 多关键词条目天然霸榜。
/// <code>
/// kw_norm     = Σ_{w∈q∩e} idf(w) / Σ_{w∈q} idf(w)
/// person_norm = |P_q ∩ P_e| / max(1,|P_q|)
/// score       = W_kw×kw_norm + W_person×person_norm + W_loc×loc_score
/// </code>
/// </summary>
public sealed class RetrievalService
{
    private readonly EventStore _store;
    private readonly ITokenizer _tokenizer;
    private readonly ILogger<RetrievalService>? _log;

    public RetrievalService(EventStore store, ITokenizer tokenizer, ILogger<RetrievalService>? log = null)
    {
        _store = store;
        _tokenizer = tokenizer;
        _log = log;
    }

    public IReadOnlyList<RetrievedEvent> Retrieve(string chatId, string queryText, IReadOnlyList<int> personIds,
        EffectiveConfig cfg)
    {
        var events = _store.LoadActive(chatId);
        if (events.Count == 0)
        {
            return Array.Empty<RetrievedEvent>();
        }

        var personSet = personIds.Where(id => id > 0).ToHashSet();
        var scored = new List<RetrievedEvent>();

        // 事件侧词元 = 模型给的关键词 ∪ 详情分词。只用关键词会因为模型给词太少而漏检；
        // 只用详情分词则会把"火锅"这类核心词淹没在长句里。
        var eventTokens = events.ToDictionary(
            e => e.EventId,
            e => new HashSet<string>(e.Keywords.Concat(_tokenizer.Tokenize(e.Detail)), StringComparer.Ordinal));

        var documentFrequency = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var tokens in eventTokens.Values)
        {
            foreach (var word in tokens)
            {
                documentFrequency[word] = documentFrequency.GetValueOrDefault(word) + 1;
            }
        }

        double Idf(string word) =>
            Math.Log((events.Count + 1.0) / (documentFrequency.GetValueOrDefault(word) + 1.0)) + 1.0;

        // 只保留在语料里出现过的查询词元：未登录的 bigram（"末吃""锅吗"）如果参与分母，
        // 会拿到最高 idf，把真正的命中稀释掉。
        var queryTokens = _tokenizer
            .Tokenize(queryText)
            .Distinct()
            .Where(documentFrequency.ContainsKey)
            .ToArray();

        var queryWeight = queryTokens.Sum(Idf);

        foreach (var item in events)
        {
            var tokens = eventTokens[item.EventId];
            var hitWeight = queryTokens.Where(tokens.Contains).Sum(Idf);
            var kwNorm = queryWeight > 0 ? Math.Clamp(hitWeight / queryWeight, 0, 1) : 0;

            var personNorm = personSet.Count == 0
                ? 0
                : item.PersonIds.Count(personSet.Contains) / (double)personSet.Count;

            var locationScore = string.Equals(item.Location, chatId, StringComparison.Ordinal) ? 1 : 0;

            // 地点只是加权项，不能单独构成"相关"——否则同群的历史事件会全量注入。
            if (!item.Pinned && kwNorm <= 0 && personNorm <= 0)
            {
                continue;
            }

            var score = (cfg.WeightKeyword * kwNorm) + (cfg.WeightPerson * personNorm) + (cfg.WeightLocation * locationScore);
            scored.Add(new RetrievedEvent(item, score, kwNorm, personNorm, locationScore));
        }

        var selected = new List<RetrievedEvent>();
        foreach (var pinned in scored.Where(s => s.Event.Pinned)
                     .OrderByDescending(s => s.Score)
                     .ThenBy(s => s.Event.EventId)
                     .Take(cfg.PinnedMax))
        {
            selected.Add(pinned);
        }

        var selectedIds = selected.Select(s => s.Event.EventId).ToHashSet();
        var ranked = scored
            .Where(s => !selectedIds.Contains(s.Event.EventId))
            .Where(s => s.Score >= cfg.EventScoreThreshold)
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.Event.EventId)
            .ToList();

        foreach (var candidate in ApplyDiversity(ranked, cfg.DiversityLambda))
        {
            if (selected.Count >= cfg.EventTopK + cfg.PinnedMax)
            {
                break;
            }

            selected.Add(candidate);
        }

        // 渲染时按 event_id 升序，与检索分数无关（文档 1.3 铁律 4）。
        return selected.OrderBy(s => s.Event.EventId).ToArray();
    }

    /// <summary>可选多样性惩罚：对与已选条目高度相似的候选降权（diversity_lambda=0 时恒等）。</summary>
    private static IEnumerable<RetrievedEvent> ApplyDiversity(List<RetrievedEvent> ranked, double lambda)
    {
        if (lambda <= 0)
        {
            return ranked;
        }

        var chosen = new List<RetrievedEvent>();
        var remaining = new List<RetrievedEvent>(ranked);
        while (remaining.Count > 0)
        {
            var best = remaining
                .OrderByDescending(candidate => candidate.Score - (lambda * MaxSimilarity(candidate, chosen)))
                .ThenBy(candidate => candidate.Event.EventId)
                .First();
            chosen.Add(best);
            remaining.Remove(best);
        }

        return chosen;
    }

    private static double MaxSimilarity(RetrievedEvent candidate, List<RetrievedEvent> chosen)
    {
        if (chosen.Count == 0)
        {
            return 0;
        }

        var a = new HashSet<string>(candidate.Event.Keywords, StringComparer.Ordinal);
        if (a.Count == 0)
        {
            return 0;
        }

        var best = 0.0;
        foreach (var other in chosen)
        {
            var b = new HashSet<string>(other.Event.Keywords, StringComparer.Ordinal);
            if (b.Count == 0)
            {
                continue;
            }

            var intersection = a.Count(b.Contains);
            best = Math.Max(best, intersection / (double)(a.Count + b.Count - intersection));
        }

        return best;
    }
}
