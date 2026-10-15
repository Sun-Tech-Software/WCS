using Wcs.LinkCheck;
using Xunit;

namespace Wcs.LinkCheck.Test;

public class LinkCheckerTest
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    [Fact]
    public void A_tree_whose_links_all_resolve_has_no_finding()
    {
        var findings = new LinkChecker(Fixture("good")).Check();

        Assert.Empty(findings);
    }

    [Fact]
    public void Every_broken_link_is_reported_on_its_line_with_its_reason()
    {
        var findings = new LinkChecker(Fixture("bad"), forbidden: ["private"]).Check()
            .Where(f => f.File == "README.md").ToDictionary(f => f.Line);

        // The fixture numbers its items: item n is on line n + 2.
        Assert.Contains("wrong case: on disk it is 'Sub'", findings[3].Problem);
        Assert.Equal("file not found", findings[4].Problem);
        Assert.Equal("no heading '#no-such-heading' in Sub/page.md", findings[5].Problem);
        Assert.Contains("write 'code.txt#L2'", findings[6].Problem);
        Assert.Equal("line 9 does not exist: the file has 3 lines", findings[7].Problem);
        Assert.Equal("line 4 does not exist: the file has 3 lines", findings[8].Problem);
        Assert.Contains("not Markdown", findings[9].Problem);
        Assert.Equal("no heading '#L3' in Sub/page.md", findings[10].Problem);
        Assert.Contains("absolute local path", findings[11].Problem);
        Assert.Equal("points to a part that is not in the public repository", findings[12].Problem);
        Assert.Equal("goes above the root of the repository", findings[13].Problem);
        Assert.Equal("absolute local path in the text", findings[14].Problem);
        Assert.Equal("no heading '#nowhere' in README.md", findings[15].Problem);
        Assert.Equal(13, findings.Count);
    }

    [Fact]
    public void The_command_fails_with_the_list_and_passes_on_a_clean_tree()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        Assert.Equal(1, LinkCheckCommand.Run([Fixture("bad"), "--forbid", "private"], output, error));
        Assert.Contains("README.md:4: (Sub/Nothing.md) file not found", error.ToString());

        Assert.Equal(0, LinkCheckCommand.Run([Fixture("good")], output, error));
        Assert.Contains("Links OK", output.ToString());
    }

    [Fact]
    public void Excluded_paths_and_the_default_exclusions_are_not_read()
    {
        var root = Fixture("bad");

        Assert.Empty(new LinkChecker(root, excluded: ["README.md"]).Check());
        Assert.Contains("docs/history", LinkChecker.DefaultExclusions);
    }

    [Fact]
    public void A_usage_error_exits_with_two()
    {
        Assert.Equal(2, LinkCheckCommand.Run(["--nonsense"], TextWriter.Null, TextWriter.Null));
        Assert.Equal(2, LinkCheckCommand.Run([Path.Combine(Fixture("good"), "no-such-folder")], TextWriter.Null, TextWriter.Null));
    }
}
