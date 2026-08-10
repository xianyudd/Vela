namespace Vela.Tui.Views;

public static class ExactConfirmationPolicy
{
    public static bool IsAccepted(string? input) =>
        string.Equals(input, "YES", StringComparison.Ordinal);
}
