using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Core.Http.Bandcamp;
using Xunit;

namespace Lidarr.Plugin.Bandcamp.Test.Http
{
    public class BandcampNativeHttpFixture
    {
        [Theory]
        [InlineData("abc123", "identity=abc123")]
        [InlineData("identity=abc123", "identity=abc123")]
        [InlineData("identity=abc123; js=1", "identity=abc123; js=1")]
        [InlineData("  bare-value  ", "identity=bare-value")]
        [InlineData("", "")]
        [InlineData(null, "")]
        public void NormalizeIdentityCookie_AcceptsBareOrFullCookie(string? input, string expected)
        {
            Assert.Equal(expected, BandcampNativeHttp.NormalizeIdentityCookie(input));
        }

        [Fact]
        public async Task GetAsync_SetsBrowserUserAgent_AndIdentityCookie()
        {
            HttpRequestMessage? seen = null;
            var handler = new CaptureHandler((request, _) =>
            {
                seen = request;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"fan_id\":42}")
                };
            });

            using var http = new HttpClient(handler);
            using var client = BandcampNativeHttp.ForTests(http, LogManager.GetCurrentClassLogger());

            var result = await client.GetAsync("https://bandcamp.com/api/fan/2/collection_summary", "raw-cookie-value");

            Assert.Equal(200, result.StatusCode);
            Assert.Equal("{\"fan_id\":42}", result.Content);
            Assert.NotNull(seen);
            Assert.True(seen!.Headers.TryGetValues("Cookie", out var cookies));
            Assert.Equal("identity=raw-cookie-value", string.Join("; ", cookies));
        }

        [Fact]
        public void CreateDefaultClient_UsesBrowserUserAgent()
        {
            using var client = BandcampNativeHttp.CreateDefaultClient();
            Assert.Contains("Mozilla/5.0", client.DefaultRequestHeaders.UserAgent.ToString());
            Assert.DoesNotContain("Lidarr", client.DefaultRequestHeaders.UserAgent.ToString());
        }

        [Fact]
        public async Task DownloadToFileAsync_ReportsProgressWithContentLength()
        {
            var payload = new byte[300_000];
            new Random(1).NextBytes(payload);

            var handler = new CaptureHandler((_, _) =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(payload)
                };
                response.Content.Headers.ContentType =
                    new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip");
                response.Content.Headers.ContentLength = payload.Length;
                return response;
            });

            using var http = new HttpClient(handler);
            using var client = BandcampNativeHttp.ForTests(http, LogManager.GetCurrentClassLogger());

            var tempFile = Path.Combine(Path.GetTempPath(), $"bc-progress-{Guid.NewGuid():N}.bin");
            var reports = new System.Collections.Generic.List<(long Downloaded, long? Total)>();

            try
            {
                await client.DownloadToFileAsync(
                    "https://bandcamp.com/download/file",
                    "identity=test",
                    tempFile,
                    CancellationToken.None,
                    (downloaded, total) => reports.Add((downloaded, total)));

                Assert.True(reports.Count >= 2);
                Assert.Equal(payload.Length, reports[0].Total);
                Assert.Equal(0, reports[0].Downloaded);
                Assert.Equal(payload.Length, reports[^1].Downloaded);
                Assert.Equal(payload.Length, new FileInfo(tempFile).Length);
            }
            finally
            {
                if (File.Exists(tempFile))
                {
                    File.Delete(tempFile);
                }
            }
        }

        private sealed class CaptureHandler : HttpMessageHandler
        {
            private readonly System.Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _handler;

            public CaptureHandler(System.Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> handler)
            {
                _handler = handler;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return Task.FromResult(_handler(request, cancellationToken));
            }
        }
    }
}
