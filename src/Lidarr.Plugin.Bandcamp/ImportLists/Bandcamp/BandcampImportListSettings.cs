using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Auth.Bandcamp;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.ImportLists.Bandcamp
{
    public class BandcampImportListSettingsValidator : AbstractValidator<BandcampImportListSettings>
    {
        public BandcampImportListSettingsValidator()
        {
            RuleFor(c => c.BaseUrl).ValidRootUrl();
            RuleFor(c => c.Cookies).ValidBandcampIdentityCookie();
        }
    }

    public class BandcampImportListSettings : IImportListSettings
    {
        private static readonly BandcampImportListSettingsValidator Validator = new BandcampImportListSettingsValidator();

        public BandcampImportListSettings()
        {
            BaseUrl = "https://bandcamp.com";
        }

        [FieldDefinition(
            0,
            Label = "Identity Cookie",
            Type = FieldType.Password,
            HelpText = BandcampIdentityAuth.FieldHelpText +
                " List sync pre-resolves MusicBrainz IDs (skips SkyHook failures per album). " +
                "Enable Automatic Add + Search for New Items, and Bandcamp in Delay Profiles. " +
                "Indexer RSS will not add library albums by itself.",
            Privacy = PrivacyLevel.Password)]
        public string Cookies { get; set; } = "";

        [FieldDefinition(1, Label = "Base URL", Type = FieldType.Url, HelpText = "Bandcamp base URL (unused for list fetch; kept for IImportListSettings)", Advanced = true)]
        public string BaseUrl { get; set; }

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
