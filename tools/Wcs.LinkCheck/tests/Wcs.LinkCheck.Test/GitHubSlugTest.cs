using Wcs.LinkCheck;
using Xunit;

namespace Wcs.LinkCheck.Test;

public class GitHubSlugTest
{
    [Theory]
    [InlineData("Section two", "section-two")]
    [InlineData("5. I tre cicli che tengono vivo il motore", "5-i-tre-cicli-che-tengono-vivo-il-motore")]
    [InlineData("Perché così?", "perché-così")]
    [InlineData("Before — after", "before--after")]
    [InlineData("The `IRouteService_Find` method", "the-irouteservice_find-method")]
    [InlineData("**Bold** and _emphasis_ in snake_case", "bold-and-emphasis-in-snake_case")]
    [InlineData("A [link](https://example.com) in a heading", "a-link-in-a-heading")]
    [InlineData("What it is (and isn't): v1.2", "what-it-is-and-isnt-v12")]
    public void A_heading_gets_the_slug_GitHub_gives_it(string heading, string slug)
    {
        Assert.Equal(slug, GitHubSlug.Of(heading));
    }

    [Fact]
    public void Repeated_headings_get_a_numeric_suffix_in_order()
    {
        Assert.Equal(["notes", "notes-1", "notes-2", "other"], GitHubSlug.OfAll(["Notes", "Notes", "Notes", "Other"]));
    }

    [Fact]
    public void Headings_and_links_inside_code_are_not_Markdown()
    {
        var doc = MarkdownDocument.Parse("# Real\n\n```\n# Not a heading\n[not](a-link.md)\n```\n\n`[nor](this.md)` [but](this.md)\n");

        Assert.Equal(["real"], doc.Anchors.Order());
        Assert.Equal(["this.md"], doc.Links.Select(l => l.Target));
        Assert.Equal(8, doc.Links.Single().Line);
    }
}
