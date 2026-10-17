using FluentValidation;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Auth.Bandcamp;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;
using NzbDrone.Core.Validation.Paths;

namespace NzbDrone.Core.Download.Clients.Bandcamp
{
    public class BandcampDownloadSettingsValidator : AbstractValidator<BandcampDownloadSettings>
    {
        public BandcampDownloadSettingsValidator()
        {
            RuleFor(c => c.Cookies).ValidBandcampIdentityCookie();

            RuleFor(c => c.DownloadPath).NotEmpty()
                .WithMessage("Download path is required");

            RuleFor(c => c.DownloadPath).IsValidPath()
                .When(c => !c.DownloadPath.IsNullOrWhiteSpace());
        }
    }

    public class BandcampDownloadSettings : IProviderConfig
    {
        private static readonly BandcampDownloadSettingsValidator Validator = new BandcampDownloadSettingsValidator();

        public BandcampDownloadSettings()
        {
            DownloadPath = "";
            Cookies = "";
        }

        [FieldDefinition(0, Label = "Identity Cookie", Type = FieldType.Password, HelpText = BandcampIdentityAuth.DownloadFieldHelpText, Privacy = PrivacyLevel.Password)]
        public string Cookies { get; set; }

        [FieldDefinition(1, Label = "Download Path", Type = FieldType.Path, HelpText = "Writable directory for Bandcamp archives before Lidarr imports them. Docker (linuxserver/hotio): usually /downloads/bandcamp — the volume is typically mounted at /downloads; avoid /downloads/downloads unless that nested folder exists inside the container.")]
        public string DownloadPath { get; set; }

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
