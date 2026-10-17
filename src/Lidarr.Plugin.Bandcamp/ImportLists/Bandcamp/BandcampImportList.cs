using System;
using System.Collections.Generic;
using System.Linq;
using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Auth.Bandcamp;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download.Clients.Bandcamp;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.ImportLists.Bandcamp
{
    /// <summary>
    /// Import List that emits album-level items from the authenticated Bandcamp fan collection.
    /// Lidarr maps Artist+Album names to MusicBrainz; recommend SpecificAlbum + Search on Add.
    /// </summary>
    public class BandcampImportList : ImportListBase<BandcampImportListSettings>
    {
        private readonly BandcampApiClient _apiClient;

        public override string Name => "Bandcamp";

        public override ImportListType ListType => ImportListType.Other;

        public override TimeSpan MinRefreshInterval => TimeSpan.FromHours(6);

        public BandcampImportList(BandcampApiClient apiClient,
                                  IImportListStatusService importListStatusService,
                                  IConfigService configService,
                                  IParsingService parsingService,
                                  Logger logger)
            : base(importListStatusService, configService, parsingService, logger)
        {
            _apiClient = apiClient;
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

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var purchase in collection)
                {
                    var artist = purchase.BandName?.Trim();
                    var album = purchase.Title?.Trim();

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

                    items.Add(new ImportListItemInfo
                    {
                        Artist = artist,
                        Album = album,
                        ReleaseDate = purchase.GetPurchaseDateUtc() ?? DateTime.UtcNow
                    });
                }

                _logger.Debug("Bandcamp import list: {0} album item(s) from collection", items.Count);
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
    }
}
