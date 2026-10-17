using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Core.ImportLists.Bandcamp;
using Xunit;

namespace Lidarr.Plugin.Bandcamp.Test.ImportLists.Bandcamp
{
    public class BandcampSkyHookAlbumSearchFixture
    {
        [Fact]
        public void FindAlbum_ParsesFirstMatchWithArtistAndAlbumIds()
        {
            var handler = new StubHandler((request) =>
            {
                Assert.Contains("/search?", request.RequestUri!.AbsoluteUri);
                Assert.Contains("type=album", request.RequestUri.Query);
                Assert.Contains("query=fresh", request.RequestUri.Query);
                Assert.Contains("artist=fresh%20band", request.RequestUri.Query);

                var json = @"[
                  {
                    ""id"": ""aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"",
                    ""title"": ""Fresh"",
                    ""artistId"": ""11111111-2222-3333-4444-555555555555"",
                    ""artists"": [
                      { ""id"": ""11111111-2222-3333-4444-555555555555"", ""artistName"": ""Fresh Band"" }
                    ]
                  }
                ]";

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json)
                };
            });

            using var http = new HttpClient(handler);
            using var subject = BandcampSkyHookAlbumSearch.ForTests(http, LogManager.GetCurrentClassLogger());

            var match = subject.FindAlbum("Fresh", "Fresh Band", null);

            Assert.NotNull(match);
            Assert.Equal("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", match!.AlbumMusicBrainzId);
            Assert.Equal("11111111-2222-3333-4444-555555555555", match.ArtistMusicBrainzId);
            Assert.Equal("Fresh", match.AlbumTitle);
            Assert.Equal("Fresh Band", match.ArtistName);
        }

        [Fact]
        public void FindAlbum_Http500_ThrowsSoftFailException()
        {
            var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("boom")
            });

            using var http = new HttpClient(handler);
            using var subject = BandcampSkyHookAlbumSearch.ForTests(http, LogManager.GetCurrentClassLogger());

            var ex = Assert.Throws<BandcampSkyHookHttpException>(() => subject.FindAlbum("Boomtown", "Boomtown", null));
            Assert.Equal(500, ex.StatusCode);
        }

        [Fact]
        public void FindAlbum_EmptyResults_ReturnsNull()
        {
            var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]")
            });

            using var http = new HttpClient(handler);
            using var subject = BandcampSkyHookAlbumSearch.ForTests(http, LogManager.GetCurrentClassLogger());

            Assert.Null(subject.FindAlbum("Unknown", "Nobody", null));
        }

        [Fact]
        public void FindAlbum_UsesCustomMetadataBaseUrl()
        {
            Uri? seen = null;
            var handler = new StubHandler(request =>
            {
                seen = request.RequestUri;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("[]")
                };
            });

            using var http = new HttpClient(handler);
            using var subject = BandcampSkyHookAlbumSearch.ForTests(http, LogManager.GetCurrentClassLogger());

            subject.FindAlbum("X", "Y", "http://metadata.local/api/v1");

            Assert.NotNull(seen);
            Assert.StartsWith("http://metadata.local/api/v1/search?", seen!.AbsoluteUri, StringComparison.Ordinal);
        }

        private sealed class StubHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

            public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
            {
                _responder = responder;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return Task.FromResult(_responder(request));
            }
        }
    }
}
