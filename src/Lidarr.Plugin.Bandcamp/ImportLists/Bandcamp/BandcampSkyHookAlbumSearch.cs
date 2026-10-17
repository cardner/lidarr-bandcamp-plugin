using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using NLog;
using NzbDrone.Common.Extensions;

namespace NzbDrone.Core.ImportLists.Bandcamp
{
    /// <summary>
    /// Direct HTTP client for Lidarr metadata (SkyHook) album search.
    /// Uses <see cref="HttpClient"/> only — never Lidarr <c>IHttpClient</c> / <c>ISearchForNewAlbum</c>.
    /// </summary>
    public sealed class BandcampSkyHookAlbumSearch : IBandcampMetadataAlbumSearch, IDisposable
    {
        public const string DefaultMetadataBaseUrl = "https://api.lidarr.audio/api/v0.4";

        private readonly HttpClient _httpClient;
        private readonly Logger _logger;
        private readonly bool _ownsClient;

        public BandcampSkyHookAlbumSearch(Logger logger)
            : this(CreateDefaultClient(), logger, ownsClient: true)
        {
        }

        private BandcampSkyHookAlbumSearch(HttpClient httpClient, Logger logger, bool ownsClient)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _ownsClient = ownsClient;
        }

        public static BandcampSkyHookAlbumSearch ForTests(HttpClient httpClient, Logger logger)
        {
            return new BandcampSkyHookAlbumSearch(httpClient, logger, ownsClient: false);
        }

        private static HttpClient CreateDefaultClient()
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.All
            };

            var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(30)
            };

            client.DefaultRequestHeaders.UserAgent.ParseAdd("Lidarr.Plugin.Bandcamp/import-list");
            return client;
        }

        public BandcampMetadataAlbumMatch? FindAlbum(string albumTitle, string artistName, string? metadataSourceBaseUrl)
        {
            if (albumTitle.IsNullOrWhiteSpace())
            {
                return null;
            }

            var baseUrl = string.IsNullOrWhiteSpace(metadataSourceBaseUrl)
                ? DefaultMetadataBaseUrl
                : metadataSourceBaseUrl.Trim().TrimEnd('/');

            var albumQuery = Uri.EscapeDataString(albumTitle.Trim().ToLowerInvariant());
            var artistQuery = Uri.EscapeDataString((artistName ?? string.Empty).Trim().ToLowerInvariant());
            var url = $"{baseUrl}/search?type=album&query={albumQuery}&artist={artistQuery}&includeTracks=1";

            try
            {
                using var response = _httpClient.GetAsync(url).GetAwaiter().GetResult();
                var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                if (!response.IsSuccessStatusCode)
                {
                    // Treat HTTP errors as soft failures (same intent as catching SkyHookException).
                    throw new BandcampSkyHookHttpException(
                        (int)response.StatusCode,
                        $"SkyHook HTTP {(int)response.StatusCode} for {artistName} — {albumTitle}");
                }

                if (string.IsNullOrWhiteSpace(body))
                {
                    return null;
                }

                var results = JsonSerializer.Deserialize<List<SkyHookAlbumDto>>(body, JsonOptions);
                var first = results?.Find(r => !string.IsNullOrWhiteSpace(r.Id) && !string.IsNullOrWhiteSpace(r.ArtistId));
                if (first == null)
                {
                    return null;
                }

                var artistNameResolved = first.Artists?.Find(a =>
                    string.Equals(a.Id, first.ArtistId, StringComparison.OrdinalIgnoreCase))?.ArtistName
                    ?? first.Artists?.Find(a => !string.IsNullOrWhiteSpace(a.ArtistName))?.ArtistName
                    ?? artistName
                    ?? string.Empty;

                return new BandcampMetadataAlbumMatch
                {
                    AlbumMusicBrainzId = first.Id!,
                    AlbumTitle = first.Title ?? albumTitle,
                    ArtistMusicBrainzId = first.ArtistId!,
                    ArtistName = artistNameResolved,
                    ReleaseDate = first.ReleaseDate
                };
            }
            catch (BandcampSkyHookHttpException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new BandcampSkyHookHttpException(0, $"SkyHook request failed for {artistName} — {albumTitle}", ex);
            }
        }

        public void Dispose()
        {
            if (_ownsClient)
            {
                _httpClient.Dispose();
            }
        }

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private sealed class SkyHookAlbumDto
        {
            [JsonPropertyName("id")]
            public string? Id { get; set; }

            [JsonPropertyName("title")]
            public string? Title { get; set; }

            [JsonPropertyName("artistId")]
            public string? ArtistId { get; set; }

            [JsonPropertyName("releaseDate")]
            public DateTime? ReleaseDate { get; set; }

            [JsonPropertyName("artists")]
            public List<SkyHookArtistDto>? Artists { get; set; }
        }

        private sealed class SkyHookArtistDto
        {
            [JsonPropertyName("id")]
            public string? Id { get; set; }

            [JsonPropertyName("artistName")]
            public string? ArtistName { get; set; }
        }
    }

    /// <summary>
    /// Soft-fail signal for Import List: one bad SkyHook response must not abort the whole sync.
    /// </summary>
    public sealed class BandcampSkyHookHttpException : Exception
    {
        public int StatusCode { get; }

        public BandcampSkyHookHttpException(int statusCode, string message, Exception? inner = null)
            : base(message, inner)
        {
            StatusCode = statusCode;
        }
    }
}
