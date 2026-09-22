using System.Text.RegularExpressions;
using Gateway.Api.Auth;

namespace Gateway.Api.Tests.Auth;

public sealed class RegistrationControllerTests
{
    private static readonly Regex SlugShape = new("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.Compiled);

    [Theory]
    [InlineData("Acme Corp!", "acme-corp")]
    [InlineData("  --Hello__World--  ", "hello-world")]
    [InlineData("日本語", "org")]
    [InlineData("a--b---c", "a-b-c")]
    [InlineData("ACME", "acme")]
    [InlineData("Team 42", "team-42")]
    [InlineData("", "org")]
    [InlineData("!!!", "org")]
    [InlineData("Café Société", "caf-soci-t")]
    public void Slugify_lowercases_hyphenates_and_strips_non_ascii(string name, string expected)
    {
        Assert.Equal(expected, RegistrationController.Slugify(name));
    }

    [Fact]
    public void Slugify_truncates_long_names_to_at_most_40_chars_without_a_trailing_hyphen()
    {
        // 10 x "abcdefghi " = 100 chars; the 40th char of the slug would be a hyphen.
        var name = string.Concat(Enumerable.Repeat("abcdefghi ", 10));
        Assert.Equal(100, name.Length);

        var slug = RegistrationController.Slugify(name);

        Assert.True(slug.Length <= 40, $"slug was {slug.Length} chars: {slug}");
        Assert.False(slug.EndsWith('-'));
        Assert.Matches(SlugShape, slug);
        Assert.StartsWith("abcdefghi-abcdefghi", slug);
    }

    [Fact]
    public void Slugify_keeps_exactly_40_chars_when_no_hyphen_is_cut()
    {
        var name = new string('x', 100);

        var slug = RegistrationController.Slugify(name);

        Assert.Equal(40, slug.Length);
        Assert.Equal(new string('x', 40), slug);
    }

    [Fact]
    public void Slugify_output_always_matches_the_slug_shape()
    {
        foreach (var name in new[] { "Acme Corp!", "  --Hello__World--  ", "a--b---c", "MiXeD-CaSe_99", "tabs\tand\nnewlines" })
        {
            var slug = RegistrationController.Slugify(name);
            Assert.Matches(SlugShape, slug);
        }
    }
}
