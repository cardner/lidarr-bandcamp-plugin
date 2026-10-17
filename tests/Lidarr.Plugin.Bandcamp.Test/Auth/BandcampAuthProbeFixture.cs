using System.Net;
using System.Threading.Tasks;
using Moq;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Auth.Bandcamp;
using NzbDrone.Core.Download.Clients.Bandcamp;
using NzbDrone.Core.Http.Bandcamp;
using Xunit;

namespace Lidarr.Plugin.Bandcamp.Test.Auth
{
    public class BandcampAuthProbeFixture
    {
        private readonly Mock<IHttpClient> _http = new();
        private readonly BandcampApiClient _client;

        public BandcampAuthProbeFixture()
        {
            var logger = LogManager.GetCurrentClassLogger();
            _client = new BandcampApiClient(BandcampHttpClient.ForTests(_http.Object, logger), logger);
        }

        private static HttpResponse StringResponse(string content, HttpStatusCode status = HttpStatusCode.OK)
        {
            return new HttpResponse(new HttpRequest("https://bandcamp.com"), new HttpHeader(), content, status);
        }

        [Fact]
        public async Task ProbeAuthAsync_InvalidShape_DoesNotCallNetwork()
        {
            var result = await _client.ProbeAuthAsync("xxx");
            Assert.False(result.Success);
            Assert.Equal(BandcampAuthFailureKind.InvalidShape, result.FailureKind);
            _http.Verify(c => c.ExecuteAsync(It.IsAny<HttpRequest>()), Times.Never);
        }

        [Fact]
        public async Task ProbeAuthAsync_CollectionSummaryFanId_Succeeds()
        {
            _http.Setup(c => c.ExecuteAsync(It.Is<HttpRequest>(r =>
                    r.Url.ToString().Contains("/api/fan/2/collection_summary"))))
                .ReturnsAsync(StringResponse("{\"fan_id\":99}"));

            var result = await _client.ProbeAuthAsync("good-cookie-value-1");

            Assert.True(result.Success);
            Assert.Equal(99, result.FanId);
            Assert.Equal("identity=good-cookie-value-1", result.NormalizedCookies);
        }

        [Fact]
        public async Task ProbeAuthAsync_Unauthorized_ReturnsHttpUnauthorized()
        {
            _http.Setup(c => c.ExecuteAsync(It.IsAny<HttpRequest>()))
                .ReturnsAsync(StringResponse("nope", HttpStatusCode.Unauthorized));

            var result = await _client.ProbeAuthAsync("expired-looking-cookie");

            Assert.False(result.Success);
            Assert.Equal(BandcampAuthFailureKind.HttpUnauthorized, result.FailureKind);
            Assert.Contains("401", result.Message);
            Assert.Contains("Re-export", result.Message);
        }

        [Fact]
        public async Task ProbeAuthAsync_ChallengePage_ReturnsChallengeKind()
        {
            _http.Setup(c => c.ExecuteAsync(It.IsAny<HttpRequest>()))
                .ReturnsAsync(StringResponse("<html>Just a moment... cf-challenge</html>"));

            var result = await _client.ProbeAuthAsync("challenge-cookie-xx");

            Assert.False(result.Success);
            Assert.Equal(BandcampAuthFailureKind.ChallengePage, result.FailureKind);
            Assert.Contains("challenge", result.Message, System.StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task ProbeAuthAsync_NullFanIdOnHomepage_ReturnsExpired()
        {
            _http.Setup(c => c.ExecuteAsync(It.Is<HttpRequest>(r =>
                    r.Url.ToString().Contains("collection_summary"))))
                .ReturnsAsync(StringResponse("{}"));

            var html = @"<div data-blob=""{&quot;pageContext&quot;:{&quot;identity&quot;:{&quot;fanId&quot;:null}}}""></div>";
            _http.Setup(c => c.ExecuteAsync(It.Is<HttpRequest>(r =>
                    !r.Url.ToString().Contains("collection_summary"))))
                .ReturnsAsync(StringResponse(html));

            var result = await _client.ProbeAuthAsync("stale-cookie-value");

            Assert.False(result.Success);
            Assert.Equal(BandcampAuthFailureKind.ExpiredOrRejected, result.FailureKind);
            Assert.Contains("Re-export", result.Message);
        }
    }
}
