using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Indexers.Bandcamp
{
    public class BandcampIndexerSettingsValidator : AbstractValidator<BandcampIndexerSettings>
    {
        public BandcampIndexerSettingsValidator()
        {
            RuleFor(c => c.BaseUrl).ValidRootUrl();
            RuleFor(c => c.Cookies).NotEmpty()
                .When(c => !string.IsNullOrWhiteSpace(c.BaseUrl))
                .WithMessage("Bandcamp identity cookie is required to search your purchase library");
        }
    }

    public class BandcampIndexerSettings : IIndexerSettings
    {
        private static readonly BandcampIndexerSettingsValidator Validator = new BandcampIndexerSettingsValidator();

        public BandcampIndexerSettings()
        {
            BaseUrl = "https://bandcamp.com";
        }

        [FieldDefinition(0, Label = "Identity Cookie", Type = FieldType.Password, HelpText = "Paste only the Bandcamp 'identity' cookie value (or the full Cookie header). Chrome: DevTools → Application → Cookies → https://bandcamp.com → identity. Firefox: Storage → Cookies. Bare values are accepted; no need to type identity=.", Privacy = PrivacyLevel.Password)]
        public string Cookies { get; set; } = "";

        [FieldDefinition(1, Label = "Base URL", Type = FieldType.Url, HelpText = "Bandcamp base URL", Advanced = true)]
        public string BaseUrl { get; set; }

        [FieldDefinition(2, Type = FieldType.Number, Label = "Early Download Limit", HelpText = "Time before release date Lidarr will download from this indexer, empty is no limit", Unit = "days", Advanced = true)]
        public int? EarlyReleaseLimit { get; set; }

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
