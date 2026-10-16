using System.Text.RegularExpressions;

namespace PartnerBank.Api.Api;

/// <summary>EWP's references (1-64 letters, digits or hyphens) - checked before they reach the database.</summary>
public static partial class References
{
    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ Allow-list validation: EWP's references are 1-64 letters, digits or hyphens - checked
    // │ before they reach the database or a log line; anything else is a validation problem.
    // └──────────────────────────────────────────────────────────────────────────────────────
    public static bool IsValid(string? reference) => reference is not null && Pattern().IsMatch(reference);

    public static IResult Invalid() =>
        Results.ValidationProblem(new Dictionary<string, string[]> { ["ewpReference"] = ["1-64 letters, digits or hyphens."] });

    [GeneratedRegex("^[A-Za-z0-9-]{1,64}$")]
    private static partial Regex Pattern();
}