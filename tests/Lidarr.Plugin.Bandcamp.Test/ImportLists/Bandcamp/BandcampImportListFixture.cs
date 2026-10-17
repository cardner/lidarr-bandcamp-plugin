using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Moq;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Download.Clients.Bandcamp;
using NzbDrone.Core.Http.Bandcamp;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.ImportLists.Bandcamp;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser;
using Xunit;

namespace Lidarr.Plugin.Bandcamp.Test.ImportLists.Bandcamp
{
    public class BandcampImportListFixture
    {
        private readonly Mock<IHttpClient> _httpClientMock;
        private readonly Mock<IImportListStatusService> _status;
        private readonly Mock<ISearchForNewAlbum> _albumSearch;
        private readonly BandcampImportList _subject;

        public BandcampImportListFixture()
        {
            _httpClientMock = new Mock<IHttpClient>();
            _status = new Mock<IImportListStatusService>();
            _albumSearch = new Mock<ISearchForNewAlbum>();
            var logger = LogManager.GetCurrentClassLogger();
            var bandcampHttpClient = BandcampHttpClient.ForTests(_httpClientMock.Object, logger);
            var apiClient = new BandcampApiClient(bandcampHttpClient, logger);

            _subject = new BandcampImportList(
                apiClient,
                _albumSearch.Object,
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
        public void Fetch_EmitsMappedMbids_AndSkipsSkyHookFailures()
        {
            SetupCollectionAuthAndItems();

            _albumSearch
                .Setup(s => s.SearchForNewAlbum("Fresh", "Fresh Band"))
                .Returns(new List<Album>
                {
                    CreateMappedAlbum(
                        "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
                        "Fresh",
                        "11111111-2222-3333-4444-555555555555",
                        "Fresh Band")
                });

            _albumSearch
                .Setup(s => s.SearchForNewAlbum("Boomtown", "Boomtown"))
                .Throws(new Exception("SkyHook 500 Unable to communicate with LidarrAPI"));

            var items = _subject.Fetch();

            var album = Assert.Single(items);
            Assert.Equal("Fresh Band", album.Artist);
            Assert.Equal("Fresh", album.Album);
            Assert.Equal("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", album.AlbumMusicBrainzId);
            Assert.Equal("11111111-2222-3333-4444-555555555555", album.ArtistMusicBrainzId);
            Assert.Equal(9, album.ImportListId);
            _status.Verify(s => s.RecordSuccess(9), Times.Once);
            _albumSearch.Verify(s => s.SearchForNewAlbum(It.IsAny<string>(), It.IsAny<string>()), Times.Exactly(2));
        }

        [Fact]
        public void Fetch_NormalizesArtistPrefixedBandcampTitles()
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
                                    ""a111"": ""https://bandcamp.com/download?type=album&id=111""
                                },
                                ""items"": [
                                    {
                                        ""item_id"": 111,
                                        ""item_type"": ""album"",
                                        ""sale_item_type"": ""a"",
                                        ""sale_item_id"": 111,
                                        ""token"": ""1700000000:111:a:0:"",
                                        ""item_url"": ""https://artist.bandcamp.com/album/album-title"",
                                        ""item_title"": ""Artist Name - Album Title - Single"",
                                        ""band_name"": ""Artist Name""
                                    }
                                ]
                            }",
                            request),
                        _ => CreateStringResponse("{}", request)
                    };
                });

            _albumSearch
                .Setup(s => s.SearchForNewAlbum("Album Title", "Artist Name"))
                .Returns(new List<Album>
                {
                    CreateMappedAlbum(
                        "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
                        "Album Title",
                        "11111111-2222-3333-4444-555555555555",
                        "Artist Name")
                });

            var items = _subject.Fetch();

            Assert.Single(items);
            _albumSearch.Verify(s => s.SearchForNewAlbum("Album Title", "Artist Name"), Times.Once);
            _albumSearch.Verify(s => s.SearchForNewAlbum("Artist Name - Album Title - Single", It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public void Fetch_SkipsTracks_PreferringAlbums()
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
                                    ""t1"": ""https://bandcamp.com/download?type=track&id=1""
                                },
                                ""items"": [
                                    {
                                        ""item_id"": 1,
                                        ""item_type"": ""track"",
                                        ""sale_item_type"": ""t"",
                                        ""sale_item_id"": 1,
                                        ""token"": ""1700000000:1:t:0:"",
                                        ""item_url"": ""https://artist.bandcamp.com/track/only"",
                                        ""item_title"": ""Only Track"",
                                        ""band_name"": ""Artist Name""
                                    }
                                ]
                            }",
                            request),
                        _ => CreateStringResponse("{}", request)
                    };
                });

            var items = _subject.Fetch();

            Assert.Empty(items);
            _albumSearch.Verify(s => s.SearchForNewAlbum(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
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

        private void SetupCollectionAuthAndItems()
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
                                        ""item_url"": ""https://boom.bandcamp.com/album/boomtown"",
                                        ""item_title"": ""Boomtown"",
                                        ""band_name"": ""Boomtown"",
                                        ""purchased"": ""01 Oct 2023 12:00:00 GMT""
                                    }
                                ]
                            }",
                            request),
                        _ => CreateStringResponse("{}", request)
                    };
                });
        }

        private static Album CreateMappedAlbum(string albumMbId, string albumTitle, string artistMbId, string artistName)
        {
            var metadata = new ArtistMetadata
            {
                ForeignArtistId = artistMbId,
                Name = artistName
            };

            return new Album
            {
                ForeignAlbumId = albumMbId,
                Title = albumTitle,
                ArtistMetadata = new LazyLoaded<ArtistMetadata>(metadata),
                Artist = new LazyLoaded<Artist>(new Artist
                {
                    Metadata = new LazyLoaded<ArtistMetadata>(metadata)
                })
            };
        }

        private static HttpResponse CreateStringResponse(string content, HttpRequest request, HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            return new HttpResponse(request, new HttpHeader(), content, statusCode);
        }
    }
}
