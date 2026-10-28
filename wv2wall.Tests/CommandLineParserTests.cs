using System;
using System.IO;
using Xunit;

public sealed class CommandLineParserTests
{
    [Fact]
    public void Defaults_are_applied()
    {
        CommandLineParseResult result = CommandLineParser.Parse([]);

        Assert.Equal(CommandLineParser.DefaultUrl, result.Url);
        Assert.Equal("", result.ScriptContent);
        Assert.Equal(WallpaperContext.WallpaperMode.Span, result.Mode);
        Assert.Equal(0, result.Monitor);
        Assert.Equal(CommandLineMessageKind.None, result.MessageKind);
    }

    [Theory]
    [InlineData("example.com", "https://example.com")]
    [InlineData("http://example.com", "http://example.com")]
    [InlineData("https://example.com/path", "https://example.com/path")]
    public void Url_is_normalized(string input, string expected)
    {
        CommandLineParseResult result = CommandLineParser.Parse(["--url", input]);

        Assert.Equal(expected, result.Url);
        Assert.Equal(CommandLineMessageKind.None, result.MessageKind);
    }

    [Fact]
    public void Unsupported_url_scheme_is_rejected()
    {
        CommandLineParseResult result = CommandLineParser.Parse(["--url", "file:///wallpaper.html"]);

        Assert.Equal(CommandLineMessageKind.Error, result.MessageKind);
        Assert.Contains("Invalid URL", result.Message);
        Assert.True(result.ShouldExit);
    }

    [Theory]
    [InlineData("span", "Span")]
    [InlineData("all", "All")]
    [InlineData("single", "Single")]
    [InlineData("SINGLE", "Single")]
    public void Mode_is_parsed_case_insensitively(string input, string expected)
    {
        CommandLineParseResult result = CommandLineParser.Parse(["--mode", input]);

        Assert.Equal(expected, result.Mode.ToString());
        Assert.Equal(CommandLineMessageKind.None, result.MessageKind);
    }

    [Fact]
    public void Invalid_mode_is_rejected()
    {
        CommandLineParseResult result = CommandLineParser.Parse(["--mode", "tiles"]);

        Assert.Equal(CommandLineMessageKind.Error, result.MessageKind);
        Assert.Contains("Expected: single, all, or span", result.Message);
    }

    [Fact]
    public void Monitor_number_is_converted_from_one_based_input()
    {
        CommandLineParseResult result = CommandLineParser.Parse(["--monitor", "3"]);

        Assert.Equal(2, result.Monitor);
        Assert.Equal(CommandLineMessageKind.None, result.MessageKind);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("not-a-number")]
    public void Invalid_monitor_is_rejected(string input)
    {
        CommandLineParseResult result = CommandLineParser.Parse(["--monitor", input]);

        Assert.Equal(CommandLineMessageKind.Error, result.MessageKind);
        Assert.Contains("Monitor numbers start at 1", result.Message);
    }

    [Fact]
    public void Script_content_is_forwarded()
    {
        CommandLineParseResult result = CommandLineParser.Parse(["--script-content", "document.body.dataset.test = 'ok';"]);

        Assert.Equal("document.body.dataset.test = 'ok';", result.ScriptContent);
        Assert.Equal(CommandLineMessageKind.None, result.MessageKind);
    }

    [Fact]
    public void Script_file_is_loaded()
    {
        string path = Path.Combine(Path.GetTempPath(), $"wv2wall-{Guid.NewGuid():N}.js");
        try
        {
            File.WriteAllText(path, "console.log('test');");

            CommandLineParseResult result = CommandLineParser.Parse(["--script", path]);

            Assert.Equal("console.log('test');", result.ScriptContent);
            Assert.Equal(CommandLineMessageKind.None, result.MessageKind);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Missing_script_file_is_rejected()
    {
        string path = Path.Combine(Path.GetTempPath(), $"wv2wall-missing-{Guid.NewGuid():N}.js");

        CommandLineParseResult result = CommandLineParser.Parse(["--script", path]);

        Assert.Equal(CommandLineMessageKind.Error, result.MessageKind);
        Assert.Contains("Script file not found", result.Message);
    }

    [Fact]
    public void Help_returns_help_message_without_starting_app()
    {
        CommandLineParseResult result = CommandLineParser.Parse(["--help"]);

        Assert.Equal(CommandLineMessageKind.Help, result.MessageKind);
        Assert.Contains("Usage:", result.Message);
        Assert.True(result.ShouldExit);
    }

    [Fact]
    public void Unknown_argument_is_warning_and_does_not_block_startup()
    {
        CommandLineParseResult result = CommandLineParser.Parse(["--unknown"]);

        Assert.Equal(CommandLineMessageKind.Warning, result.MessageKind);
        Assert.Contains("--unknown", result.Message);
        Assert.False(result.ShouldExit);
    }
}
