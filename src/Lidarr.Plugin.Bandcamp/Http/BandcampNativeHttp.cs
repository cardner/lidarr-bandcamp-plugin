using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NLog;

namespace NzbDrone.Core.Http.Bandcamp
{
    /// <summary>
    /// Bandcamp-facing HTTP using <see cref="System.Net.Http.HttpClient"/> directly.
    ///
    /// Lidarr's <c>ManagedHttpDispatcher</c> throws
    /// <c>NotSupportedException("User-Agent other than Lidarr not allowed.")</c>
    /// when any request sets a custom User-Agent. Bandcamp pages and fan APIs
    /// expect a browser-like UA, so Bandcamp traffic must not go through that
    /// dispatcher. This client owns its own handler and never touches Lidarr's
    /// <c>IHttpClient</c> for outbound Bandcamp calls.
    /// </summary>
    public sealed class BandcampNativeHttp : IDisposable
    {
        public const string BrowserUserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

        private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(2);

        private readonly HttpClient _httpClient;
        private readonly Logger _logger;
        private readonly object _rateGate = new();
        private DateTime _nextAllowedUtc = DateTime.MinValue;

        public BandcampNativeHttp(Logger logger)
            : this(CreateDefaultClient(), logger)
        {
        }

        /// <summary>
        /// Test seam — inject a custom <see cref="HttpClient"/> (e.g. with a handler).
        /// </summary>
        public BandcampNativeHttp(HttpClient httpClient, Logger logger)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public static HttpClient CreateDefaultClient()
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.All,
                UseCookies = false
            };

            var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromMinutes(5)
            };

            client.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserUserAgent);
            client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");

            return client;
        }

        public static string NormalizeIdentityCookie(string? cookies)
        {
            if (string.IsNullOrWhiteSpace(cookies))
            {
                return string.Empty;
            }

            var cookieHeader = cookies.Trim();
            return cookieHeader.Contains('=') ? cookieHeader : $"identity={cookieHeader}";
        }

        public async Task<BandcampHttpResult> GetAsync(string url, string cookies, CancellationToken cancellationToken = default)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            ApplyCookie(request, cookies);
            return await SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        public async Task<BandcampHttpResult> PostJsonAsync(string url, string cookies, string jsonBody, CancellationToken cancellationToken = default)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json")
            };
            ApplyCookie(request, cookies);
            return await SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        public async Task<BandcampHttpResult> GetBytesAsync(string url, string cookies, CancellationToken cancellationToken = default)
        {
            await WaitForRateLimitAsync(cancellationToken).ConfigureAwait(false);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            ApplyCookie(request, cookies);

            _logger.Debug("Bandcamp native request: GET {0}", url);

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            _logger.Debug("Bandcamp native response: {0} -> {1} ({2} bytes)",
                url, (int)response.StatusCode, bytes.Length);

            return new BandcampHttpResult
            {
                StatusCode = (int)response.StatusCode,
                Content = null,
                RawBytes = bytes,
                ContentType = response.Content.Headers.ContentType?.ToString()
            };
        }

        private async Task<BandcampHttpResult> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await WaitForRateLimitAsync(cancellationToken).ConfigureAwait(false);

            _logger.Debug("Bandcamp native request: {0} {1}", request.Method, request.RequestUri);

            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            _logger.Debug("Bandcamp native response: {0} -> {1} ({2} bytes)",
                request.RequestUri,
                (int)response.StatusCode,
                content.Length);

            return new BandcampHttpResult
            {
                StatusCode = (int)response.StatusCode,
                Content = content,
                RawBytes = null,
                ContentType = response.Content.Headers.ContentType?.ToString()
            };
        }

        private static void ApplyCookie(HttpRequestMessage request, string cookies)
        {
            var cookieHeader = NormalizeIdentityCookie(cookies);
            if (!string.IsNullOrWhiteSpace(cookieHeader))
            {
                request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
            }
        }

        private async Task WaitForRateLimitAsync(CancellationToken cancellationToken)
        {
            TimeSpan delay;
            lock (_rateGate)
            {
                var now = DateTime.UtcNow;
                if (now < _nextAllowedUtc)
                {
                    delay = _nextAllowedUtc - now;
                }
                else
                {
                    delay = TimeSpan.Zero;
                }

                _nextAllowedUtc = DateTime.UtcNow + MinInterval;
            }

            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }

        public void Dispose()
        {
            _httpClient.Dispose();
        }
    }

    public sealed class BandcampHttpResult
    {
        public int StatusCode { get; set; }
        public string? Content { get; set; }
        public byte[]? RawBytes { get; set; }
        public string? ContentType { get; set; }
    }
}
