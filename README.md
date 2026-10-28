# wv2wall

`wv2wall` is a Windows Forms app that hosts a WebView2 surface behind desktop icons, allowing a web page to act as a live wallpaper.

## Requirements

- Windows 10/11
- .NET 9 SDK
- WebView2 Runtime

## Build & Test

From repository root:

```powershell
dotnet restore .\wv2wall\wv2wall.sln
dotnet build .\wv2wall\wv2wall.sln -c Release
dotnet test .\wv2wall\wv2wall.sln -c Release
```

Build output:

```text
wv2wall\bin\Release\net9.0-windows\wv2wall.exe
```

## Run

Default URL is `http://127.0.0.1:5500`:

```powershell
dotnet run --project .\wv2wall\wv2wall.csproj
```

Pass app arguments after `--`:

```powershell
dotnet run --project .\wv2wall\wv2wall.csproj -- --url https://example.com
dotnet run --project .\wv2wall\wv2wall.csproj -- --url example.com --mode all
dotnet run --project .\wv2wall\wv2wall.csproj -- --mode single --monitor 2
dotnet run --project .\wv2wall\wv2wall.csproj -- --script .\sidebar.js
dotnet run --project .\wv2wall\wv2wall.csproj -- --script-content "document.body.dataset.wallpaper = 'true';"
```

Options: `--url`, `--script`, `--script-content`, `--mode span|single|all`, `--monitor <number>`, `--help`.

Right-click tray icon for URL, monitor mode, reload, suspend, and fullscreen behavior.

## modifications

- cli parameters from `miltoncandelero`. See fork/commit [4ba511a](https://github.com/EliteDiamonHawk/PWAwall/commit/4ba511a2f10ebf0c7901446f2653a7a8e8020f14) copied from [f46e595](https://github.com/miltoncandelero/wv2wall/commit/f46e595b317003f498882752c84336a4f5082fa9)
- manual suspend/freeze
- toggle: auto suspend on fullscreen
- reload tray button

## Credits

- [Superswan](https://github.com/superswan) for upstream code
- [miltoncandelero](https://github.com/miltoncandelero) for cli paramters code

## License
MIT
- See `LICENSE` file for this repo
- See `Superswan/LICENSE` file for upstream code
