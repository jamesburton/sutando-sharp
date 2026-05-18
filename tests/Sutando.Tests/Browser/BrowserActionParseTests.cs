using Sutando.Browser;

namespace Sutando.Tests.Browser;

public sealed class BrowserActionParseTests
{
    [Fact]
    public void Parse_Text_ReturnsTextAction()
    {
        var action = BrowserAction.Parse("text");
        Assert.IsType<BrowserAction.Text>(action);
    }

    [Fact]
    public void Parse_Screenshot_ReturnsScreenshotWithDefaults()
    {
        var action = Assert.IsType<BrowserAction.Screenshot>(BrowserAction.Parse("screenshot"));
        Assert.Null(action.OutPath);
        Assert.True(action.FullPage);
    }

    [Fact]
    public void Parse_Pdf_ReturnsPdfWithDefaults()
    {
        var action = Assert.IsType<BrowserAction.Pdf>(BrowserAction.Parse("pdf"));
        Assert.Null(action.OutPath);
    }

    [Fact]
    public void Parse_Html_ReturnsHtmlAction()
    {
        Assert.IsType<BrowserAction.Html>(BrowserAction.Parse("html"));
    }

    [Theory]
    [InlineData("click:#submit", "#submit")]
    [InlineData("click:button.primary", "button.primary")]
    [InlineData("click:[data-id=\"x\"]", "[data-id=\"x\"]")]
    public void Parse_Click_ExtractsSelector(string expression, string expectedSelector)
    {
        var click = Assert.IsType<BrowserAction.Click>(BrowserAction.Parse(expression));
        Assert.Equal(expectedSelector, click.Selector);
    }

    [Fact]
    public void Parse_Click_PreservesColonsInSelector()
    {
        // Upstream: `action.slice(6)` — selector keeps any further colons verbatim.
        var click = Assert.IsType<BrowserAction.Click>(BrowserAction.Parse("click:a:nth-child(2)"));
        Assert.Equal("a:nth-child(2)", click.Selector);
    }

    [Fact]
    public void Parse_Fill_ExtractsSelectorAndValue()
    {
        var fill = Assert.IsType<BrowserAction.Fill>(BrowserAction.Parse("fill:#email:me@x.com"));
        Assert.Equal("#email", fill.Selector);
        Assert.Equal("me@x.com", fill.Value);
    }

    [Fact]
    public void Parse_Fill_PreservesColonsInValue()
    {
        // Upstream `parts.slice(2).join(':')` keeps colons embedded in the value.
        var fill = Assert.IsType<BrowserAction.Fill>(BrowserAction.Parse("fill:#url:https://example.com:8080/path"));
        Assert.Equal("#url", fill.Selector);
        Assert.Equal("https://example.com:8080/path", fill.Value);
    }

    [Fact]
    public void Parse_Fill_AllowsEmptyValue()
    {
        var fill = Assert.IsType<BrowserAction.Fill>(BrowserAction.Parse("fill:#email:"));
        Assert.Equal("#email", fill.Selector);
        Assert.Equal(string.Empty, fill.Value);
    }

    [Fact]
    public void Parse_Select_ExtractsSelectorAndValue()
    {
        var sel = Assert.IsType<BrowserAction.Select>(BrowserAction.Parse("select:#country:US"));
        Assert.Equal("#country", sel.Selector);
        Assert.Equal("US", sel.Value);
    }

    [Theory]
    [InlineData("wait:500", 500)]
    [InlineData("wait:0", 0)]
    [InlineData("wait:60000", 60000)]
    public void Parse_Wait_ParsesMilliseconds(string expression, int expectedMs)
    {
        var wait = Assert.IsType<BrowserAction.Wait>(BrowserAction.Parse(expression));
        Assert.Equal(expectedMs, (int)wait.Duration.TotalMilliseconds);
    }

    [Fact]
    public void Parse_NullExpression_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => BrowserAction.Parse(null!));
    }

    [Theory]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData("Text")] // case sensitive — upstream uses lowercase verbs only
    [InlineData("TEXT")]
    public void Parse_UnknownBareVerb_Throws(string expression)
    {
        Assert.Throws<FormatException>(() => BrowserAction.Parse(expression));
    }

    [Theory]
    [InlineData("foo:bar")]
    [InlineData("Click:#x")] // verbs are case sensitive
    public void Parse_UnknownColonVerb_Throws(string expression)
    {
        Assert.Throws<FormatException>(() => BrowserAction.Parse(expression));
    }

    [Fact]
    public void Parse_ClickEmptySelector_Throws()
    {
        Assert.Throws<FormatException>(() => BrowserAction.Parse("click:"));
    }

    [Theory]
    [InlineData("fill:#email")] // no second colon
    [InlineData("fill:")] // empty selector and no value
    [InlineData("fill::value")] // empty selector
    public void Parse_FillMalformed_Throws(string expression)
    {
        Assert.Throws<FormatException>(() => BrowserAction.Parse(expression));
    }

    [Theory]
    [InlineData("select:#country")] // no second colon
    [InlineData("select:")] // empty selector and no value
    [InlineData("select::US")] // empty selector
    public void Parse_SelectMalformed_Throws(string expression)
    {
        Assert.Throws<FormatException>(() => BrowserAction.Parse(expression));
    }

    [Theory]
    [InlineData("wait:")] // empty number — upstream falls back to 2000, we throw
    [InlineData("wait:abc")]
    [InlineData("wait:-1")]
    [InlineData("wait: 500")] // leading space — strict integer parse rejects
    public void Parse_WaitMalformed_Throws(string expression)
    {
        Assert.Throws<FormatException>(() => BrowserAction.Parse(expression));
    }

    [Fact]
    public void Parse_BareVerbWithTrailingColon_Throws()
    {
        // 'text:' is NOT 'text' — strict grammar; falls through to colon-verb dispatch and
        // 'text' isn't a colon-verb, so we throw.
        Assert.Throws<FormatException>(() => BrowserAction.Parse("text:"));
    }

    [Fact]
    public void NavigateActionType_NotEmittedByParser()
    {
        // Navigate is a C#-only action that the upstream CLI handles as a positional arg.
        // Sanity-check that the parser never produces one for any of the documented verbs.
        var verbs = new[]
        {
            "text", "screenshot", "pdf", "html",
            "click:#x", "fill:#x:y", "select:#x:y", "wait:100",
        };
        foreach (var v in verbs)
        {
            var action = BrowserAction.Parse(v);
            Assert.IsNotType<BrowserAction.Navigate>(action);
        }
    }
}
