using Vela.Core.Models;
using Vela.Core.Validation;

namespace Vela.Tests.Core;

public sealed class ProfileValidatorTests
{
    [Fact]
    public void Validate_WhenProfileIsNull_ReturnsPresentableProfileRequiredError()
    {
        var result = ProfileValidator.Validate(null);

        AssertIssue(result, ProfileValidationErrorCode.ProfileRequired);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_WhenDistroNameIsBlank_ReturnsPresentableRequiredError(string distroName)
    {
        var result = ProfileValidator.Validate(CreateProfile(distroName: distroName));

        var issue = Assert.Single(result.Errors);
        Assert.False(result.IsValid);
        Assert.Equal(ProfileValidationErrorCode.DistroNameRequired, issue.Code);
        Assert.False(string.IsNullOrWhiteSpace(issue.Message));
    }

    [Theory]
    [InlineData("Ubuntu\u0000")]
    [InlineData("Ubuntu\r")]
    [InlineData("Ubuntu\n")]
    public void Validate_WhenDistroNameContainsControlCharacter_ReturnsControlCharacterError(string distroName)
    {
        var result = ProfileValidator.Validate(CreateProfile(distroName: distroName));

        AssertIssue(result, ProfileValidationErrorCode.DistroNameContainsControlCharacter);
    }

    [Fact]
    public void Validate_WhenVhdxPathIsRelative_ReturnsAbsolutePathError()
    {
        var result = ProfileValidator.Validate(CreateProfile(vhdxPath: "Vela\\ext4.vhdx"));

        AssertIssue(result, ProfileValidationErrorCode.VhdxPathMustBeWindowsAbsolute);
    }

    [Theory]
    [InlineData("\\\\server\\\\ext4.vhdx")]
    [InlineData("\\\\.\\Vela\\ext4.vhdx")]
    public void Validate_WhenUncPathDoesNotContainServerShareAndFileSegments_ReturnsAbsolutePathError(
        string vhdxPath)
    {
        var result = ProfileValidator.Validate(CreateProfile(vhdxPath: vhdxPath));

        AssertIssue(result, ProfileValidationErrorCode.VhdxPathMustBeWindowsAbsolute);
    }

    [Fact]
    public void Validate_WhenVhdxPathDoesNotUseVhdxSuffix_ReturnsSuffixError()
    {
        var result = ProfileValidator.Validate(CreateProfile(vhdxPath: "D:\\Vela\\ext4.img"));

        AssertIssue(result, ProfileValidationErrorCode.VhdxPathMustHaveVhdxExtension);
    }

    [Theory]
    [InlineData("D:\\Vela\\ext4\".vhdx")]
    [InlineData("D:\\Vela\\ext4<.vhdx")]
    [InlineData("D:\\Vela\\ext4>.vhdx")]
    [InlineData("D:\\Vela\\ext4|.vhdx")]
    [InlineData("D:\\Vela\\ext4?.vhdx")]
    [InlineData("D:\\Vela\\ext4*.vhdx")]
    [InlineData("D:\\Vela:invalid\\ext4.vhdx")]
    public void Validate_WhenVhdxPathContainsInvalidWindowsCharacter_ReturnsInvalidCharacterError(
        string vhdxPath)
    {
        var result = ProfileValidator.Validate(CreateProfile(vhdxPath: vhdxPath));

        AssertIssue(result, ProfileValidationErrorCode.VhdxPathContainsInvalidWindowsCharacter);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_WhenVhdxPathIsBlank_ReturnsPresentableRequiredError(string vhdxPath)
    {
        var result = ProfileValidator.Validate(CreateProfile(vhdxPath: vhdxPath));

        AssertIssue(result, ProfileValidationErrorCode.VhdxPathRequired);
    }

    [Theory]
    [InlineData("D:\\Vela\\ext4\u0000.vhdx")]
    [InlineData("D:\\Vela\\ext4\r.vhdx")]
    [InlineData("D:\\Vela\\ext4\n.vhdx")]
    public void Validate_WhenVhdxPathContainsControlCharacter_ReturnsControlCharacterError(string vhdxPath)
    {
        var result = ProfileValidator.Validate(CreateProfile(vhdxPath: vhdxPath));

        AssertIssue(result, ProfileValidationErrorCode.VhdxPathContainsControlCharacter);
    }

    [Fact]
    public void Validate_WhenVhdxPathContainsNonAsciiCharacters_ReturnsDiskPartAsciiError()
    {
        var result = ProfileValidator.Validate(CreateProfile(vhdxPath: "D:\\Vela\\用户\\ext4.vhdx"));

        AssertIssue(result, ProfileValidationErrorCode.VhdxPathMustBeAscii);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(301)]
    public void Validate_WhenShutdownTimeoutIsOutsideSupportedRange_ReturnsTimeoutError(int timeoutSeconds)
    {
        var result = ProfileValidator.Validate(CreateProfile(shutdownTimeout: TimeSpan.FromSeconds(timeoutSeconds)));

        AssertIssue(result, ProfileValidationErrorCode.ShutdownTimeoutOutOfRange);
    }

    [Fact]
    public void Validate_WhenShutdownModeIsUnsupported_ReturnsUnsupportedModeError()
    {
        var result = ProfileValidator.Validate(CreateProfile(shutdownMode: (ShutdownMode)999));

        AssertIssue(result, ProfileValidationErrorCode.ShutdownModeUnsupported);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(300)]
    public void Validate_WhenShutdownTimeoutIsAtSupportedBoundary_IsValid(int timeoutSeconds)
    {
        var result = ProfileValidator.Validate(CreateProfile(shutdownTimeout: TimeSpan.FromSeconds(timeoutSeconds)));

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Validate_WhenProfileIsTheCurrentUbuntuDefault_IsValid()
    {
        var result = ProfileValidator.Validate(CreateProfile());

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Validate_WhenDistroNameContainsUnicodeCharacters_IsValid()
    {
        var result = ProfileValidator.Validate(CreateProfile(distroName: "Ubuntu-二十四"));

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Validate_WhenVhdxPathUsesUppercaseExtension_IsValid()
    {
        var result = ProfileValidator.Validate(CreateProfile(vhdxPath: "D:\\Vela\\ext4.VHDX"));

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Theory]
    [InlineData("D:\\Vela\\ext4.vhdx")]
    [InlineData("d:/Vela/ext4.vhdx")]
    [InlineData("\\\\server\\share\\ext4.vhdx")]
    public void Validate_WhenVhdxPathIsWindowsAbsolute_IsValid(string vhdxPath)
    {
        var result = ProfileValidator.Validate(CreateProfile(vhdxPath: vhdxPath));

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Validate_WhenProfileHasMultipleProblems_ReturnsAllIssuesInStableOrder()
    {
        var result = ProfileValidator.Validate(
            CreateProfile(
                distroName: "Ubuntu\r",
                vhdxPath: "Vela\\ext4*.img",
                shutdownMode: (ShutdownMode)999,
                shutdownTimeout: TimeSpan.FromSeconds(4)));

        Assert.False(result.IsValid);
        Assert.Equal(
            [
                ProfileValidationErrorCode.DistroNameContainsControlCharacter,
                ProfileValidationErrorCode.VhdxPathMustBeWindowsAbsolute,
                ProfileValidationErrorCode.VhdxPathMustHaveVhdxExtension,
                ProfileValidationErrorCode.VhdxPathContainsInvalidWindowsCharacter,
                ProfileValidationErrorCode.ShutdownModeUnsupported,
                ProfileValidationErrorCode.ShutdownTimeoutOutOfRange
            ],
            result.Errors.Select(static error => error.Code));
    }

    private static void AssertIssue(ValidationResult result, ProfileValidationErrorCode expectedCode)
    {
        var issue = Assert.Single(result.Errors);

        Assert.False(result.IsValid);
        Assert.Equal(expectedCode, issue.Code);
        Assert.False(string.IsNullOrWhiteSpace(issue.Message));
    }

    private static Profile CreateProfile(
        string distroName = "Ubuntu-24.04",
        string vhdxPath = "D:\\DevTools\\WSL2\\Ubuntu24.04\\ext4.vhdx",
        ShutdownMode shutdownMode = ShutdownMode.Global,
        TimeSpan? shutdownTimeout = null) => new(
            Guid.Parse("7ac4ef71-05b1-4b89-ae2d-ef644c9ae7eb"),
            "Ubuntu 24.04",
            distroName,
            vhdxPath,
            shutdownMode,
            shutdownTimeout ?? TimeSpan.FromSeconds(45));
}
