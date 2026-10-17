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

        [Fact]
        public async Task ProbeOrStreamStatdownloadAsync_JsonUrl_ReturnsTextWithoutWritingFile()
        {
            var json = @"{""url"":""https://pop.s3.amazonaws.com/bandcamp/album.zip?sig=1""}";
            var requestCount = 0;

            var handler = new CaptureHandler((_, _) =>
            {
                requestCount++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
                };
            });

            using var http = new HttpClient(handler);
            using var client = BandcampNativeHttp.ForTests(http, LogManager.GetCurrentClassLogger());
            var archivePath = Path.Combine(Path.GetTempPath(), $"bc-probe-{Guid.NewGuid():N}.zip");

            try
            {
                var result = await client.ProbeOrStreamStatdownloadAsync(
                    "https://popplers5.bandcamp.com/download/album?enc=flac&.format=flac&.json=true",
                    "identity=test",
                    archivePath);

                Assert.Equal(1, requestCount);
                Assert.False(result.StreamedToFile);
                Assert.Contains("s3.amazonaws.com", result.Content);
                Assert.False(File.Exists(archivePath));
            }
            finally
            {
                if (File.Exists(archivePath))
                {
                    File.Delete(archivePath);
                }
            }
        }

        [Fact]
        public async Task ProbeOrStreamStatdownloadAsync_ZipBody_StreamsFirstResponseOnce()
        {
            // Large-ish payload to ensure we never round-trip via string Content.
            var payload = new byte[512 * 1024];
            payload[0] = 0x50;
            payload[1] = 0x4B;
            payload[2] = 0x03;
            payload[3] = 0x04;
            new Random(42).NextBytes(payload.AsSpan(4));

            var requestCount = 0;
            var handler = new CaptureHandler((_, _) =>
            {
                requestCount++;
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(payload)
                };
                response.Content.Headers.ContentType =
                    new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                response.Content.Headers.ContentLength = payload.Length;
                return response;
            });

            using var http = new HttpClient(handler);
            using var client = BandcampNativeHttp.ForTests(http, LogManager.GetCurrentClassLogger());
            var archivePath = Path.Combine(Path.GetTempPath(), $"bc-probe-{Guid.NewGuid():N}.zip");
            var reports = new System.Collections.Generic.List<(long Downloaded, long? Total)>();

            try
            {
                var result = await client.ProbeOrStreamStatdownloadAsync(
                    "https://popplers5.bandcamp.com/download/album?enc=flac&id=1&sig=abc&.format=flac&.json=true",
                    "identity=test",
                    archivePath,
                    CancellationToken.None,
                    (downloaded, total) => reports.Add((downloaded, total)));

                Assert.Equal(1, requestCount); // one-shot — never probe-and-re-GET
                Assert.True(result.StreamedToFile);
                Assert.Null(result.Content);
                Assert.Equal(payload.Length, new FileInfo(archivePath).Length);
                Assert.Equal(payload, File.ReadAllBytes(archivePath));
                Assert.True(reports.Count >= 2);
                Assert.Equal(payload.Length, reports[^1].Downloaded);
            }
            finally
            {
                if (File.Exists(archivePath))
                {
                    File.Delete(archivePath);
                }
            }
        }

        [Fact]
        public async Task ProbeOrStreamStatdownloadAsync_ZipMagicWithoutContentType_StreamsSameResponse()
        {
            var payload = new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x0A, 0x0B, 0x0C, 0x0D };
            var requestCount = 0;

            var handler = new CaptureHandler((_, _) =>
            {
                requestCount++;
                // No Content-Type — detect via ZIP local-file magic on the peek.
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(payload)
                };
            });

            using var http = new HttpClient(handler);
            using var client = BandcampNativeHttp.ForTests(http, LogManager.GetCurrentClassLogger());
            var archivePath = Path.Combine(Path.GetTempPath(), $"bc-probe-{Guid.NewGuid():N}.zip");

            try
            {
                var result = await client.ProbeOrStreamStatdownloadAsync(
                    "https://bandcamp.com/statdownload/album/1?sig=abc&.format=flac&.json=true",
                    "identity=test",
                    archivePath);

                Assert.Equal(1, requestCount);
                Assert.True(result.StreamedToFile);
                Assert.Equal(payload, File.ReadAllBytes(archivePath));
            }
            finally
            {
                if (File.Exists(archivePath))
                {
                    File.Delete(archivePath);
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
