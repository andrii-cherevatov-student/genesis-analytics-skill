using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using WikipediaInterestSkill.Application;
using WikipediaInterestSkill.Wikipedia.Models;

namespace WikipediaInterestSkill.Caching;

public sealed class PageviewCache
{
    public const int UnsettledDays = 3;
    public static readonly TimeSpan ResolutionTtl = TimeSpan.FromDays(30);

    private readonly string _connectionString;
    private readonly ILogger<PageviewCache> _logger;
    private readonly Func<DateOnly> _today;
    private int _hits;
    private int _misses;

    public PageviewCache(string directory, ILogger<PageviewCache> logger, Func<DateOnly>? today = null)
    {
        Directory.CreateDirectory(directory);
        DatabasePath = Path.Combine(directory, "wiki-interest-cache.db");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = 30,
        }.ToString();
        _logger = logger;
        _today = today ?? (() => DateOnly.FromDateTime(DateTime.UtcNow));
        Initialize();
    }

    public string DatabasePath { get; }
    public int Hits => _hits;
    public int Misses => _misses;

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        using var pragma = c.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout=30000;";
        pragma.ExecuteNonQuery();
        return c;
    }

    private void Initialize()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS pageviews (
                project TEXT NOT NULL, article TEXT NOT NULL, date TEXT NOT NULL, views INTEGER NOT NULL,
                PRIMARY KEY (project, article, date)) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS coverage (
                project TEXT NOT NULL, article TEXT NOT NULL, from_date TEXT NOT NULL, to_date TEXT NOT NULL,
                fetched_at TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_coverage ON coverage(project, article);
            CREATE TABLE IF NOT EXISTS resolutions (
                key TEXT PRIMARY KEY, json TEXT NOT NULL, resolved_at TEXT NOT NULL);
            """;
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<(DateOnly From, DateOnly To)> MissingRanges(string project, string article, DateOnly from, DateOnly to)
    {
        var covered = new List<(DateOnly From, DateOnly To)>();
        using (var c = Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT from_date, to_date FROM coverage WHERE project=$p AND article=$a";
            cmd.Parameters.AddWithValue("$p", project);
            cmd.Parameters.AddWithValue("$a", article);
            using var r = cmd.ExecuteReader();
            while (r.Read()) covered.Add((ParseDate(r.GetString(0)), ParseDate(r.GetString(1))));
        }

        var missing = new List<(DateOnly, DateOnly)>();
        var cursor = from;
        foreach (var (cf, ct) in covered.OrderBy(x => x.From))
        {
            if (ct < cursor) continue;
            if (cf > to) break;
            if (cf > cursor) missing.Add((cursor, Min(cf.AddDays(-1), to)));
            cursor = Max(cursor, ct.AddDays(1));
            if (cursor > to) break;
        }

        if (cursor <= to) missing.Add((cursor, to));
        if (missing.Count == 0) Interlocked.Increment(ref _hits);
        else Interlocked.Increment(ref _misses);
        _logger.LogInformation("Cache {Result} for {Project}/{Article} {From}..{To}: {Missing} range(s) to fetch",
            missing.Count == 0 ? "hit" : "miss", project, article, from, to, missing.Count);
        return missing;
    }

    public void Store(string project, string article, DateOnly from, DateOnly to, IReadOnlyList<DailyPageview> daily)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT OR REPLACE INTO pageviews(project, article, date, views) VALUES ($p, $a, $d, $v)";
            var pd = cmd.Parameters.Add("$d", SqliteType.Text);
            var pv = cmd.Parameters.Add("$v", SqliteType.Integer);
            cmd.Parameters.AddWithValue("$p", project);
            cmd.Parameters.AddWithValue("$a", article);
            foreach (var d in daily)
            {
                pd.Value = FormatDate(d.Date);
                pv.Value = d.Views;
                cmd.ExecuteNonQuery();
            }
        }

        var settled = _today().AddDays(-UnsettledDays);
        var coveredTo = Min(to, settled);
        if (coveredTo >= from)
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                "INSERT INTO coverage(project, article, from_date, to_date, fetched_at) VALUES ($p, $a, $f, $t, $n)";
            cmd.Parameters.AddWithValue("$p", project);
            cmd.Parameters.AddWithValue("$a", article);
            cmd.Parameters.AddWithValue("$f", FormatDate(from));
            cmd.Parameters.AddWithValue("$t", FormatDate(coveredTo));
            cmd.Parameters.AddWithValue("$n", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public IReadOnlyList<DailyPageview> Get(string project, string article, DateOnly from, DateOnly to)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText =
            "SELECT date, views FROM pageviews WHERE project=$p AND article=$a AND date BETWEEN $f AND $t ORDER BY date";
        cmd.Parameters.AddWithValue("$p", project);
        cmd.Parameters.AddWithValue("$a", article);
        cmd.Parameters.AddWithValue("$f", FormatDate(from));
        cmd.Parameters.AddWithValue("$t", FormatDate(to));
        using var r = cmd.ExecuteReader();
        var list = new List<DailyPageview>();
        while (r.Read()) list.Add(new DailyPageview(ParseDate(r.GetString(0)), r.GetInt64(1)));
        return list;
    }

    public ResolvedArticle? GetResolution(string key)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT json, resolved_at FROM resolutions WHERE key=$k";
        cmd.Parameters.AddWithValue("$k", key);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        var at = DateTime.Parse(r.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        if (DateTime.UtcNow - at > ResolutionTtl) return null;
        try
        {
            var article = JsonSerializer.Deserialize<ResolvedArticle>(r.GetString(0), JsonOptions.Default);
            if (article is not null)
                _logger.LogInformation("Resolution cache hit for {Key}: {Title}", key, article.ArticleTitle);
            return article;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void StoreResolution(string key, ResolvedArticle article)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO resolutions(key, json, resolved_at) VALUES ($k, $j, $t)";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$j", JsonSerializer.Serialize(article, JsonOptions.Default));
        cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    }

    private static string FormatDate(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static DateOnly ParseDate(string s) => DateOnly.ParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static DateOnly Min(DateOnly a, DateOnly b) => a < b ? a : b;
    private static DateOnly Max(DateOnly a, DateOnly b) => a > b ? a : b;
}
