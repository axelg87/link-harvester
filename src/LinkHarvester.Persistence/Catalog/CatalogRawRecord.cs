using System.Text.Json.Serialization;

namespace LinkHarvester.Persistence.Catalog;

/// <summary>
/// Shape of one entry in the Hydracker JSON dump. Field names match the dump exactly
/// so we can deserialise with default JsonSerializer settings.
/// </summary>
public sealed class CatalogRawRecord
{
    [JsonPropertyName("imdb_id")] public string? ImdbId { get; set; }
    [JsonPropertyName("link_id")] public long LinkId { get; set; }
    [JsonPropertyName("tmdb_id")] public int? TmdbId { get; set; }
    [JsonPropertyName("link_url")] public string? LinkUrl { get; set; }
    [JsonPropertyName("host_name")] public string? HostName { get; set; }
    [JsonPropertyName("sub_langs")] public string? SubLangs { get; set; }
    [JsonPropertyName("created_at")] public string? CreatedAt { get; set; }
    [JsonPropertyName("size_bytes")] public long? SizeBytes { get; set; }
    [JsonPropertyName("size_human")] public string? SizeHuman { get; set; }
    [JsonPropertyName("title_name")] public string? TitleName { get; set; }
    [JsonPropertyName("audio_langs")] public string? AudioLangs { get; set; }
    [JsonPropertyName("episode_name")] public string? EpisodeName { get; set; }
    [JsonPropertyName("quality_name")] public string? QualityName { get; set; }
    [JsonPropertyName("title_poster")] public string? TitlePoster { get; set; }
    [JsonPropertyName("category_name")] public string? CategoryName { get; set; }
    [JsonPropertyName("season_number")] public int SeasonNumber { get; set; }
    [JsonPropertyName("episode_number")] public int EpisodeNumber { get; set; }
    [JsonPropertyName("episode_poster")] public string? EpisodePoster { get; set; }
    [JsonPropertyName("is_full_season")] public int IsFullSeason { get; set; }
    [JsonPropertyName("original_title")] public string? OriginalTitle { get; set; }
}
