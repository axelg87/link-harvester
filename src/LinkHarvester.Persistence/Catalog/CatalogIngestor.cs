using System.Data;
using System.Globalization;
using System.Text.Json;
using LinkHarvester.Core;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LinkHarvester.Persistence.Catalog;

/// <summary>
/// Streams a Hydracker JSON dump (either a JSON array or NDJSON lines) into the
/// catalog tables. Memory footprint is independent of input size — we never
/// materialise more than one record at a time outside an INSERT batch.
///
/// Bulk loading strategy:
///   - One single SQLite connection in WAL mode with PRAGMAs tuned for bulk load
///     (synchronous=NORMAL, temp_store=MEMORY, journal_mode=WAL).
///   - Two prepared statements (titles UPSERT, links INSERT) bound and reused.
///   - Wrapped in 50k-record transactions to amortise commit cost.
///   - FTS triggers are dropped during the load and the FTS table is rebuilt at
///     the end (10-20x faster than per-row trigger fires).
/// </summary>
public sealed class CatalogIngestor
{
    private readonly IDbContextFactory<HarvesterDbContext> _factory;
    private readonly ITitleNormalizer _normalizer;
    private readonly ILogger<CatalogIngestor> _log;

    public CatalogIngestor(IDbContextFactory<HarvesterDbContext> factory,
                            ITitleNormalizer normalizer,
                            ILogger<CatalogIngestor> log)
    {
        _factory = factory;
        _normalizer = normalizer;
        _log = log;
    }

    public async Task<CatalogImportRunEntity> IngestAsync(
        Stream input,
        string sourceDescription,
        IProgress<IngestProgress>? progress,
        CancellationToken ct)
    {
        var run = new CatalogImportRunEntity
        {
            StartedAt = DateTimeOffset.UtcNow,
            Source = "upload",
            SourceDescription = sourceDescription,
            Status = "running"
        };
        await using (var db = await _factory.CreateDbContextAsync(ct))
        {
            db.CatalogImportRuns.Add(run);
            await db.SaveChangesAsync(ct);
        }

        long total = 0, links = 0, titles = 0, episodes = 0, failed = 0;
        var titleIdsByKey = new Dictionary<string, int>(StringComparer.Ordinal);
        var episodeIdsByTitleAndKey = new Dictionary<(int, int, int, bool), int>();

        SqliteConnection? conn = null;
        SqliteTransaction? tx = null;
        SqliteCommand? insTitle = null, insEpisode = null, insLink = null;

        try
        {
            var connStr = GetConnectionString();
            conn = new SqliteConnection(connStr);
            await conn.OpenAsync(ct);

            using (var pragma = conn.CreateCommand())
            {
                pragma.CommandText = @"
                    PRAGMA journal_mode=WAL;
                    PRAGMA synchronous=NORMAL;
                    PRAGMA temp_store=MEMORY;
                    PRAGMA cache_size=-65536;
                    DROP TRIGGER IF EXISTS CatalogTitles_ai;
                    DROP TRIGGER IF EXISTS CatalogTitles_ad;
                    DROP TRIGGER IF EXISTS CatalogTitles_au;";
                await pragma.ExecuteNonQueryAsync(ct);
            }

            tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
            (insTitle, insEpisode, insLink) = PreparePreparedStatements(conn, tx);

            await foreach (var rec in StreamRecordsAsync(input, ct))
            {
                ct.ThrowIfCancellationRequested();
                total++;

                if (string.IsNullOrWhiteSpace(rec.TitleName) || string.IsNullOrWhiteSpace(rec.LinkUrl))
                {
                    failed++;
                    continue;
                }

                try
                {
                    var canonical = BuildTitleKey(rec);
                    if (!titleIdsByKey.TryGetValue(canonical, out var titleId))
                    {
                        titleId = await UpsertTitleAsync(insTitle, rec, canonical, ct);
                        titleIdsByKey[canonical] = titleId;
                        titles++;
                    }

                    int? episodeId = null;
                    if (string.Equals(rec.CategoryName, "Films", StringComparison.OrdinalIgnoreCase) == false
                        && (rec.SeasonNumber > 0 || rec.EpisodeNumber > 0 || rec.IsFullSeason == 1))
                    {
                        var epKey = (titleId, rec.SeasonNumber, rec.EpisodeNumber, rec.IsFullSeason == 1);
                        if (!episodeIdsByTitleAndKey.TryGetValue(epKey, out var epId))
                        {
                            epId = await UpsertEpisodeAsync(insEpisode, titleId, rec, ct);
                            episodeIdsByTitleAndKey[epKey] = epId;
                            episodes++;
                        }
                        episodeId = epId;
                    }

                    await InsertLinkAsync(insLink, titleId, episodeId, rec, ct);
                    links++;
                }
                catch (Exception ex)
                {
                    failed++;
                    if (failed % 100 == 1)
                        _log.LogWarning(ex, "ingest skip line {N}: {Msg}", total, ex.Message);
                }

                if (total % 50_000 == 0)
                {
                    await tx.CommitAsync(ct);
                    tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
                    (insTitle, insEpisode, insLink) = PreparePreparedStatements(conn, tx);
                    progress?.Report(new IngestProgress(total, titles, episodes, links, failed));
                    _log.LogInformation("ingested {N} records ({T} titles, {E} eps, {L} links, {F} fail)",
                        total, titles, episodes, links, failed);
                }
            }

            await tx.CommitAsync(ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "ingest failed at total={N}", total);
            try { if (tx is not null) await tx.RollbackAsync(ct); } catch { /* ignore */ }
            run.Status = "failed";
            run.Notes = ex.Message;
        }
        finally
        {
            insTitle?.Dispose();
            insEpisode?.Dispose();
            insLink?.Dispose();
            if (conn is not null) await conn.DisposeAsync();
        }

        await using (var db = await _factory.CreateDbContextAsync(ct))
        {
            CatalogFts.EnsureCreated(db);
            _log.LogInformation("rebuilding FTS index...");
            CatalogFts.Rebuild(db);
        }

        run.FinishedAt = DateTimeOffset.UtcNow;
        run.TotalRecords = total;
        run.InsertedLinks = links;
        run.InsertedTitles = titles;
        run.InsertedEpisodes = episodes;
        run.FailedRecords = failed;
        if (run.Status == "running") run.Status = "succeeded";

        await using (var db = await _factory.CreateDbContextAsync(ct))
        {
            db.CatalogImportRuns.Update(run);
            await db.SaveChangesAsync(ct);
        }

        progress?.Report(new IngestProgress(total, titles, episodes, links, failed));
        return run;
    }

    private static (SqliteCommand title, SqliteCommand episode, SqliteCommand link) PreparePreparedStatements(
        SqliteConnection conn, SqliteTransaction tx)
    {
        var insTitle = conn.CreateCommand();
        insTitle.Transaction = tx;
        insTitle.CommandText = @"
            INSERT INTO CatalogTitles
                (CanonicalKey, TitleName, OriginalTitle, NormalizedTitle, ImdbId, TmdbId,
                 CategoryName, TitlePoster, FirstSeenAt, LastSeenAt, LinkCount, EpisodeCount)
            VALUES ($k,$t,$o,$n,$imdb,$tmdb,$cat,$poster,$seen,$seen,0,0)
            ON CONFLICT(CanonicalKey) DO UPDATE SET
                TitleName=excluded.TitleName,
                OriginalTitle=excluded.OriginalTitle,
                CategoryName=excluded.CategoryName,
                TitlePoster=COALESCE(excluded.TitlePoster, CatalogTitles.TitlePoster),
                LastSeenAt=excluded.LastSeenAt
            RETURNING Id;";
        insTitle.Parameters.Add(new SqliteParameter("$k", SqliteType.Text));
        insTitle.Parameters.Add(new SqliteParameter("$t", SqliteType.Text));
        insTitle.Parameters.Add(new SqliteParameter("$o", SqliteType.Text) { IsNullable = true });
        insTitle.Parameters.Add(new SqliteParameter("$n", SqliteType.Text));
        insTitle.Parameters.Add(new SqliteParameter("$imdb", SqliteType.Text) { IsNullable = true });
        insTitle.Parameters.Add(new SqliteParameter("$tmdb", SqliteType.Integer) { IsNullable = true });
        insTitle.Parameters.Add(new SqliteParameter("$cat", SqliteType.Text));
        insTitle.Parameters.Add(new SqliteParameter("$poster", SqliteType.Text) { IsNullable = true });
        insTitle.Parameters.Add(new SqliteParameter("$seen", SqliteType.Integer));
        insTitle.Prepare();

        var insEpisode = conn.CreateCommand();
        insEpisode.Transaction = tx;
        insEpisode.CommandText = @"
            INSERT INTO CatalogEpisodes
                (TitleId, SeasonNumber, EpisodeNumber, EpisodeName, EpisodePoster, IsFullSeason)
            VALUES ($tid,$s,$e,$name,$poster,$full)
            ON CONFLICT(TitleId, SeasonNumber, EpisodeNumber, IsFullSeason) DO UPDATE SET
                EpisodeName=COALESCE(excluded.EpisodeName, CatalogEpisodes.EpisodeName)
            RETURNING Id;";
        insEpisode.Parameters.Add(new SqliteParameter("$tid", SqliteType.Integer));
        insEpisode.Parameters.Add(new SqliteParameter("$s", SqliteType.Integer));
        insEpisode.Parameters.Add(new SqliteParameter("$e", SqliteType.Integer));
        insEpisode.Parameters.Add(new SqliteParameter("$name", SqliteType.Text) { IsNullable = true });
        insEpisode.Parameters.Add(new SqliteParameter("$poster", SqliteType.Text) { IsNullable = true });
        insEpisode.Parameters.Add(new SqliteParameter("$full", SqliteType.Integer));
        insEpisode.Prepare();

        var insLink = conn.CreateCommand();
        insLink.Transaction = tx;
        insLink.CommandText = @"
            INSERT INTO CatalogLinks
                (ExternalLinkId, TitleId, EpisodeId, LinkUrl, HostName, NormalizedHost,
                 QualityName, AudioLangs, SubLangs, SizeBytes, CreatedAt)
            VALUES ($eid,$tid,$epid,$url,$host,$nhost,$q,$alang,$slang,$sz,$created)
            ON CONFLICT(ExternalLinkId) DO NOTHING;";
        insLink.Parameters.Add(new SqliteParameter("$eid", SqliteType.Integer));
        insLink.Parameters.Add(new SqliteParameter("$tid", SqliteType.Integer));
        insLink.Parameters.Add(new SqliteParameter("$epid", SqliteType.Integer) { IsNullable = true });
        insLink.Parameters.Add(new SqliteParameter("$url", SqliteType.Text));
        insLink.Parameters.Add(new SqliteParameter("$host", SqliteType.Text));
        insLink.Parameters.Add(new SqliteParameter("$nhost", SqliteType.Text));
        insLink.Parameters.Add(new SqliteParameter("$q", SqliteType.Text) { IsNullable = true });
        insLink.Parameters.Add(new SqliteParameter("$alang", SqliteType.Text) { IsNullable = true });
        insLink.Parameters.Add(new SqliteParameter("$slang", SqliteType.Text) { IsNullable = true });
        insLink.Parameters.Add(new SqliteParameter("$sz", SqliteType.Integer) { IsNullable = true });
        insLink.Parameters.Add(new SqliteParameter("$created", SqliteType.Integer));
        insLink.Prepare();

        return (insTitle, insEpisode, insLink);
    }

    private async Task<int> UpsertTitleAsync(SqliteCommand cmd, CatalogRawRecord rec, string canonical, CancellationToken ct)
    {
        cmd.Parameters["$k"].Value = canonical;
        cmd.Parameters["$t"].Value = rec.TitleName!;
        cmd.Parameters["$o"].Value = (object?)rec.OriginalTitle ?? DBNull.Value;
        cmd.Parameters["$n"].Value = _normalizer.Normalize(rec.TitleName!);
        cmd.Parameters["$imdb"].Value = (object?)rec.ImdbId ?? DBNull.Value;
        cmd.Parameters["$tmdb"].Value = (object?)rec.TmdbId ?? DBNull.Value;
        cmd.Parameters["$cat"].Value = rec.CategoryName ?? "Other";
        cmd.Parameters["$poster"].Value = (object?)rec.TitlePoster ?? DBNull.Value;
        cmd.Parameters["$seen"].Value = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    private static async Task<int> UpsertEpisodeAsync(SqliteCommand cmd, int titleId, CatalogRawRecord rec, CancellationToken ct)
    {
        cmd.Parameters["$tid"].Value = titleId;
        cmd.Parameters["$s"].Value = rec.SeasonNumber;
        cmd.Parameters["$e"].Value = rec.EpisodeNumber;
        cmd.Parameters["$name"].Value = (object?)rec.EpisodeName ?? DBNull.Value;
        cmd.Parameters["$poster"].Value = (object?)rec.EpisodePoster ?? DBNull.Value;
        cmd.Parameters["$full"].Value = rec.IsFullSeason == 1 ? 1 : 0;
        var result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    private static async Task InsertLinkAsync(SqliteCommand cmd, int titleId, int? episodeId, CatalogRawRecord rec, CancellationToken ct)
    {
        cmd.Parameters["$eid"].Value = rec.LinkId;
        cmd.Parameters["$tid"].Value = titleId;
        cmd.Parameters["$epid"].Value = (object?)episodeId ?? DBNull.Value;
        cmd.Parameters["$url"].Value = rec.LinkUrl!;
        cmd.Parameters["$host"].Value = rec.HostName ?? "Unknown";
        cmd.Parameters["$nhost"].Value = NormalizeHost(rec.HostName);
        cmd.Parameters["$q"].Value = (object?)rec.QualityName ?? DBNull.Value;
        cmd.Parameters["$alang"].Value = (object?)rec.AudioLangs ?? DBNull.Value;
        cmd.Parameters["$slang"].Value = (object?)rec.SubLangs ?? DBNull.Value;
        cmd.Parameters["$sz"].Value = (object?)rec.SizeBytes ?? DBNull.Value;
        cmd.Parameters["$created"].Value = ParseCreatedAtMs(rec.CreatedAt);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private string BuildTitleKey(CatalogRawRecord rec)
    {
        // Prefer TMDB id for stable cross-import identity; fall back to imdb, then normalised title.
        if (rec.TmdbId is int tmdb && tmdb > 0) return $"tmdb:{tmdb}";
        if (!string.IsNullOrEmpty(rec.ImdbId)) return $"imdb:{rec.ImdbId}";
        var norm = _normalizer.Normalize(rec.TitleName ?? "");
        return $"name:{norm}";
    }

    private static string NormalizeHost(string? host)
    {
        if (string.IsNullOrEmpty(host)) return "unknown";
        return host.Trim().ToLowerInvariant().Replace(" ", string.Empty);
    }

    private static long ParseCreatedAtMs(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0;
        if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d))
            return d.ToUnixTimeMilliseconds();
        return 0;
    }

    private string GetConnectionString()
    {
        // Match the configured DbPath. Caller ensures the path exists.
        // We re-read it from a fresh DbContext to honor whatever the API host wired up.
        using var db = _factory.CreateDbContext();
        return db.Database.GetConnectionString() ?? "Data Source=data/linkharvester.db";
    }

    /// <summary>
    /// Streams individual records from either a top-level JSON array
    /// (<c>[ {...}, {...}, ... ]</c>) or newline-delimited JSON. We sniff the first
    /// non-whitespace character: <c>[</c> means array, anything else means NDJSON.
    /// We don't seek the input stream — instead we wrap it with a stream that
    /// "pushes back" the bytes we consumed during the sniff.
    /// </summary>
    public static async IAsyncEnumerable<CatalogRawRecord> StreamRecordsAsync(
        Stream input,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var (first, consumed) = await PeekFirstNonWsAsync(input, ct);
        var wrapped = consumed.Length == 0
            ? input
            : new PrependBufferStream(consumed, input);
        if (first == (byte)'[')
        {
            await foreach (var rec in StreamJsonArrayAsync(wrapped, ct))
                yield return rec;
        }
        else
        {
            await foreach (var rec in StreamNdjsonAsync(wrapped, ct))
                yield return rec;
        }
    }

    private static async Task<(byte first, byte[] consumed)> PeekFirstNonWsAsync(Stream s, CancellationToken ct)
    {
        var consumed = new List<byte>(8);
        var buf = new byte[1];
        while (true)
        {
            var n = await s.ReadAsync(buf.AsMemory(0, 1), ct);
            if (n == 0) return (0, consumed.ToArray());
            consumed.Add(buf[0]);
            if (buf[0] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') continue;
            return (buf[0], consumed.ToArray());
        }
    }

    private sealed class PrependBufferStream : Stream
    {
        private readonly byte[] _prepend;
        private int _prependPos;
        private readonly Stream _inner;
        public PrependBufferStream(byte[] prepend, Stream inner)
        { _prepend = prepend; _inner = inner; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_prependPos < _prepend.Length)
            {
                var n = Math.Min(count, _prepend.Length - _prependPos);
                Array.Copy(_prepend, _prependPos, buffer, offset, n);
                _prependPos += n;
                return n;
            }
            return _inner.Read(buffer, offset, count);
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_prependPos < _prepend.Length)
            {
                var n = Math.Min(buffer.Length, _prepend.Length - _prependPos);
                new ReadOnlyMemory<byte>(_prepend, _prependPos, n).CopyTo(buffer);
                _prependPos += n;
                return n;
            }
            return await _inner.ReadAsync(buffer, ct);
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static async IAsyncEnumerable<CatalogRawRecord> StreamJsonArrayAsync(
        Stream s,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var opts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
        };
        await foreach (var rec in JsonSerializer.DeserializeAsyncEnumerable<CatalogRawRecord>(s, opts, ct))
        {
            if (rec is null) continue;
            yield return rec;
        }
    }

    private static async IAsyncEnumerable<CatalogRawRecord> StreamNdjsonAsync(
        Stream s,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var opts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
        };
        using var reader = new StreamReader(s);
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            line = line.Trim();
            if (line.Length == 0 || line[0] != '{') continue;
            CatalogRawRecord? rec = null;
            try { rec = JsonSerializer.Deserialize<CatalogRawRecord>(line, opts); }
            catch { continue; }
            if (rec is not null) yield return rec;
        }
    }
}

public sealed record IngestProgress(long Read, long Titles, long Episodes, long Links, long Failed);
