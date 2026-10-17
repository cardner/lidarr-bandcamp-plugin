using System;
using System.Collections.Generic;
using System.Linq;
using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Auth.Bandcamp;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download.Clients.Bandcamp;
using NzbDrone.Core.Indexers.Bandcamp;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.ThingiProvider;

namespace NzbDrone.Core.ImportLists.Bandcamp
{
    /// <summary>
    /// Import List that emits album-level items from the authenticated Bandcamp fan collection.
    /// Resolves MusicBrainz IDs inside Fetch (per-item try/catch) so a SkyHook 500 cannot abort
    /// the host ImportListSync loop the way raw title-only items can.
    /// </summary>
    public class BandcampImportList : ImportListBase<BandcampImportListSettings>
    {
        private readonly BandcampApiClient _apiClient;
        private readonly ISearchForNewAlbum _albumSearch;

        public override string Name => "Bandcamp";

        public override ImportListType ListType => ImportListType.Other;

        public override TimeSpan MinRefreshInterval => TimeSpan.FromHours(6);

        public override ProviderMessage Message => new ProviderMessage(
            "Adds purchased Bandcamp albums into Lidarr via MusicBrainz. " +
            "This list pre-resolves MBIDs (skipping SkyHook failures per album) so one bad lookup " +
            "does not abort the whole sync. Enable Automatic Add + Search for New Items; " +
            "prefer Monitor → Specific Album; enable Bandcamp in Delay Profiles. " +
            "Indexer RSS only grabs albums already in your library.",
            ProviderMessageType.Info);

        public override IEnumerable<ProviderDefinition> DefaultDefinitions
        {
            get
            {
                yield return new ImportListDefinition
                {
                    EnableAutomaticAdd = true,
                    ShouldSearch = true,
                    ShouldMonitor = ImportListMonitorType.SpecificAlbum,
                    ShouldMonitorExisting = true,
                    Implementation = GetType().Name,
                    Settings = new BandcampImportListSettings()
                };
            }
        }

        public BandcampImportList(BandcampApiClient apiClient,
                                  ISearchForNewAlbum albumSearch,
                                  IImportListStatusService importListStatusService,
                                  IConfigService configService,
                                  IParsingService parsingService,
                                  Logger logger)
            : base(importListStatusService, configService, parsingService, logger)
        {
            _apiClient = apiClient;
            _albumSearch = albumSearch;
        }

        public override IList<ImportListItemInfo> Fetch()
        {
            var items = new List<ImportListItemInfo>();

            try
            {
                var probe = _apiClient.ProbeAuthAsync(Settings.Cookies).GetAwaiter().GetResult();
                if (!probe.Success || probe.FanId == null)
                {
                    _logger.Warn("Bandcamp import list: auth failed — {0}", probe.Message);
                    _importListStatusService.RecordFailure(Definition.Id);
                    return CleanupListItems(items);
                }

                var cookies = string.IsNullOrWhiteSpace(probe.NormalizedCookies)
                    ? Settings.Cookies
                    : probe.NormalizedCookies;

                var collection = _apiClient.GetDownloadableCollectionAsync(cookies!, probe.FanId.Value)
                    .GetAwaiter()
                    .GetResult();

                var candidates = BuildNormalizedCandidates(collection);
                var mapped = 0;
                var noMatch = 0;
                var skyhookErrors = 0;
                var skippedTracks = collection.Count(c => IsTrackOnly(c));

                foreach (var candidate in candidates)
                {
                    var resolved = TryResolveAlbum(candidate.Artist, candidate.Album, out var failureKind);
                    if (resolved == null)
                    {
                        if (failureKind == ResolveFailureKind.SkyHookError)
                        {
                            skyhookErrors++;
                        }
                        else
                        {
                            noMatch++;
                        }

                        continue;
                    }

                    items.Add(resolved);
                    mapped++;
                }

                _logger.Info(
                    "Bandcamp import list: candidates={0}, mapped={1}, noMatch={2}, skyhookErrors={3}, skippedTracks={4}",
                    candidates.Count,
                    mapped,
                    noMatch,
                    skyhookErrors,
                    skippedTracks);

                if (mapped == 0 && candidates.Count > 0)
                {
                    _logger.Warn(
                        "Bandcamp import list: 0/{0} purchases mapped to MusicBrainz. " +
                        "SkyHook errors={1}, no-match={2}. Check LidarrAPI/SkyHook health or add albums manually.",
                        candidates.Count,
                        skyhookErrors,
                        noMatch);
                }

                _importListStatusService.RecordSuccess(Definition.Id);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Bandcamp import list fetch failed for {0}", Definition.Name);
                _importListStatusService.RecordFailure(Definition.Id);
            }

            return CleanupListItems(items);
        }

        protected override void Test(List<ValidationFailure> failures)
        {
            try
            {
                var probe = _apiClient.ProbeAuthAsync(Settings.Cookies).GetAwaiter().GetResult();
                if (!probe.Success || probe.FanId == null)
                {
                    failures.Add(new ValidationFailure("Cookies", probe.Message));
                    return;
                }

                var cookies = string.IsNullOrWhiteSpace(probe.NormalizedCookies)
                    ? Settings.Cookies
                    : probe.NormalizedCookies;

                var collection = _apiClient.GetDownloadableCollectionAsync(cookies!, probe.FanId.Value, maxPages: 1)
                    .GetAwaiter()
                    .GetResult();

                if (collection.Empty())
                {
                    failures.Add(new ValidationFailure(
                        "Cookies",
                        BandcampIdentityAuth.DescribeFailure(BandcampAuthFailureKind.EmptyCollection)));
                }
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Bandcamp import list test failed");
                failures.Add(new ValidationFailure(
                    "Cookies",
                    BandcampIdentityAuth.DescribeFailure(BandcampAuthFailureKind.NetworkError, ex.Message)));
            }
        }

        private List<NormalizedCandidate> BuildNormalizedCandidates(List<BandcampCollectionItem> collection)
        {
            var results = new List<NormalizedCandidate>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var purchase in collection)
            {
                if (IsTrackOnly(purchase))
                {
                    continue;
                }

                var artist = BandcampIndexer.NormalizeReleaseComponent(purchase.BandName);
                var album = BandcampIndexer.NormalizeAlbumTitle(artist, purchase.Title);

                if (artist.IsNullOrWhiteSpace() || album.IsNullOrWhiteSpace())
                {
                    continue;
                }

                var key = !string.IsNullOrWhiteSpace(purchase.ItemUrl)
                    ? purchase.ItemUrl!
                    : $"{artist}|{album}";

                if (!seen.Add(key))
                {
                    continue;
                }

                results.Add(new NormalizedCandidate(
                    artist,
                    album,
                    purchase.GetPurchaseDateUtc() ?? DateTime.UtcNow,
                    purchase.ItemUrl));
            }

            return results;
        }

        private ImportListItemInfo? TryResolveAlbum(string artist, string album, out ResolveFailureKind failureKind)
        {
            failureKind = ResolveFailureKind.None;

            try
            {
                var matches = _albumSearch.SearchForNewAlbum(album, artist);
                var mapped = matches?.FirstOrDefault();
                if (mapped == null || mapped.ForeignAlbumId.IsNullOrWhiteSpace())
                {
                    failureKind = ResolveFailureKind.NoMatch;
                    _logger.Debug("Bandcamp import list: no MusicBrainz match for {0} — {1}", artist, album);
                    return null;
                }

                var artistMbId = GetArtistMusicBrainzId(mapped);
                if (artistMbId.IsNullOrWhiteSpace())
                {
                    failureKind = ResolveFailureKind.NoMatch;
                    _logger.Debug(
                        "Bandcamp import list: MusicBrainz album {0} missing artist id for {1} — {2}",
                        mapped.ForeignAlbumId,
                        artist,
                        album);
                    return null;
                }

                return new ImportListItemInfo
                {
                    Artist = mapped.Artist?.Value?.Name ?? mapped.ArtistMetadata?.Value?.Name ?? artist,
                    Album = mapped.Title ?? album,
                    ArtistMusicBrainzId = artistMbId,
                    AlbumMusicBrainzId = mapped.ForeignAlbumId,
                    ReleaseDate = mapped.ReleaseDate ?? DateTime.UtcNow
                };
            }
            catch (Exception ex)
            {
                failureKind = ResolveFailureKind.SkyHookError;
                _logger.Warn(
                    ex,
                    "Bandcamp import list: SkyHook/metadata error for {0} — {1} (continuing with remaining purchases)",
                    artist,
                    album);
                return null;
            }
        }

        private static string? GetArtistMusicBrainzId(Album mapped)
        {
            var fromArtist = mapped.Artist?.Value?.Metadata?.Value?.ForeignArtistId;
            if (!fromArtist.IsNullOrWhiteSpace())
            {
                return fromArtist;
            }

            return mapped.ArtistMetadata?.Value?.ForeignArtistId;
        }

        private static bool IsTrackOnly(BandcampCollectionItem item)
        {
            var type = item.ItemType?.Trim();
            return type != null &&
                   (type.Equals("track", StringComparison.OrdinalIgnoreCase) ||
                    type.Equals("t", StringComparison.OrdinalIgnoreCase));
        }

        private enum ResolveFailureKind
        {
            None,
            NoMatch,
            SkyHookError
        }

        private sealed record NormalizedCandidate(string Artist, string Album, DateTime PurchasedUtc, string? ItemUrl);
    }
}
