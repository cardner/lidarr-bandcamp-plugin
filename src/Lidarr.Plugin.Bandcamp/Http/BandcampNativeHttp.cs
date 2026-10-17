using System;
using System.IO;
using System.Net.Http;
using System.Text;
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
        private readonly bool _ownsClient;
        private readonly object _rateGate = new();
        private DateTime _nextAllowedUtc = DateTime.MinValue;

        public BandcampNativeHttp(Logger logger)
            : this(CreateDefaultClient(), logger, ownsClient: true)
        {
        }

        private BandcampNativeHttp(HttpClient httpClient, Logger logger, bool ownsClient)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _ownsClient = ownsClient;
        }

        /// <summary>
        /// Test seam — inject a custom <see cref="HttpClient"/> (e.g. with a handler).
        /// Not used by Lidarr DI (avoids DryIoc auto-creating a bare HttpClient without browser UA).
        /// </summary>
        public static BandcampNativeHttp ForTests(HttpClient httpClient, Logger logger)
        {
            return new BandcampNativeHttp(httpClient, logger, ownsClient: false);
        }

        public static HttpClient CreateDefaultClient()
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.All,
                UseCookies = false
            };

            // Large FLAC album zips often exceed 400MB; allow slow Docker/NAS links.
            var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromMinutes(30)
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

        /// <summary>
        /// Streams response body to <paramref name="filePath"/> without buffering the
        /// full archive in memory (critical for ~400MB+ Bandcamp FLAC zips in Docker).
        /// Invokes <paramref name="progress"/> with (downloadedBytes, contentLengthOrNull)
        /// as chunks arrive so Lidarr Activity can show a live progress bar.
        /// </summary>
        public async Task DownloadToFileAsync(
            string url,
            string cookies,
            string filePath,
            CancellationToken cancellationToken = default,
            Action<long, long?>? progress = null)
        {
            await WaitForRateLimitAsync(cancellationToken).ConfigureAwait(false);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            ApplyCookie(request, cookies);

            _logger.Debug("Bandcamp native download: GET {0} -> {1}", url, filePath);

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            response.EnsureSuccessStatusCode();

            var contentType = response.Content.Headers.ContentType?.ToString() ?? string.Empty;
            if (contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Received HTML response instead of expected audio archive. " +
                    "The download URL may have expired or the session cookies may be invalid.");
            }

            long? contentLength = response.Content.Headers.ContentLength;
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await StreamToFileAsync(source, filePath, contentLength, progress, peek: null, peekLength: 0, cancellationToken)
                .ConfigureAwait(false);

            _logger.Debug("Bandcamp native download complete: {0} -> {1}", url, filePath);
        }

        /// <summary>
        /// One-shot GET for Bandcamp <c>statdownload</c> / popplers URLs that may return
        /// either a tiny JSON redirect or the archive bytes themselves.
        ///
        /// Uses <see cref="HttpCompletionOption.ResponseHeadersRead"/>, peeks the body,
        /// and either streams <b>this same response</b> to <paramref name="archivePath"/>
        /// (never re-GET — URLs are often single-use) or returns small text for JSON parsing.
        /// Never materializes a multi-hundred-MB body via <c>ReadAsByteArrayAsync</c> / string decode.
        /// </summary>
        public async Task<BandcampStatdownloadProbeResult> ProbeOrStreamStatdownloadAsync(
            string url,
            string cookies,
            string archivePath,
            CancellationToken cancellationToken = default,
            Action<long, long?>? progress = null)
        {
            await WaitForRateLimitAsync(cancellationToken).ConfigureAwait(false);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            ApplyCookie(request, cookies);

            _logger.Debug("Bandcamp native statdownload probe: GET {0}", url);

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            response.EnsureSuccessStatusCode();

            var contentType = response.Content.Headers.ContentType?.ToString() ?? string.Empty;
            long? contentLength = response.Content.Headers.ContentLength;

            if (contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Received HTML response instead of expected Bandcamp download payload. " +
                    "The download URL may have expired or the session cookies may be invalid.");
            }

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            // Prefer Content-Type when Bandcamp already declares a binary archive.
            if (LooksLikeBinaryArchiveContentType(contentType) ||
                (contentLength is > MaxStatdownloadTextBytes))
            {
                _logger.Debug(
                    "Bandcamp native statdownload: streaming binary response to {0} (Content-Type={1}, Content-Length={2})",
                    archivePath,
                    contentType,
                    contentLength?.ToString() ?? "unknown");

                await StreamToFileAsync(source, archivePath, contentLength, progress, peek: null, peekLength: 0, cancellationToken)
                    .ConfigureAwait(false);

                return new BandcampStatdownloadProbeResult
                {
                    StatusCode = (int)response.StatusCode,
                    StreamedToFile = true,
                    ContentType = contentType
                };
            }

            // Peek enough to detect ZIP magic or read a small JSON/URL body — without
            // buffering a full FLAC zip if Content-Type/Length lied.
            var peek = new byte[StatdownloadPeekBytes];
            var peekLength = await ReadAtMostAsync(source, peek, cancellationToken).ConfigureAwait(false);

            if (peekLength >= 4 && IsZipLocalFileHeader(peek))
            {
                _logger.Debug("Bandcamp native statdownload: ZIP magic on first response — streaming to {0}", archivePath);

                await StreamToFileAsync(source, archivePath, contentLength, progress, peek, peekLength, cancellationToken)
                    .ConfigureAwait(false);

                return new BandcampStatdownloadProbeResult
                {
                    StatusCode = (int)response.StatusCode,
                    StreamedToFile = true,
                    ContentType = contentType
                };
            }

            // Small JSON / plain URL — assemble text with a hard size cap.
            using var textBuffer = new MemoryStream(Math.Max(peekLength, 256));
            if (peekLength > 0)
            {
                textBuffer.Write(peek, 0, peekLength);
            }

            var copyBuf = new byte[8192];
            while (textBuffer.Length < MaxStatdownloadTextBytes)
            {
                var toRead = (int)Math.Min(copyBuf.Length, MaxStatdownloadTextBytes - textBuffer.Length);
                var read = await source.ReadAsync(copyBuf.AsMemory(0, toRead), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                textBuffer.Write(copyBuf, 0, read);
            }

            // If the body keeps going past the text cap, treat remaining as archive on this
            // same connection (one-shot URL — do not discard and re-GET).
            var next = await source.ReadAsync(copyBuf.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
            if (next > 0)
            {
                _logger.Debug(
                    "Bandcamp native statdownload: body exceeded {0} bytes without ZIP magic — streaming remainder to {1}",
                    MaxStatdownloadTextBytes,
                    archivePath);

                var overflowPeek = new byte[textBuffer.Length + 1];
                var prior = textBuffer.ToArray();
                Buffer.BlockCopy(prior, 0, overflowPeek, 0, prior.Length);
                overflowPeek[prior.Length] = copyBuf[0];

                await StreamToFileAsync(
                        source,
                        archivePath,
                        contentLength,
                        progress,
                        overflowPeek,
                        overflowPeek.Length,
                        cancellationToken)
                    .ConfigureAwait(false);

                return new BandcampStatdownloadProbeResult
                {
                    StatusCode = (int)response.StatusCode,
                    StreamedToFile = true,
                    ContentType = contentType
                };
            }

            var text = Encoding.UTF8.GetString(textBuffer.ToArray());

            _logger.Debug(
                "Bandcamp native statdownload: text response ({0} bytes, Content-Type={1})",
                text.Length,
                contentType);

            return new BandcampStatdownloadProbeResult
            {
                StatusCode = (int)response.StatusCode,
                StreamedToFile = false,
                Content = text,
                ContentType = contentType
            };
        }

        private static async Task StreamToFileAsync(
            Stream source,
            string filePath,
            long? contentLength,
            Action<long, long?>? progress,
            byte[]? peek,
            int peekLength,
            CancellationToken cancellationToken)
        {
            progress?.Invoke(0, contentLength);

            const int bufferSize = 1024 * 128;
            var buffer = new byte[bufferSize];
            long downloaded = 0;

            await using (var destination = new FileStream(
                filePath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: bufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                if (peek != null && peekLength > 0)
                {
                    await destination.WriteAsync(peek.AsMemory(0, peekLength), cancellationToken).ConfigureAwait(false);
                    downloaded = peekLength;
                    progress?.Invoke(downloaded, contentLength);
                }

                int read;
                while ((read = await source.ReadAsync(buffer.AsMemory(0, bufferSize), cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    downloaded += read;
                    progress?.Invoke(downloaded, contentLength);
                }
            }

            if (downloaded == 0)
            {
                throw new InvalidOperationException("Download response contained no data.");
            }

            progress?.Invoke(downloaded, contentLength ?? downloaded);
        }

        private static async Task<int> ReadAtMostAsync(Stream source, byte[] buffer, CancellationToken cancellationToken)
        {
            var total = 0;
            while (total < buffer.Length)
            {
                var read = await source.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total += read;
            }

            return total;
        }

        private static bool IsZipLocalFileHeader(byte[] data)
        {
            return data.Length >= 4 &&
                   data[0] == 0x50 && data[1] == 0x4B &&
                   data[2] == 0x03 && data[3] == 0x04;
        }

        private static bool LooksLikeBinaryArchiveContentType(string contentType)
        {
            if (string.IsNullOrWhiteSpace(contentType))
            {
                return false;
            }

            return contentType.Contains("application/zip", StringComparison.OrdinalIgnoreCase) ||
                   contentType.Contains("application/x-zip", StringComparison.OrdinalIgnoreCase) ||
                   contentType.Contains("application/octet-stream", StringComparison.OrdinalIgnoreCase) ||
                   contentType.Contains("application/x-download", StringComparison.OrdinalIgnoreCase) ||
                   contentType.Contains("audio/", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Peek window large enough for JSON url payloads; tiny vs FLAC zips.</summary>
        private const int StatdownloadPeekBytes = 4096;

        /// <summary>Hard cap for non-archive text bodies (JSON / plain URL).</summary>
        private const int MaxStatdownloadTextBytes = 256 * 1024;

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
            if (_ownsClient)
            {
                _httpClient.Dispose();
            }
        }
    }

    public sealed class BandcampHttpResult
    {
        public int StatusCode { get; set; }
        public string? Content { get; set; }
        public byte[]? RawBytes { get; set; }
        public string? ContentType { get; set; }
    }

    /// <summary>
    /// Result of a one-shot statdownload / popplers probe: either a small text body
    /// (CDN URL JSON) or confirmation that the archive was streamed to disk.
    /// </summary>
    public sealed class BandcampStatdownloadProbeResult
    {
        public int StatusCode { get; set; }

        /// <summary>True when the first response body was streamed to the archive path.</summary>
        public bool StreamedToFile { get; set; }

        /// <summary>Small JSON / plain URL body when <see cref="StreamedToFile"/> is false.</summary>
        public string? Content { get; set; }

        public string? ContentType { get; set; }
    }
}
