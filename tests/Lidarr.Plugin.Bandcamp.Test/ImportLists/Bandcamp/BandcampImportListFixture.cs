using System;
using System.Linq;
using System.Net;
using Moq;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download.Clients.Bandcamp;
using NzbDrone.Core.Http.Bandcamp;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.ImportLists.Bandcamp;
using NzbDrone.Core.Parser;
using Xunit;

namespace Lidarr.Plugin.Bandcamp.Test.ImportLists.Bandcamp
{
    public class BandcampImportListFixture
    {
        private readonly Mock<IHttpClient> _httpClientMock;
        private readonly Mock<IImportListStatusService> _status;
        private readonly BandcampImportList _subject;

        public BandcampImportListFixture()
        {
            _httpClientMock = new Mock<IHttpClient>();
            _status = new Mock<IImportListStatusService>();
            var logger = LogManager.GetCurrentClassLogger();
            var bandcampHttpClient = BandcampHttpClient.ForTests(_httpClientMock.Object, logger);
            var apiClient = new BandcampApiClient(bandcampHttpClient, logger);

            _subject = new BandcampImportList(
                apiClient,
                _status.Object,
                Mock.Of<IConfigService>(),
                Mock.Of<IParsingService>(),
                logger)
            {
                Definition = new ImportListDefinition
                {
                    Id = 9,
                    Name = "Bandcamp Collection",
                    Settings = new BandcampImportListSettings { Cookies = "testcookie" }
                }
            };
        }

        [Fact]
        public void MinRefreshInterval_IsAtLeastSixHours()
        {
            Assert.True(_subject.MinRefreshInterval >= TimeSpan.FromHours(6));
            Assert.Equal(ImportListType.Other, _subject.ListType);
        }

        [Fact]
        public void DefaultDefinitions_EnableSearchAndSpecificAlbumMonitor()
        {
            var definition = Assert.Single(_subject.DefaultDefinitions.OfType<ImportListDefinition>());

            Assert.True(definition.EnableAutomaticAdd);
            Assert.True(definition.ShouldSearch);
            Assert.Equal(ImportListMonitorType.SpecificAlbum, definition.ShouldMonitor);
            Assert.True(definition.ShouldMonitorExisting);
            Assert.Equal(nameof(BandcampImportList), definition.Implementation);
        }

        [Fact]
        public void Message_WarnsAboutMusicBrainzAndSearchOnAdd()
        {
            Assert.NotNull(_subject.Message);
            Assert.Contains("MusicBrainz", _subject.Message.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Search", _subject.Message.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("RSS", _subject.Message.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Fetch_EmitsAlbumLevelArtistAndAlbum_FromDownloadableCollection()
        {
            _httpClientMock
                .Setup(c => c.ExecuteAsync(It.IsAny<HttpRequest>()))
                .ReturnsAsync((HttpRequest request) =>
                {
                    return request.Url.FullUri switch
                    {
                        "https://bandcamp.com/api/fan/2/collection_summary" => CreateStringResponse(
                            @"{""fan_id"":12345678}",
                            request),
                        "https://bandcamp.com/api/fancollection/1/collection_items" => CreateStringResponse(
                            @"{
                                ""redownload_urls"": {
                                    ""a111"": ""https://bandcamp.com/download?type=album&id=111"",
                                    ""a222"": ""https://bandcamp.com/download?type=album&id=222""
                                },
                                ""items"": [
                                    {
                                        ""item_id"": 111,
                                        ""item_type"": ""album"",
                                        ""sale_item_type"": ""a"",
                                        ""sale_item_id"": 111,
                                        ""token"": ""1700000000:111:a:0:"",
                                        ""item_url"": ""https://fresh.bandcamp.com/album/fresh"",
                                        ""item_title"": ""Fresh"",
                                        ""band_name"": ""Fresh Band"",
                                        ""purchased"": ""15 Nov 2023 12:00:00 GMT""
                                    },
                                    {
                                        ""item_id"": 222,
                                        ""item_type"": ""album"",
                                        ""sale_item_type"": ""a"",
                                        ""sale_item_id"": 222,
                                        ""token"": ""1690000000:222:a:1:"",
                                        ""item_url"": ""https://fresh.bandcamp.com/album/fresh"",
                                        ""item_title"": ""Fresh"",
                                        ""band_name"": ""Fresh Band"",
                                        ""purchased"": ""01 Oct 2023 12:00:00 GMT""
                                    },
                                    {
                                        ""item_id"": 333,
                                        ""item_type"": ""album"",
                                        ""sale_item_type"": ""a"",
                                        ""sale_item_id"": 333,
                                        ""token"": ""1680000000:333:a:2:"",
                                        ""item_url"": ""https://other.bandcamp.com/album/no-dl"",
                                        ""item_title"": ""No Dl"",
                                        ""band_name"": ""Other""
                                    }
                                ]
                            }",
                            request),
                        _ => CreateStringResponse("{}", request)
                    };
                });

            var items = _subject.Fetch();

            var album = Assert.Single(items);
            Assert.Equal("Fresh Band", album.Artist);
            Assert.Equal("Fresh", album.Album);
            Assert.Equal(9, album.ImportListId);
            Assert.Equal(new DateTime(2023, 11, 15, 12, 0, 0, DateTimeKind.Utc), album.ReleaseDate.ToUniversalTime());
            _status.Verify(s => s.RecordSuccess(9), Times.Once);
        }

        [Fact]
        public void Fetch_AuthFailure_RecordsFailureAndReturnsEmpty()
        {
            _httpClientMock
                .Setup(c => c.ExecuteAsync(It.IsAny<HttpRequest>()))
                .ReturnsAsync((HttpRequest request) => CreateStringResponse("{}", request, HttpStatusCode.Unauthorized));

            var items = _subject.Fetch();

            Assert.Empty(items);
            _status.Verify(s => s.RecordFailure(9), Times.Once);
        }

        private static HttpResponse CreateStringResponse(string content, HttpRequest request, HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            return new HttpResponse(request, new HttpHeader(), content, statusCode);
        }
    }
}
