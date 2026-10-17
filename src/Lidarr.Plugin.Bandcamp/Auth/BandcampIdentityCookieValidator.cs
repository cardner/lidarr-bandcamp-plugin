using FluentValidation;

namespace NzbDrone.Core.Auth.Bandcamp
{
    /// <summary>
    /// FluentValidation rule shared by indexer and download-client settings.
    /// </summary>
    public static class BandcampIdentityCookieValidator
    {
        public static IRuleBuilderOptions<T, string> ValidBandcampIdentityCookie<T>(
            this IRuleBuilder<T, string> ruleBuilder)
        {
            return ruleBuilder
                .Must(cookies => BandcampIdentityAuth.ValidateShape(cookies).IsValid)
                .WithMessage((_, cookies) =>
                {
                    var result = BandcampIdentityAuth.ValidateShape(cookies);
                    return string.IsNullOrWhiteSpace(result.Message)
                        ? "Identity Cookie is invalid."
                        : result.Message;
                });
        }
    }
}
