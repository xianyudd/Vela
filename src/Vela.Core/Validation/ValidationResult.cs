using System.Collections.Immutable;

namespace Vela.Core.Validation;

public sealed record ValidationResult(ImmutableArray<ValidationError> Errors)
{
    public static ValidationResult Valid { get; } = new(ImmutableArray<ValidationError>.Empty);

    public bool IsValid => Errors.IsDefaultOrEmpty;
}

public sealed record ValidationError(ProfileValidationErrorCode Code, string Message);

public enum ProfileValidationErrorCode
{
    ProfileRequired,
    DistroNameRequired,
    DistroNameContainsControlCharacter,
    VhdxPathRequired,
    VhdxPathMustBeWindowsAbsolute,
    VhdxPathMustHaveVhdxExtension,
    VhdxPathContainsControlCharacter,
    VhdxPathContainsInvalidWindowsCharacter,
    VhdxPathMustBeAscii,
    ShutdownModeUnsupported,
    ShutdownTimeoutOutOfRange
}
