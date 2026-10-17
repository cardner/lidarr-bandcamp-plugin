using System;
using System.Linq;
using System.Text.RegularExpressions;
using NzbDrone.Core.Http.Bandcamp;

namespace NzbDrone.Core.Auth.Bandcamp
{
    /// <summary>
    /// Shared Identity Cookie validation and user-facing auth messaging.
    /// Fan purchases have no OAuth — the browser <c>identity</c> cookie is the only
    /// supported credential. Auto-refresh is not available; users must re-export
    /// from DevTools when the cookie expires.
    /// </summary>
    public static class BandcampIdentityAuth
    {
        public const string FieldHelpText =
            "Paste the Bandcamp 'identity' cookie only (bare value preferred). " +
            "Chrome: DevTools (F12) → Application → Cookies → https://bandcamp.com → copy the identity value. " +
            "Firefox: Storage → Cookies. Full Cookie headers (identity=…) are also accepted. " +
            "When Test fails or downloads stop authenticating, export a fresh identity cookie — Bandcamp sessions expire and there is no OAuth refresh for fan accounts.";

        public const string DownloadFieldHelpText =
            "Same Identity Cookie as the Bandcamp indexer. Prefer the bare identity value. " +
            "Re-export from your browser when Test fails or grabs return auth errors — cookies expire and cannot be auto-refreshed.";

        public const string RefreshGuidance =
            "Re-export the identity cookie from a logged-in bandcamp.com session in your browser, " +
            "paste it into Identity Cookie, then click Test again.";

        private static readonly Regex PlaceholderPattern = new(
            @"^(your[_-]?cookie|xxx+|placeholder|changeme|example|insert|paste|todo)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly string[] WrongCookieNames =
        {
            "session", "csrftoken", "cf_clearance", "sid", "client_id", "js", "_ga", "_gid"
        };

        /// <summary>
        /// Normalize pasted input into a Cookie header that always includes <c>identity=</c>.
        /// </summary>
        public static string Normalize(string? cookies)
        {
            return BandcampNativeHttp.NormalizeIdentityCookie(cookies);
        }

        /// <summary>
        /// Static shape checks before any network call.
        /// </summary>
        public static BandcampCookieValidation ValidateShape(string? cookies)
        {
            if (string.IsNullOrWhiteSpace(cookies))
            {
                return BandcampCookieValidation.Fail(
                    BandcampAuthFailureKind.Missing,
                    "Identity Cookie is required. Paste the Bandcamp 'identity' cookie value from your browser.");
            }

            var trimmed = cookies.Trim();
            if (PlaceholderPattern.IsMatch(trimmed) ||
                PlaceholderPattern.IsMatch(ExtractIdentityValue(Normalize(trimmed))))
            {
                return BandcampCookieValidation.Fail(
                    BandcampAuthFailureKind.InvalidShape,
                    "Identity Cookie looks like a placeholder. Paste the real 'identity' value from bandcamp.com cookies.");
            }

            var normalized = Normalize(trimmed);
            var identityValue = ExtractIdentityValue(normalized);

            if (string.IsNullOrWhiteSpace(identityValue))
            {
                return BandcampCookieValidation.Fail(
                    BandcampAuthFailureKind.InvalidShape,
                    "Could not find an 'identity' value in the pasted cookie. Copy the identity cookie from https://bandcamp.com (Application/Storage → Cookies).");
            }

            // Real Bandcamp identity values are long opaque strings (often URL-encoded JSON).
            // Keep a low floor so unit fixtures can use short stand-ins; live cookies are much longer.
            if (identityValue.Length < 8)
            {
                return BandcampCookieValidation.Fail(
                    BandcampAuthFailureKind.InvalidShape,
                    "Identity Cookie is too short to be a Bandcamp session. Copy the full 'identity' value (not another cookie name).");
            }

            // User pasted only a different cookie name=value without identity.
            if (trimmed.Contains('=') &&
                !trimmed.Contains("identity=", StringComparison.OrdinalIgnoreCase) &&
                WrongCookieNames.Any(name =>
                    trimmed.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.Contains(";" + name + "=", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.Contains("; " + name + "=", StringComparison.OrdinalIgnoreCase)))
            {
                // Still allow if they somehow only pasted other cookies without identity —
                // ExtractIdentityValue already failed above when identity missing.
            }

            if (LooksLikeJwt(identityValue))
            {
                return BandcampCookieValidation.Fail(
                    BandcampAuthFailureKind.InvalidShape,
                    "Pasted value looks like a JWT/API token, not a Bandcamp identity cookie. Use the browser 'identity' cookie instead.");
            }

            return BandcampCookieValidation.Ok(normalized, identityValue);
        }

        public static string ExtractIdentityValue(string normalizedCookieHeader)
        {
            if (string.IsNullOrWhiteSpace(normalizedCookieHeader))
            {
                return string.Empty;
            }

            foreach (var part in normalizedCookieHeader.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var eq = part.IndexOf('=');
                if (eq <= 0)
                {
                    continue;
                }

                var name = part[..eq].Trim();
                if (name.Equals("identity", StringComparison.OrdinalIgnoreCase))
                {
                    return part[(eq + 1)..].Trim();
                }
            }

            return string.Empty;
        }

        public static string DescribeFailure(BandcampAuthFailureKind kind, string? detail = null)
        {
            var baseMessage = kind switch
            {
                BandcampAuthFailureKind.Missing =>
                    "Identity Cookie is required.",
                BandcampAuthFailureKind.InvalidShape =>
                    detail ?? "Identity Cookie is missing or has the wrong shape.",
                BandcampAuthFailureKind.ExpiredOrRejected =>
                    "Bandcamp did not accept this Identity Cookie (expired, logged out, or wrong account). " + RefreshGuidance,
                BandcampAuthFailureKind.ChallengePage =>
                    "Bandcamp returned a bot challenge page instead of your library. " +
                    "Export a fresh identity cookie from a normal browser session (not via automation). " + RefreshGuidance,
                BandcampAuthFailureKind.HttpUnauthorized =>
                    "Bandcamp returned HTTP 401. " + RefreshGuidance,
                BandcampAuthFailureKind.HttpForbidden =>
                    "Bandcamp returned HTTP 403. " + RefreshGuidance,
                BandcampAuthFailureKind.NetworkError =>
                    detail ?? "Could not reach Bandcamp to verify the Identity Cookie.",
                BandcampAuthFailureKind.EmptyCollection =>
                    "Identity Cookie is valid, but no downloadable purchases were found in this Bandcamp collection.",
                _ => detail ?? "Bandcamp authentication failed."
            };

            if (!string.IsNullOrWhiteSpace(detail) &&
                kind is not BandcampAuthFailureKind.InvalidShape and not BandcampAuthFailureKind.NetworkError &&
                !baseMessage.Contains(detail, StringComparison.Ordinal))
            {
                return baseMessage + " Details: " + detail;
            }

            return baseMessage;
        }

        public static bool LooksLikeChallengePage(string? content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return false;
            }

            return content.Contains("cf-challenge", StringComparison.OrdinalIgnoreCase) ||
                   content.Contains("challenge-platform", StringComparison.OrdinalIgnoreCase) ||
                   content.Contains("Just a moment", StringComparison.OrdinalIgnoreCase) ||
                   content.Contains("Checking your browser", StringComparison.OrdinalIgnoreCase) ||
                   content.Contains("Enable JavaScript and cookies", StringComparison.OrdinalIgnoreCase);
        }

        private static bool LooksLikeJwt(string value)
        {
            var parts = value.Split('.');
            return parts.Length == 3 &&
                   parts.All(p => p.Length >= 8 && p.All(c => char.IsLetterOrDigit(c) || c is '-' or '_'));
        }
    }

    public enum BandcampAuthFailureKind
    {
        None = 0,
        Missing,
        InvalidShape,
        ExpiredOrRejected,
        ChallengePage,
        HttpUnauthorized,
        HttpForbidden,
        NetworkError,
        EmptyCollection
    }

    public sealed class BandcampCookieValidation
    {
        public bool IsValid { get; init; }
        public BandcampAuthFailureKind FailureKind { get; init; }
        public string Message { get; init; } = string.Empty;
        public string NormalizedCookieHeader { get; init; } = string.Empty;
        public string IdentityValue { get; init; } = string.Empty;

        public static BandcampCookieValidation Ok(string normalized, string identityValue) => new()
        {
            IsValid = true,
            FailureKind = BandcampAuthFailureKind.None,
            NormalizedCookieHeader = normalized,
            IdentityValue = identityValue
        };

        public static BandcampCookieValidation Fail(BandcampAuthFailureKind kind, string message) => new()
        {
            IsValid = false,
            FailureKind = kind,
            Message = message
        };
    }

    public sealed class BandcampAuthProbeResult
    {
        public bool Success { get; init; }
        public long? FanId { get; init; }
        public BandcampAuthFailureKind FailureKind { get; init; }
        public string Message { get; init; } = string.Empty;
        public string NormalizedCookies { get; init; } = string.Empty;

        public static BandcampAuthProbeResult Ok(long fanId, string normalizedCookies) => new()
        {
            Success = true,
            FanId = fanId,
            FailureKind = BandcampAuthFailureKind.None,
            NormalizedCookies = normalizedCookies,
            Message = $"Authenticated as fan_id {fanId}."
        };

        public static BandcampAuthProbeResult Fail(BandcampAuthFailureKind kind, string message, string? normalized = null) => new()
        {
            Success = false,
            FailureKind = kind,
            Message = message,
            NormalizedCookies = normalized ?? string.Empty
        };
    }
}
