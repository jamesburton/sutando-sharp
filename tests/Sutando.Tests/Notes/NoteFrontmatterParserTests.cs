using System.Collections.Generic;
using Sutando.Notes;

namespace Sutando.Tests.Notes;

/// <summary>
/// Round-trip coverage for <see cref="NoteFrontmatterParser"/>. Verifies the parser correctly
/// splits the leading YAML block, projects scalars / lists / nested maps into the documented
/// shapes, and that <see cref="NoteFrontmatterParser.Compose"/> rebuilds an equivalent file.
/// </summary>
public sealed class NoteFrontmatterParserTests
{
    [Fact]
    public void Parse_NoFrontmatter_TreatsEntireFileAsBody()
    {
        const string Content = "# Hello\n\nJust a body.\n";
        var (fm, body) = NoteFrontmatterParser.Parse(Content);

        Assert.Empty(fm);
        Assert.Equal(Content, body);
    }

    [Fact]
    public void Parse_EmptyFrontmatter_ReturnsEmptyMapAndBody()
    {
        const string Content = "---\n---\nBody here\n";
        var (fm, body) = NoteFrontmatterParser.Parse(Content);

        Assert.Empty(fm);
        Assert.Equal("Body here\n", body);
    }

    [Fact]
    public void Parse_ScalarsAndList_ProducesPassThroughMap()
    {
        const string Content = """
                               ---
                               title: My Note
                               version: 3
                               draft: true
                               tags:
                                 - ideas
                                 - workflow
                               ---
                               body content
                               """;

        var (fm, body) = NoteFrontmatterParser.Parse(Content);

        Assert.Equal("My Note", fm["title"]);
        Assert.Equal(3L, fm["version"]);
        Assert.Equal(true, fm["draft"]);
        var tags = Assert.IsAssignableFrom<IReadOnlyList<object?>>(fm["tags"]);
        Assert.Equal(["ideas", "workflow"], tags.Select(t => t?.ToString()));
        Assert.Equal("body content", body.TrimEnd('\n'));
    }

    [Fact]
    public void Parse_NestedMap_RecursesIntoStringKeyedDictionary()
    {
        const string Content = """
                               ---
                               author:
                                 name: jane
                                 contacts:
                                   email: jane@example.com
                                   discord: jane#0001
                               ---
                               """;

        var (fm, _) = NoteFrontmatterParser.Parse(Content);

        var author = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(fm["author"]);
        Assert.Equal("jane", author["name"]);
        var contacts = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(author["contacts"]);
        Assert.Equal("jane@example.com", contacts["email"]);
        Assert.Equal("jane#0001", contacts["discord"]);
    }

    [Fact]
    public void ExtractTags_OnListValue_ReturnsTagStrings()
    {
        var fm = new Dictionary<string, object?>
        {
            ["tags"] = new List<object?> { "ideas", "voice", "projects" },
        };

        var tags = NoteFrontmatterParser.ExtractTags(fm);

        Assert.Equal(["ideas", "voice", "projects"], tags);
    }

    [Fact]
    public void ExtractTags_OnAbsentKey_ReturnsEmpty()
    {
        var tags = NoteFrontmatterParser.ExtractTags(new Dictionary<string, object?>());
        Assert.Empty(tags);
    }

    [Fact]
    public void ExtractTags_OnScalarString_ReturnsSingletonList()
    {
        // Some authors (and a few existing upstream notes) write `tags: foo` as a scalar rather
        // than a list. Treat that as one tag rather than silently dropping it.
        var fm = new Dictionary<string, object?> { ["tags"] = "ideas" };
        Assert.Equal(["ideas"], NoteFrontmatterParser.ExtractTags(fm));
    }

    [Fact]
    public void Compose_EmptyFrontmatter_OmitsFences()
    {
        var output = NoteFrontmatterParser.Compose(new Dictionary<string, object?>(), "just a body");

        Assert.DoesNotContain("---", output);
        Assert.EndsWith("just a body\n", output);
    }

    [Fact]
    public void Compose_RoundTripsWithParse()
    {
        var original = new Dictionary<string, object?>
        {
            ["title"] = "Round Trip",
            ["draft"] = false,
            ["count"] = 42L,
            ["tags"] = new List<object?> { "a", "b" },
            ["author"] = new Dictionary<string, object?>
            {
                ["name"] = "jane",
                ["email"] = "jane@example.com",
            },
        };

        var serialised = NoteFrontmatterParser.Compose(original, "body line 1\nbody line 2");

        // Sanity-check the on-disk format.
        Assert.StartsWith("---\n", serialised);
        Assert.Contains("\n---\n", serialised);

        var (parsed, body) = NoteFrontmatterParser.Parse(serialised);

        Assert.Equal("Round Trip", parsed["title"]);
        Assert.Equal(false, parsed["draft"]);
        Assert.Equal(42L, parsed["count"]);
        var tags = Assert.IsAssignableFrom<IReadOnlyList<object?>>(parsed["tags"]);
        Assert.Equal(["a", "b"], tags.Select(t => t?.ToString()));
        var author = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(parsed["author"]);
        Assert.Equal("jane", author["name"]);
        Assert.Equal("body line 1\nbody line 2\n", body);
    }

    [Fact]
    public void Parse_CrlfLineEndings_HandledCorrectly()
    {
        // Windows-authored notes (notably the upstream Sutando workspace running on Windows) use
        // CRLF; the parser should treat the fence line the same way regardless of separator.
        const string Content = "---\r\ntitle: CRLF\r\n---\r\nBody!\r\n";
        var (fm, body) = NoteFrontmatterParser.Parse(Content);

        Assert.Equal("CRLF", fm["title"]);
        Assert.Equal("Body!\r\n", body);
    }

    [Fact]
    public void Parse_MalformedYaml_ThrowsFormatException()
    {
        // Unclosed quote inside the frontmatter — YamlDotNet raises a YamlException, which the
        // parser normalises to FormatException for call sites.
        const string Content = "---\ntitle: \"unterminated\n---\nbody\n";
        Assert.Throws<FormatException>(() => NoteFrontmatterParser.Parse(Content));
    }
}
