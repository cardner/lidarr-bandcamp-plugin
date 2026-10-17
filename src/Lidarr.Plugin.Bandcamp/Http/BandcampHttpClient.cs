using System;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.Http;

namespace NzbDrone.Core.Http.Bandcamp
{
    /// <summary>
    /// HTTP client wrapper for Bandcamp requests with rate limiting,
    /// browser-like user-agent, cookie injection, and credential-safe logging.
    ///
    /// Custom User-Agent is intentional: Bandcamp serves full pages / fan APIs
    /// to browser-like clients. Lidarr's default <c>ManagedHttpDispatcher</c>
    /// rejects non-Lidarr User-Agents, so this plugin also registers
    /// <see cref="BandcampHttpDispatcher"/> and provides
    /// <see cref="BandcampNativeHttp"/> for calls that must never touch Lidarr's
    /// dispatcher.
    ///
    /// Construct with <see cref="BandcampNativeHttp"/> (live harness / preferred)
    /// to bypass Lidarr's dispatcher entirely. Construct with <see cref="IHttpClient"/>
    /// for in-host DI when <see cref="BandcampHttpDispatcher"/> is registered.
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

        public BandcampHttpClient(IHttpClient httpClient, Logger logger)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Preferred for live smoke / any path that must not set User-Agent on Lidarr's dispatcher.
        /// </summary>
        public BandcampHttpClient(BandcampNativeHttp nativeHttp, Logger logger)
        {
            _nativeHttp = nativeHttp ?? throw new ArgumentNullException(nameof(nativeHttp));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
                _logger.Debug(ex, "Bandcamp request failed: {0}", request.Url);
                throw;
            }
        }

        public Task<HttpResponse> ExecuteRawAsync(HttpRequest request)
        {
            return ExecuteAsync(request);
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
