using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Http;
using NzbDrone.Core.Auth.Bandcamp;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Localization;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.RemotePathMappings;

namespace NzbDrone.Core.Download.Clients.Bandcamp
{
    /// <summary>
    /// Lidarr download client for Bandcamp. Uses cookie-based authentication to
    /// resolve purchased albums and download them as FLAC archives.
    ///
    /// Lifecycle:
    ///   Download() — enqueues a download via the background task queue
    ///   GetItems() — reports current queue state to Lidarr for import tracking
    ///   RemoveItem() — cancels/removes a download and optionally deletes data
    ///   GetStatus() — reports output root folder for Lidarr's completed download monitoring
    ///   Test() — validates cookie auth (fan_id resolution) and download path accessibility
    ///
    /// All credential-safe logging is inherited from BandcampHttpClient.
    /// </summary>
    public class BandcampDownloadClient : DownloadClientBase<BandcampDownloadSettings>
    {
        private readonly IBandcampDownloadQueue _taskQueue;
        private readonly BandcampApiClient _apiClient;
        private readonly IAlbumService _albumService;
        private readonly IReleaseService _releaseService;
        private readonly ITrackService _trackService;

        public override string Name => "Bandcamp";
        public override string Protocol => nameof(BandcampDownloadProtocol);

        public BandcampDownloadClient(
            IBandcampDownloadQueue taskQueue,
            BandcampApiClient apiClient,
            IAlbumService albumService,
            IReleaseService releaseService,
            ITrackService trackService,
            IConfigService configService,
            IDiskProvider diskProvider,
            IRemotePathMappingService remotePathMappingService,
            ILocalizationService localizationService,
            Logger logger)
            : base(configService, diskProvider, remotePathMappingService, localizationService, logger)
        {
            _taskQueue = taskQueue;
            _apiClient = apiClient;
            _albumService = albumService;
            _releaseService = releaseService;
            _trackService = trackService;
        }

        /// <summary>
        /// Enqueues an album download from Bandcamp. Extracts the album URL from the
        /// release's DownloadUrl (set by the Bandcamp indexer), creates a BandcampDownloadItem,
        /// and enqueues it for background processing via the DownloadTaskQueue.
        /// </summary>
        /// <returns>The download ID for tracking.</returns>
        public override async Task<string> Download(RemoteAlbum remoteAlbum, IIndexer indexer)
        {
            var albumUrl = remoteAlbum.Release.DownloadUrl;

            if (string.IsNullOrWhiteSpace(albumUrl))
            {
                _logger.Debug("Bandcamp download client: Release has no DownloadUrl — cannot enqueue download");
                throw new DownloadException("Cannot download: release has no Bandcamp album URL.");
            }

            var title = remoteAlbum.Release.Title ?? remoteAlbum.Release.Album ?? albumUrl;
            var downloadId = Guid.NewGuid().ToString("N");
            var retagContext = BuildRetagContext(remoteAlbum);

            // Fail fast with a Queue-visible message when the configured root is missing/unwritable
            // (common Docker misconfig: /downloads/downloads when only /downloads is mounted).
            var pathError = BandcampPathGuard.EnsureWritable(_diskProvider, Settings.DownloadPath, _logger);
            if (pathError != null)
            {
                _logger.Error("Bandcamp download client: Refusing grab — {0}", pathError);
                throw new DownloadException(pathError);
            }

            // Build a unique working directory under the configured download root so retries
            // and concurrent grabs never delete or overwrite each other's extracted files.
            var downloadPath = BandcampPathGuard.Normalize(Settings.DownloadPath);
            var folderTitle = retagContext != null
                ? $"{retagContext.ArtistName} - {retagContext.AlbumTitle}"
                : title;
            var albumDir = MakeValidDirectoryName(folderTitle);
            var outputPath = System.IO.Path.Combine(downloadPath, $"{albumDir}-{downloadId[..8]}");

            var item = new BandcampDownloadItem
            {
                DownloadId = downloadId,
                AlbumUrl = albumUrl,
                Title = title,
                OutputPath = outputPath,
                Cookies = Settings.Cookies,
                RetagContext = retagContext,
                // Seed TotalSize from the indexer release so Activity can show a bar
                // before CDN Content-Length arrives (and if the CDN omits it).
                TotalSize = remoteAlbum.Release.Size > 0 ? remoteAlbum.Release.Size : 0
            };

            await _taskQueue.EnqueueAsync(item).ConfigureAwait(false);

            _logger.Info("Bandcamp download client: Enqueued download {0} for '{1}' -> {2}",
                downloadId, title, outputPath);

            return downloadId;
        }

        /// <summary>
        /// Returns all tracked download items mapped to Lidarr's DownloadClientItem format.
        /// Lidarr polls this to check download progress and trigger import on completion.
        /// </summary>
        public override IEnumerable<DownloadClientItem> GetItems()
        {
            var items = _taskQueue.GetItems();

            foreach (var kvp in items)
            {
                var bcItem = kvp.Value;

                var clientItem = new DownloadClientItem
                {
                    DownloadClientInfo = DownloadClientItemClientInfo.FromDownloadClient(this, false),
                    DownloadId = bcItem.DownloadId,
                    Title = bcItem.Title,
                    TotalSize = bcItem.TotalSize,
                    RemainingSize = ComputeRemainingSize(bcItem),
                    RemainingTime = EstimateRemainingTime(bcItem),
                    OutputPath = new OsPath(bcItem.OutputPath),
                    Status = MapStatus(bcItem.Status),
                    Message = FormatQueueMessage(bcItem),
                    CanBeRemoved = bcItem.Status == BandcampDownloadStatus.Completed ||
                                   bcItem.Status == BandcampDownloadStatus.Failed,
                    CanMoveFiles = bcItem.Status == BandcampDownloadStatus.Completed
                };

                yield return clientItem;
            }
        }

        private static long ComputeRemainingSize(BandcampDownloadItem item)
        {
            if (item.TotalSize <= 0)
            {
                return 0;
            }

            var remaining = item.TotalSize - item.DownloadedBytes;
            return remaining > 0 ? remaining : 0;
        }

        /// <summary>
        /// ETA from observed transfer rate once bytes are flowing. Null while resolving
        /// or when rate cannot be estimated (matches qBittorrent "unknown" → "-").
        /// </summary>
        internal static TimeSpan? EstimateRemainingTime(BandcampDownloadItem item)
        {
            if (item.Status == BandcampDownloadStatus.Completed)
            {
                return TimeSpan.Zero;
            }

            if (item.Status is BandcampDownloadStatus.Failed or BandcampDownloadStatus.Queued)
            {
                return null;
            }

            if (item.Status is BandcampDownloadStatus.Resolving or BandcampDownloadStatus.Extracting)
            {
                return null;
            }

            if (item.TotalSize <= 0 || item.DownloadedBytes <= 0 || item.DownloadStartedAt == null)
            {
                return null;
            }

            var elapsed = DateTime.UtcNow - item.DownloadStartedAt.Value;
            if (elapsed.TotalSeconds < 0.5)
            {
                return null;
            }

            var bytesPerSecond = item.DownloadedBytes / elapsed.TotalSeconds;
            if (bytesPerSecond <= 0)
            {
                return null;
            }

            var remainingBytes = item.TotalSize - item.DownloadedBytes;
            if (remainingBytes <= 0)
            {
                return TimeSpan.Zero;
            }

            var seconds = remainingBytes / bytesPerSecond;
            if (seconds > 365 * 24 * 3600)
            {
                return null;
            }

            return TimeSpan.FromSeconds(seconds);
        }

        internal static string? FormatQueueMessage(BandcampDownloadItem item)
        {
            if (!string.IsNullOrWhiteSpace(item.ErrorMessage))
            {
                return item.ErrorMessage;
            }

            if (item.Status == BandcampDownloadStatus.Failed)
            {
                return BandcampPathGuard.FormatDownloadFailure(item.Phase, "unknown error");
            }

            return item.Status switch
            {
                BandcampDownloadStatus.Queued => "Queued",
                BandcampDownloadStatus.Resolving => "Resolving Bandcamp purchase…",
                BandcampDownloadStatus.Downloading when item.TotalSize > 0 =>
                    $"Downloading {FormatBytes(item.DownloadedBytes)} / {FormatBytes(item.TotalSize)}",
                BandcampDownloadStatus.Downloading when item.DownloadedBytes > 0 =>
                    $"Downloading {FormatBytes(item.DownloadedBytes)}…",
                BandcampDownloadStatus.Downloading => "Downloading…",
                BandcampDownloadStatus.Extracting => "Extracting archive…",
                BandcampDownloadStatus.Completed => null,
                _ => null
            };
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024)
            {
                return $"{bytes} B";
            }

            if (bytes < 1024 * 1024)
            {
                return $"{bytes / 1024.0:0.#} KB";
            }

            if (bytes < 1024L * 1024 * 1024)
            {
                return $"{bytes / (1024.0 * 1024.0):0.#} MB";
            }

            return $"{bytes / (1024.0 * 1024.0 * 1024.0):0.##} GB";
        }

        /// <summary>
        /// Removes a download item from tracking. If deleteData is true, also deletes
        /// the downloaded files from disk using Lidarr's built-in DeleteItemData helper.
        /// </summary>
        public override void RemoveItem(DownloadClientItem item, bool deleteData)
        {
            _logger.Debug("Bandcamp download client: Removing item {0} (deleteData={1})",
                item.DownloadId, deleteData);

            // Remove from the shared queue/registry state
            _taskQueue.RemoveItem(item.DownloadId);

            if (deleteData)
            {
                DeleteItemData(item);
            }
        }

        /// <summary>
        /// Reports the download client status to Lidarr. Bandcamp downloads are local
        /// (IsLocalhost=true) and output to the configured DownloadPath.
        /// Lidarr uses OutputRootFolders to monitor for completed downloads.
        /// </summary>
        public override DownloadClientInfo GetStatus()
        {
            var outputPath = Settings.DownloadPath;

            return new DownloadClientInfo
            {
                IsLocalhost = true,
                OutputRootFolders = new List<OsPath>
                {
                    new(outputPath)
                }
            };
        }

        /// <summary>
        /// Validates the download client configuration:
        /// 1. Tests cookie auth by attempting fan_id resolution
        /// 2. Validates the download path exists and is writable
        /// </summary>
        protected override void Test(List<ValidationFailure> failures)
        {
            _logger.Debug("Bandcamp download client: Running connection test");

            // Test cookie auth — shape check + fan_id probe with actionable messages
            try
            {
                var probe = _apiClient.ProbeAuthAsync(Settings.Cookies).GetAwaiter().GetResult();

                if (!probe.Success)
                {
                    failures.Add(new ValidationFailure("Cookies", probe.Message));
                    _logger.Debug("Bandcamp download client: Cookie auth test failed — {0}", probe.FailureKind);
                }
                else
                {
                    _logger.Debug("Bandcamp download client: Cookie auth test passed — fan_id resolved");
                }
            }
            catch (Exception ex)
            {
                // Keep User-Agent failures readable if anything still hits Lidarr's dispatcher.
                failures.Add(new ValidationFailure("Cookies",
                    BandcampIdentityAuth.DescribeFailure(BandcampAuthFailureKind.NetworkError, ex.Message)));
                _logger.Debug(ex, "Bandcamp download client: Cookie auth test threw exception");
            }

            // Test download path — create if needed, prove writable, warn on doubled Docker paths.
            var pathError = BandcampPathGuard.EnsureWritable(_diskProvider, Settings.DownloadPath, _logger);
            if (pathError != null)
            {
                failures.Add(new ValidationFailure("DownloadPath", pathError));
            }
            else
            {
                _logger.Debug("Bandcamp download client: Download path test passed for '{0}'", Settings.DownloadPath);
            }

            var doubledWarning = BandcampPathGuard.SuspiciousPathWarning(Settings.DownloadPath);
            if (doubledWarning != null)
            {
                failures.Add(doubledWarning);
            }
        }

        /// <summary>
        /// Builds a conservative retagging context from Lidarr's matched album metadata.
        /// We always carry canonical artist/album information. Release-level MBIDs are only
        /// included when we can identify a single preferred release safely.
        /// </summary>
        private BandcampRetagContext? BuildRetagContext(RemoteAlbum remoteAlbum)
        {
            if (remoteAlbum.Artist == null || remoteAlbum.Albums == null || remoteAlbum.Albums.Count != 1)
            {
                return null;
            }

            var album = _albumService.GetAlbum(remoteAlbum.Albums[0].Id);
            var artist = _albumService.GetAlbum(album.Id).Artist?.Value ?? remoteAlbum.Artist;
            var release = SelectPreferredRelease(album.Id);
            var tracks = BuildTrackContexts(album.Id, release?.Id);

            return new BandcampRetagContext
            {
                ArtistName = artist.Name,
                ArtistMusicBrainzId = artist.ForeignArtistId,
                AlbumTitle = album.Title,
                AlbumMusicBrainzId = album.ForeignAlbumId,
                AlbumType = album.AlbumType,
                AlbumDisambiguation = album.Disambiguation,
                AlbumReleaseDate = album.ReleaseDate,
                Genres = album.Genres.Any() ? album.Genres.ToArray() : Array.Empty<string>(),
                PreferredRelease = release == null ? null : new BandcampRetagReleaseContext
                {
                    ReleaseMusicBrainzId = release.ForeignReleaseId,
                    ReleaseArtistMusicBrainzId = artist.ForeignArtistId,
                    ReleaseStatus = release.Status,
                    Label = release.Label.FirstOrDefault(),
                    ReleaseDate = release.ReleaseDate,
                    DiscCount = release.Media.Count,
                    MediaByDisc = release.Media.ToDictionary(m => m.Number, m => m.Format)
                },
                Tracks = tracks
            };
        }

        private AlbumRelease? SelectPreferredRelease(int albumId)
        {
            var releases = _releaseService.GetReleasesByAlbum(albumId);
            if (releases.Count == 1)
            {
                return releases[0];
            }

            var monitored = releases.Where(r => r.Monitored).ToList();
            if (monitored.Count == 1)
            {
                return monitored[0];
            }

            return null;
        }

        private List<BandcampRetagTrackContext> BuildTrackContexts(int albumId, int? preferredReleaseId)
        {
            var tracks = preferredReleaseId.HasValue
                ? _trackService.GetTracksByRelease(preferredReleaseId.Value)
                : _trackService.GetTracksByAlbum(albumId);

            return tracks
                .GroupBy(t => t.AbsoluteTrackNumber)
                .Select(g =>
                {
                    var distinctTitles = g.Select(t => t.Title).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    if (distinctTitles.Count > 1)
                    {
                        return null;
                    }

                    var distinctRecordings = g.Select(t => t.ForeignRecordingId).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    if (distinctRecordings.Count > 1)
                    {
                        return null;
                    }

                    var track = g.OrderBy(t => t.MediumNumber).ThenBy(t => t.AbsoluteTrackNumber).First();
                    return new BandcampRetagTrackContext
                    {
                        AbsoluteTrackNumber = track.AbsoluteTrackNumber,
                        MediumNumber = track.MediumNumber,
                        Title = track.Title,
                        RecordingMusicBrainzId = distinctRecordings.SingleOrDefault(),
                        ReleaseTrackMusicBrainzId = preferredReleaseId.HasValue ? track.ForeignTrackId : null
                    };
                })
                .Where(t => t != null)
                .OrderBy(t => t!.AbsoluteTrackNumber)
                .Select(t => t!)
                .ToList();
        }

        /// <summary>
        /// Maps Bandcamp download status to Lidarr's DownloadItemStatus.
        /// </summary>
        private static DownloadItemStatus MapStatus(BandcampDownloadStatus status)
        {
            return status switch
            {
                BandcampDownloadStatus.Queued => DownloadItemStatus.Queued,
                BandcampDownloadStatus.Resolving => DownloadItemStatus.Downloading,
                BandcampDownloadStatus.Downloading => DownloadItemStatus.Downloading,
                BandcampDownloadStatus.Extracting => DownloadItemStatus.Downloading,
                BandcampDownloadStatus.Completed => DownloadItemStatus.Completed,
                BandcampDownloadStatus.Failed => DownloadItemStatus.Failed,
                _ => DownloadItemStatus.Queued
            };
        }

        /// <summary>
        /// Creates a filesystem-safe directory name from a release title.
        /// Strips characters invalid on Windows/Linux and collapses whitespace.
        /// </summary>
        private static string MakeValidDirectoryName(string title)
        {
            var invalid = System.IO.Path.GetInvalidFileNameChars();
            var name = title;

            foreach (var c in invalid)
            {
                name = name.Replace(c, ' ');
            }

            // Also remove characters invalid in directory names but not in GetInvalidFileNameChars
            name = name.Replace('/', ' ').Replace('\\', ' ').Replace(':', ' ');

            // Collapse whitespace and trim
            while (name.Contains("  "))
            {
                name = name.Replace("  ", " ");
            }

            return name.Trim();
        }
    }
}
