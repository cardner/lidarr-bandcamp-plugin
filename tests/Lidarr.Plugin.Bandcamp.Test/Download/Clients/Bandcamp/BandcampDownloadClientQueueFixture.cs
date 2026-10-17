using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients.Bandcamp;
using NzbDrone.Core.Http.Bandcamp;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Localization;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.RemotePathMappings;
using Xunit;

namespace Lidarr.Plugin.Bandcamp.Test.Download.Clients.Bandcamp
{
    public class BandcampDownloadClientQueueFixture
    {
        [Fact]
        public async Task Download_QueuedByOneClient_IsVisibleFromAnotherClientInstance()
        {
            var logger = LogManager.GetCurrentClassLogger();
            var httpClient = new Mock<IHttpClient>();
            var apiClient = new BandcampApiClient(BandcampHttpClient.ForTests(httpClient.Object, logger), logger);
            var registry = new BandcampDownloadRegistry();
            var proxy = new BlockingProxy();
            using var queue = new DownloadTaskQueue(proxy, registry, logger);

            var client1 = CreateClient(queue, apiClient, logger);
            var client2 = CreateClient(queue, apiClient, logger);

            var remoteAlbum = new RemoteAlbum
            {
                Release = new ReleaseInfo
                {
                    Title = "Fresh - Fresh [FLAC]",
                    Album = "Fresh",
                    DownloadUrl = "https://bandcamp.com/download?type=album&id=111#format=flac"
                }
            };

            var downloadId = await client1.Download(remoteAlbum, Mock.Of<IIndexer>());

            var items = client2.GetItems().ToList();

            var item = Assert.Single(items.Where(i => i.DownloadId == downloadId));
            Assert.Equal("Fresh - Fresh [FLAC]", item.Title);
            Assert.True(item.Status == DownloadItemStatus.Queued || item.Status == DownloadItemStatus.Downloading);

            proxy.Release();
        }

        private static BandcampDownloadClient CreateClient(IBandcampDownloadQueue queue, BandcampApiClient apiClient, Logger logger)
        {
            var disk = new Mock<IDiskProvider>();
            disk.Setup(d => d.FolderExists("/tmp/bandcamp")).Returns(true);
            disk.Setup(d => d.FolderWritable("/tmp/bandcamp")).Returns(true);
            disk.Setup(d => d.CreateFolder(It.IsAny<string>()));
            disk.Setup(d => d.DeleteFolder(It.IsAny<string>(), It.IsAny<bool>()));

            var client = new BandcampDownloadClient(
                queue,
                apiClient,
                Mock.Of<IAlbumService>(),
                Mock.Of<IReleaseService>(),
                Mock.Of<ITrackService>(),
                Mock.Of<IConfigService>(),
                disk.Object,
                Mock.Of<IRemotePathMappingService>(),
                Mock.Of<ILocalizationService>(),
                logger);

            client.Definition = new DownloadClientDefinition
            {
                Name = "Bandcamp",
                Settings = new BandcampDownloadSettings
                {
                    Cookies = "identity=testcookie",
                    DownloadPath = "/tmp/bandcamp"
                }
            };

            return client;
        }

        [Fact]
        public async Task Download_UnwritablePath_ThrowsWithPathNotWritableMessage()
        {
            var logger = LogManager.GetCurrentClassLogger();
            var httpClient = new Mock<IHttpClient>();
            var apiClient = new BandcampApiClient(BandcampHttpClient.ForTests(httpClient.Object, logger), logger);
            var registry = new BandcampDownloadRegistry();
            var proxy = new BlockingProxy();
            using var queue = new DownloadTaskQueue(proxy, registry, logger);

            var disk = new Mock<IDiskProvider>();
            disk.Setup(d => d.FolderExists("/downloads/downloads")).Returns(true);
            disk.Setup(d => d.FolderWritable("/downloads/downloads")).Returns(false);

            var client = new BandcampDownloadClient(
                queue,
                apiClient,
                Mock.Of<IAlbumService>(),
                Mock.Of<IReleaseService>(),
                Mock.Of<ITrackService>(),
                Mock.Of<IConfigService>(),
                disk.Object,
                Mock.Of<IRemotePathMappingService>(),
                Mock.Of<ILocalizationService>(),
                logger);

            client.Definition = new DownloadClientDefinition
            {
                Name = "Bandcamp",
                Settings = new BandcampDownloadSettings
                {
                    Cookies = "identity=testcookie",
                    DownloadPath = "/downloads/downloads"
                }
            };

            var remoteAlbum = new RemoteAlbum
            {
                Release = new ReleaseInfo
                {
                    Title = "Artist - Album [FLAC]",
                    DownloadUrl = "https://bandcamp.com/download?type=album&id=1#format=flac"
                }
            };

            var ex = await Assert.ThrowsAsync<DownloadException>(
                () => client.Download(remoteAlbum, Mock.Of<IIndexer>()));

            Assert.Contains("not writable", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("/downloads/downloads", ex.Message);
            Assert.Empty(client.GetItems());
        }

        [Fact]
        public async Task Queue_FailedDownload_SetsNonEmptyErrorMessage()
        {
            var logger = LogManager.GetCurrentClassLogger();
            var registry = new BandcampDownloadRegistry();
            var proxy = new FailingProxy(new IOException(string.Empty));
            using var queue = new DownloadTaskQueue(proxy, registry, logger);

            var item = new BandcampDownloadItem
            {
                DownloadId = "abc123",
                AlbumUrl = "https://bandcamp.com/download?id=1",
                Title = "Test Album",
                OutputPath = "/downloads/downloads/album",
                Cookies = "identity=x",
                Phase = "file_download"
            };

            await queue.EnqueueAsync(item);

            // Wait until the consumer marks the item failed.
            BandcampDownloadItem? tracked = null;
            for (var i = 0; i < 50; i++)
            {
                if (registry.GetItems().TryGetValue("abc123", out tracked) &&
                    tracked.Status == BandcampDownloadStatus.Failed)
                {
                    break;
                }

                await Task.Delay(20);
            }

            Assert.NotNull(tracked);
            Assert.Equal(BandcampDownloadStatus.Failed, tracked!.Status);
            Assert.False(string.IsNullOrWhiteSpace(tracked.ErrorMessage));
            Assert.Contains("Bandcamp download failed (file_download)", tracked.ErrorMessage);
            Assert.Contains("IOException: unknown error", tracked.ErrorMessage);
        }

        private sealed class BlockingProxy : IBandcampDownloadProxy
        {
            private readonly TaskCompletionSource<bool> _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public void Release()
            {
                _gate.TrySetResult(true);
            }

            public async Task ExecuteDownloadAsync(BandcampDownloadItem item, CancellationToken cancellationToken)
            {
                item.Status = BandcampDownloadStatus.Resolving;
                item.Phase = "resolving";
                await _gate.Task.WaitAsync(cancellationToken);
                item.Status = BandcampDownloadStatus.Completed;
                item.Phase = "completed";
                item.CompletedAt = DateTime.UtcNow;
            }
        }

        private sealed class FailingProxy : IBandcampDownloadProxy
        {
            private readonly Exception _exception;

            public FailingProxy(Exception exception)
            {
                _exception = exception;
            }

            public Task ExecuteDownloadAsync(BandcampDownloadItem item, CancellationToken cancellationToken)
            {
                item.Phase = "file_download";
                throw _exception;
            }
        }
    }
}
