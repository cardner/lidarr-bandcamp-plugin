using System;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Core.Download.Clients.Bandcamp;
using NzbDrone.Core.Http.Bandcamp;
using Xunit;

namespace Lidarr.Plugin.Bandcamp.Test.Live
{
    /// <summary>
    /// Optional live smoke against Bandcamp. Skips unless
    /// <c>BANDCAMP_IDENTITY_COOKIE</c> (or legacy <c>BANDCAMP_COOKIES</c>) is set.
    /// Never enable these secrets in CI.
    /// </summary>
    public class BandcampLiveSmokeTests
    {
        private static string? ResolveCookie()
        {
            var value = Environment.GetEnvironmentVariable("BANDCAMP_IDENTITY_COOKIE")
                        ?? Environment.GetEnvironmentVariable("BANDCAMP_COOKIES");
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        [Fact]
        public async Task Live_Authenticate_And_ListCollection()
        {
            var cookie = ResolveCookie();
            if (cookie == null)
            {
                // Intentional skip — do not fail CI when Ryan has not supplied a cookie.
                return;
            }

            var logger = LogManager.GetCurrentClassLogger();
            using var native = new BandcampNativeHttp(logger);
            var api = new BandcampApiClient(BandcampHttpClient.ForNative(native, logger), logger);

            var probe = await api.ProbeAuthAsync(cookie);
            Assert.True(probe.Success, probe.Message);
            Assert.NotNull(probe.FanId);

            var items = await api.GetDownloadableCollectionAsync(
                probe.NormalizedCookies, probe.FanId!.Value, maxPages: 1);

            Assert.NotNull(items);
            // Empty collection is allowed (auth still succeeded); just ensure the call works.
        }
    }
}
