namespace LinkHarvester.Persistence.Cards;

/// <summary>
/// Denormalised read-model row for one inbox card. Owned by <see cref="ICardKeeper"/>:
/// every base-table mutation that could affect what the inbox shows updates
/// the matching row inside the same transaction. The card schema is shaped
/// after <c>InboxCardDto</c> so the read endpoint is a flat <c>SELECT</c>
/// with no joins.
///
/// <see cref="Visible"/> is a soft-delete flag: cards stay in the table when
/// a title leaves the inbox so we can flip them back without re-deriving
/// everything if the user un-submits. Reader filters on
/// <c>(Visible, UpdatedAt DESC)</c> via a covering index.
///
/// <see cref="ChosenArticleJson"/> and <see cref="OtherVariantsJson"/> carry
/// the pre-rendered <c>ArticleDto</c> payloads (best variant + the rest),
/// so the reader doesn't touch <c>Articles</c> at all. The writer picks the
/// best variant once, at card-build time.
/// </summary>
public class InboxCardEntity
{
    public int TitleId { get; set; }
    public int? CatalogTitleId { get; set; }
    public string DisplayTitle { get; set; } = string.Empty;
    public string? Poster { get; set; }
    public int? Year { get; set; }
    public double? Rating { get; set; }
    public string GenresJson { get; set; } = "[]";
    public bool MetadataUncertain { get; set; }
    public string Kind { get; set; } = string.Empty;
    public int? SeasonNumber { get; set; }
    public bool BetterAvailable { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string ChosenArticleJson { get; set; } = "{}";
    public string OtherVariantsJson { get; set; } = "[]";
    public bool Visible { get; set; }
}

/// <summary>
/// Flat catalog-browse row. One per <see cref="Catalog.CatalogTitleEntity"/>.
/// Carries every column the catalog search DTO needs so the reader avoids
/// joins to CatalogTitles, CatalogTitleMetadata, and CatalogEpisodes.
///
/// Filterable scalar columns (Year, Rating, Category, etc.) are stored
/// directly. Multi-valued filters live in companion tables:
///   <see cref="CatalogCardGenreEntity"/> — one row per (card, genre).
///   <see cref="CatalogCardLinkFacetEntity"/> — one row per link, with the
///   host / quality / audio / size combination indexed for fast filter
///   intersection.
///
/// <see cref="SeasonMin"/> / <see cref="SeasonMax"/> are populated by
/// the keeper from <c>CatalogEpisodes</c>, killing the per-row correlated
/// subqueries the previous reader generated.
/// </summary>
public class CatalogCardEntity
{
    public int TitleId { get; set; }
    public string TitleName { get; set; } = string.Empty;
    public string? OriginalTitle { get; set; }
    public string NormalizedTitle { get; set; } = string.Empty;
    public string CategoryName { get; set; } = string.Empty;
    public string? TitlePoster { get; set; }
    public int LinkCount { get; set; }
    public int EpisodeCount { get; set; }
    public int? Year { get; set; }
    public double? Rating { get; set; }
    public int? Runtime { get; set; }
    public string? Overview { get; set; }
    public string GenresJson { get; set; } = "[]";
    public string? OriginalLanguage { get; set; }
    public string? EnrichmentSource { get; set; }
    public bool MetadataUncertain { get; set; }
    public bool HasMetadata { get; set; }
    public int? SeasonMin { get; set; }
    public int? SeasonMax { get; set; }
    public double? Popularity { get; set; }
    public bool IsHidden { get; set; }
}

/// <summary>
/// Join row backing the catalog's genre filter. The previous reader did a
/// <c>LIKE '%Comedy%'</c> against <c>GenresJson</c> per metadata row — a
/// guaranteed full scan. This table makes "title matches any of these
/// genres" an index-only intersection.
/// </summary>
public class CatalogCardGenreEntity
{
    public int CardId { get; set; }
    public string Genre { get; set; } = string.Empty;
}

/// <summary>
/// Per-link facet row. One row per <c>CatalogLinkEntity</c>, carrying
/// only the fields the four catalog filters need:
///   host, quality, audio, size.
///
/// Catalog search's <c>EXISTS</c> subquery becomes a single index seek
/// into this table — no joins to CatalogLinks at read time.
/// </summary>
public class CatalogCardLinkFacetEntity
{
    public int Id { get; set; }
    public int CardId { get; set; }
    public string NormalizedHost { get; set; } = string.Empty;
    public string? QualityName { get; set; }
    public string? AudioLangs { get; set; }
    public long? SizeBytes { get; set; }
}
