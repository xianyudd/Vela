using System.Collections.Immutable;
using Vela.Core.Models;

namespace Vela.Core.Validation;

public static class ProfileValidator
{
    private static readonly TimeSpan MinimumShutdownTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaximumShutdownTimeout = TimeSpan.FromSeconds(300);

    public static ValidationResult Validate(Profile? profile)
    {
        if (profile is null)
        {
            return new ValidationResult(
                ImmutableArray.Create(
                    new ValidationError(
                        ProfileValidationErrorCode.ProfileRequired,
                        "A target profile is required.")));
        }

        var errors = ImmutableArray<ValidationError>.Empty;

        var distroName = profile.DistroName;

        if (string.IsNullOrWhiteSpace(distroName))
        {
            errors = errors.Add(
                new ValidationError(
                    ProfileValidationErrorCode.DistroNameRequired,
                    "The WSL distribution name is required."));
        }

        if (distroName is not null && distroName.Any(char.IsControl))
        {
            errors = errors.Add(
                new ValidationError(
                    ProfileValidationErrorCode.DistroNameContainsControlCharacter,
                    "The WSL distribution name contains a control character."));
        }

        errors = ValidateVhdxPath(profile.VhdxPath, errors);

        if (!Enum.IsDefined(profile.ShutdownMode))
        {
            errors = errors.Add(
                new ValidationError(
                    ProfileValidationErrorCode.ShutdownModeUnsupported,
                    "The shutdown mode is not supported."));
        }

        if (profile.ShutdownTimeout < MinimumShutdownTimeout || profile.ShutdownTimeout > MaximumShutdownTimeout)
        {
            errors = errors.Add(
                new ValidationError(
                    ProfileValidationErrorCode.ShutdownTimeoutOutOfRange,
                    "The shutdown timeout must be between 5 and 300 seconds."));
        }

        return errors.IsEmpty ? ValidationResult.Valid : new ValidationResult(errors);
    }

    private static ImmutableArray<ValidationError> ValidateVhdxPath(
        string? vhdxPath,
        ImmutableArray<ValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(vhdxPath))
        {
            return errors.Add(
                new ValidationError(
                    ProfileValidationErrorCode.VhdxPathRequired,
                    "The VHDX path is required."));
        }

        if (!IsWindowsAbsolutePath(vhdxPath))
        {
            errors = errors.Add(
                new ValidationError(
                    ProfileValidationErrorCode.VhdxPathMustBeWindowsAbsolute,
                    "The VHDX path must be an absolute Windows path."));
        }

        if (!vhdxPath.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase))
        {
            errors = errors.Add(
                new ValidationError(
                    ProfileValidationErrorCode.VhdxPathMustHaveVhdxExtension,
                    "The VHDX path must end with the .vhdx extension."));
        }

        if (vhdxPath.Any(char.IsControl))
        {
            errors = errors.Add(
                new ValidationError(
                    ProfileValidationErrorCode.VhdxPathContainsControlCharacter,
                    "The VHDX path contains a control character."));
        }

        if (ContainsInvalidWindowsPathCharacter(vhdxPath))
        {
            errors = errors.Add(
                new ValidationError(
                    ProfileValidationErrorCode.VhdxPathContainsInvalidWindowsCharacter,
                    "The VHDX path contains an invalid Windows path character."));
        }

        if (vhdxPath.Any(static character => character > '\x7f'))
        {
            errors = errors.Add(
                new ValidationError(
                    ProfileValidationErrorCode.VhdxPathMustBeAscii,
                    "The VHDX path must contain only ASCII characters for DiskPart."));
        }

        return errors;
    }

    private static bool IsWindowsAbsolutePath(string path) =>
        IsDriveRootedPath(path) || IsUncPath(path);

    private static bool IsDriveRootedPath(string path) =>
        path.Length >= 3 &&
        IsAsciiLetter(path[0]) &&
        path[1] == ':' &&
        IsWindowsDirectorySeparator(path[2]);

    private static bool IsUncPath(string path)
    {
        if (path.Length < 5 || path[0] != '\\' || path[1] != '\\')
        {
            return false;
        }

        if (path[2] is '.' or '?' && path[3] == '\\')
        {
            return false;
        }

        const int serverStartIndex = 2;
        var serverSeparatorIndex = path.IndexOf('\\', serverStartIndex);
        if (serverSeparatorIndex <= serverStartIndex)
        {
            return false;
        }

        var shareStartIndex = serverSeparatorIndex + 1;
        var shareSeparatorIndex = path.IndexOf('\\', shareStartIndex);
        return shareSeparatorIndex > shareStartIndex && shareSeparatorIndex < path.Length - 1;
    }

    private static bool ContainsInvalidWindowsPathCharacter(string path)
    {
        for (var index = 0; index < path.Length; index++)
        {
            var character = path[index];
            if (character is '"' or '<' or '>' or '|' or '?' or '*' ||
                character == ':' && (index != 1 || !IsAsciiLetter(path[0])))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsAsciiLetter(char character) =>
        character is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static bool IsWindowsDirectorySeparator(char character) =>
        character is '\\' or '/';
}
