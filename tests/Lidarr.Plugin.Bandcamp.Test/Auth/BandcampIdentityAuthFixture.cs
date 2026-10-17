using NzbDrone.Core.Auth.Bandcamp;
using Xunit;

namespace Lidarr.Plugin.Bandcamp.Test.Auth
{
    public class BandcampIdentityAuthFixture
    {
        [Fact]
        public void ValidateShape_Empty_ReturnsMissing()
        {
            var result = BandcampIdentityAuth.ValidateShape("  ");
            Assert.False(result.IsValid);
            Assert.Equal(BandcampAuthFailureKind.Missing, result.FailureKind);
            Assert.Contains("required", result.Message, System.StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData("xxx")]
        [InlineData("placeholder")]
        [InlineData("changeme")]
        [InlineData("your_cookie")]
        public void ValidateShape_Placeholder_ReturnsInvalidShape(string value)
        {
            var result = BandcampIdentityAuth.ValidateShape(value);
            Assert.False(result.IsValid);
            Assert.Equal(BandcampAuthFailureKind.InvalidShape, result.FailureKind);
            Assert.Contains("placeholder", result.Message, System.StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ValidateShape_TooShort_ReturnsInvalidShape()
        {
            var result = BandcampIdentityAuth.ValidateShape("short");
            Assert.False(result.IsValid);
            Assert.Equal(BandcampAuthFailureKind.InvalidShape, result.FailureKind);
            Assert.Contains("too short", result.Message, System.StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ValidateShape_JwtLike_ReturnsInvalidShape()
        {
            var jwt = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.signaturepart";
            var result = BandcampIdentityAuth.ValidateShape(jwt);
            Assert.False(result.IsValid);
            Assert.Equal(BandcampAuthFailureKind.InvalidShape, result.FailureKind);
            Assert.Contains("JWT", result.Message, System.StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ValidateShape_BareValue_NormalizesIdentity()
        {
            var result = BandcampIdentityAuth.ValidateShape("a1b2c3d4e5f6g7h8");
            Assert.True(result.IsValid);
            Assert.Equal("identity=a1b2c3d4e5f6g7h8", result.NormalizedCookieHeader);
            Assert.Equal("a1b2c3d4e5f6g7h8", result.IdentityValue);
        }

        [Fact]
        public void ValidateShape_FullHeader_ExtractsIdentity()
        {
            var result = BandcampIdentityAuth.ValidateShape("js=1; identity=%22abc%22; session=nope");
            Assert.True(result.IsValid);
            Assert.Equal("%22abc%22", result.IdentityValue);
            Assert.Contains("identity=", result.NormalizedCookieHeader);
        }

        [Fact]
        public void ValidateShape_MissingIdentityName_WithOtherCookies_Fails()
        {
            // Normalize treats any '=' string as a full header; without identity= extract fails.
            var result = BandcampIdentityAuth.ValidateShape("session=abc12345; js=1");
            Assert.False(result.IsValid);
            Assert.Equal(BandcampAuthFailureKind.InvalidShape, result.FailureKind);
        }

        [Theory]
        [InlineData("Just a moment... Checking your browser", true)]
        [InlineData("cf-challenge script", true)]
        [InlineData("{\"fan_id\":1}", false)]
        [InlineData("", false)]
        public void LooksLikeChallengePage_DetectsKnownMarkers(string content, bool expected)
        {
            Assert.Equal(expected, BandcampIdentityAuth.LooksLikeChallengePage(content));
        }

        [Fact]
        public void DescribeFailure_Expired_IncludesRefreshGuidance()
        {
            var message = BandcampIdentityAuth.DescribeFailure(BandcampAuthFailureKind.ExpiredOrRejected);
            Assert.Contains("Re-export", message, System.StringComparison.OrdinalIgnoreCase);
            Assert.Contains("identity", message, System.StringComparison.OrdinalIgnoreCase);
        }
    }
}
