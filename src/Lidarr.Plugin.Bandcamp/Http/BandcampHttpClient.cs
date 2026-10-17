using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.Http;

namespace NzbDrone.Core.Http.Bandcamp
{
    /// <summary>
    /// HTTP client wrapper for Bandcamp requests with rate limiting,
    /// browser-like user-agent, cookie injection, and credential-safe logging.
    ///
    /// Production / Lidarr DI always uses <see cref="BandcampNativeHttp"/> so Bandcamp
    /// traffic never depends on <c>ManagedHttpDispatcher</c> / <c>IHttpDispatcher</c>
    /// registration order (which other plugins can disturb). This matches the console
    /// harness path that successfully downloads large FLAC archives.
    ///
    /// Unit tests that mock <see cref="IHttpClient"/> must construct via
    /// <see cref="ForTests"/>.
    /// </summary>
    public class BandcampHttpClient
    {
        private const string BrowserUserAgent = BandcampNativeHttp.BrowserUserAgent;

        private const string BrowserAccept =
            "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8";

        private static readonly TimeSpan RateLimitInterval = TimeSpan.FromSeconds(2);

        private readonly IHttpClient? _httpClient;
        private readonly BandcampNativeHttp? _nativeHttp;
        private readonly Logger _logger;

        /// <summary>
        /// Lidarr DI entry point — always native HTTP (same as the live harness).
        /// Single public constructor so DryIoc cannot pick an IHttpClient-based path.
        /// </summary>
        public BandcampHttpClient(Logger logger)
        {
            _nativeHttp = new BandcampNativeHttp(logger);
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Live smoke / explicit native injection (console harness).
        /// </summary>
        public static BandcampHttpClient ForNative(BandcampNativeHttp nativeHttp, Logger logger)
        {
            return new BandcampHttpClient(nativeHttp, logger, forNative: true);
        }

        private BandcampHttpClient(BandcampNativeHttp nativeHttp, Logger logger, bool forNative)
        {
            if (!forNative)
            {
                throw new ArgumentException("Use ForNative(...) for an injected BandcampNativeHttp.", nameof(forNative));
            }

            _nativeHttp = nativeHttp ?? throw new ArgumentNullException(nameof(nativeHttp));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        private BandcampHttpClient(IHttpClient httpClient, Logger logger, bool forTests)
        {
            if (!forTests)
            {
                throw new ArgumentException("Use ForTests(...) for the IHttpClient seam.", nameof(forTests));
            }

            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Test seam — routes through a mocked Lidarr <see cref="IHttpClient"/>.
        /// Not used by Lidarr DI (DryIoc cannot satisfy the private bool gate).
        /// </summary>
        public static BandcampHttpClient ForTests(IHttpClient httpClient, Logger logger)
        {
            return new BandcampHttpClient(httpClient, logger, forTests: true);
        }

        /// <summary>
        /// Creates an HttpRequestBuilder pre-configured for Bandcamp with browser-like
        /// headers, cookie injection, and rate limiting.
        /// </summary>
        public HttpRequestBuilder CreateRequestBuilder(string url, string cookies)
        {
            var builder = new HttpRequestBuilder(url)
            {
                RateLimit = RateLimitInterval
            };

            builder.PostProcess = req => req.RateLimitKey = "bandcamp";

            builder.Headers.Set("User-Agent", BrowserUserAgent);
            builder.Headers.Set("Accept", BrowserAccept);
            builder.Headers.Set("Accept-Language", "en-US,en;q=0.9");

            var cookieHeader = NormalizeCookieHeader(cookies);
            if (!string.IsNullOrWhiteSpace(cookieHeader))
            {
                builder.Headers.Set("Cookie", cookieHeader);
            }

            return builder;
        }

        public static string NormalizeCookieHeader(string cookies)
        {
            return BandcampNativeHttp.NormalizeIdentityCookie(cookies);
        }

        public async Task<HttpResponse> ExecuteAsync(HttpRequest request)
        {
            _logger.Debug("Bandcamp request: {0} {1}", request.Method, request.Url);

            try
            {
                var response = _nativeHttp != null
                    ? await ExecuteViaNativeAsync(request).ConfigureAwait(false)
                    : await _httpClient!.ExecuteAsync(request).ConfigureAwait(false);

                _logger.Debug("Bandcamp response: {0} -> {1} ({2} bytes)",
                    request.Url,
                    (int)response.StatusCode,
                    response.Content?.Length ?? response.ResponseData?.Length ?? 0);

                return response;
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Bandcamp request failed: {0} {1}", request.Method, request.Url);
                throw;
            }
        }

        public Task<HttpResponse> ExecuteRawAsync(HttpRequest request)
        {
            return ExecuteAsync(request);
        }

        /// <summary>
        /// Streams a Bandcamp CDN / statdownload URL straight to disk (avoids buffering
        /// ~400MB+ FLAC zips in RAM — important in memory-constrained Docker).
        /// </summary>
        public Task DownloadToFileAsync(
            string url,
            string cookies,
            string filePath,
            CancellationToken cancellationToken = default,
            Action<long, long?>? progress = null)
        {
            if (_nativeHttp != null)
            {
                return _nativeHttp.DownloadToFileAsync(url, cookies, filePath, cancellationToken, progress);
            }

            // Test seam: buffer via IHttpClient then write (mocks return small payloads).
            return DownloadToFileViaLidarrHttpAsync(url, cookies, filePath, cancellationToken, progress);
        }

        private async Task DownloadToFileViaLidarrHttpAsync(
            string url,
            string cookies,
            string filePath,
            CancellationToken cancellationToken,
            Action<long, long?>? progress)
        {
            var builder = CreateRequestBuilder(url, cookies);
            var request = builder.Build();
            var response = await _httpClient!.ExecuteAsync(request).ConfigureAwait(false);
            var data = response.ResponseData;
            if (data == null || data.Length == 0)
            {
                throw new InvalidOperationException("Download response contained no data.");
            }

            progress?.Invoke(0, data.Length);
            await File.WriteAllBytesAsync(filePath, data, cancellationToken).ConfigureAwait(false);
            progress?.Invoke(data.Length, data.Length);
        }

        private async Task<HttpResponse> ExecuteViaNativeAsync(HttpRequest request)
        {
            var cookies = request.Headers["Cookie"] ?? string.Empty;
            var url = request.Url.FullUri;

            BandcampHttpResult result;
            if (request.Method == System.Net.Http.HttpMethod.Post)
            {
                var body = request.ContentData != null
                    ? Encoding.UTF8.GetString(request.ContentData)
                    : string.Empty;
                result = await _nativeHttp!.PostJsonAsync(url, cookies, body).ConfigureAwait(false);
            }
            else
            {
                // Always fetch bytes so ZIP responses remain usable; HttpResponse
                // exposes both ResponseData and Content when constructed from bytes.
                result = await _nativeHttp!.GetBytesAsync(url, cookies).ConfigureAwait(false);
            }

            var status = (HttpStatusCode)result.StatusCode;
            if (result.RawBytes != null)
            {
                return new HttpResponse(request, new HttpHeader(), result.RawBytes, status);
            }

            return new HttpResponse(request, new HttpHeader(), result.Content ?? string.Empty, status);
        }
    }
}
