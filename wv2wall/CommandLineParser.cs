internal enum CommandLineMessageKind
{
    None,
    Help,
    Warning,
    Error
}

internal sealed record CommandLineParseResult(
    string Url,
    string ScriptContent,
    WallpaperContext.WallpaperMode Mode,
    int Monitor,
    CommandLineMessageKind MessageKind = CommandLineMessageKind.None,
    string Message = "")
{
    public bool ShouldExit => MessageKind is CommandLineMessageKind.Help or CommandLineMessageKind.Error;
}

internal static class CommandLineParser
{
    internal const string DefaultUrl = "http://127.0.0.1:5500";

    internal static CommandLineParseResult Parse(IReadOnlyList<string> args)
    {
        string url = DefaultUrl;
        string scriptContent = "";
        int monitor = 0;
        WallpaperContext.WallpaperMode mode = WallpaperContext.WallpaperMode.Span;
        CommandLineMessageKind messageKind = CommandLineMessageKind.None;
        string message = "";

        for (int i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--url" when i + 1 < args.Count:
                    url = args[++i].Trim();
                    if (!url.Contains("://"))
                        url = $"https://{url}";

                    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                        (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                    {
                        return Error($"Invalid URL:\n\n{url}", url, scriptContent, mode, monitor);
                    }
                    break;

                case "--script" when i + 1 < args.Count:
                {
                    string scriptPath = Path.GetFullPath(args[++i].Trim());
                    if (!File.Exists(scriptPath))
                    {
                        return Error($"Script file not found:\n\n{scriptPath}", url, scriptContent, mode, monitor);
                    }

                    scriptContent = File.ReadAllText(scriptPath);
                    break;
                }

                case "--script-content" when i + 1 < args.Count:
                    scriptContent = args[++i];
                    break;

                case "--mode" when i + 1 < args.Count:
                {
                    string modeArg = args[++i].ToLowerInvariant();
                    switch (modeArg)
                    {
                        case "single": mode = WallpaperContext.WallpaperMode.Single; break;
                        case "all": mode = WallpaperContext.WallpaperMode.All; break;
                        case "span": mode = WallpaperContext.WallpaperMode.Span; break;
                        default:
                            return Error(
                                $"Unknown wallpaper mode: {modeArg}\n\nExpected: single, all, or span.",
                                url, scriptContent, mode, monitor);
                    }
                    break;
                }

                case "--monitor" when i + 1 < args.Count:
                    if (!int.TryParse(args[++i], out int monitorNumber) || monitorNumber < 1)
                    {
                        return Error("Invalid monitor number.\n\nMonitor numbers start at 1.", url, scriptContent, mode, monitor);
                    }
                    monitor = monitorNumber - 1;
                    break;

                case "--help" or "-h":
                    return new CommandLineParseResult(
                        url, scriptContent, mode, monitor,
                        CommandLineMessageKind.Help,
                        HelpText);

                default:
                    messageKind = CommandLineMessageKind.Warning;
                    message = $"Unknown argument:\n\n{args[i]}";
                    break;
            }
        }

        return new CommandLineParseResult(url, scriptContent, mode, monitor, messageKind, message);
    }

    private static CommandLineParseResult Error(
        string message,
        string url,
        string scriptContent,
        WallpaperContext.WallpaperMode mode,
        int monitor) =>
        new(url, scriptContent, mode, monitor, CommandLineMessageKind.Error, message);

    private const string HelpText = """
        wv2wall - WebView2 desktop wallpaper

        Usage:
          wv2wall.exe [options]

        Options:

          --url <url>
              Wallpaper URL.
              Automatically adds https:// when no scheme is specified.
              Default: http://127.0.0.1:5500

          --script <path>
              Path to a JavaScript file to inject into the page.

          --script-content <js>
              JavaScript to inject directly from the command line.

          --mode <mode>
              Wallpaper mode:
                span      One wallpaper across all monitors (default)
                single    One monitor
                all       One wallpaper per monitor

          --monitor <number>
              Monitor to use in single mode, using 1-based numbering.
              Default: 1

          --help, -h
              Show this help message.

        Examples:

          wv2wall.exe
          wv2wall.exe --url https://excalidraw.com
          wv2wall.exe --url excalidraw.com --mode all
          wv2wall.exe --mode single --monitor 2
          wv2wall.exe --script sidebar.js
          wv2wall.exe --url excalidraw.com --script sidebar.js
        """;
}
