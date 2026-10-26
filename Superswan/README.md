# wv2wall

`wv2wall` is a Windows Forms app that hosts a WebView2 surface *behind* desktop icons (attached to `WorkerW/Progman`) so a web page can act like a live wallpaper. It includes a tray menu for URL + monitor mode selection, and low-level input hooks to forward mouse/keyboard to the WebView (scrolling works!) while preserving normal desktop behavior (desktop icons + right-click menu). 


## Requirements

- Windows 10/11
- .NET (SDK that supports WinForms; typically .NET 6+)
- WebView2 Runtime installed (usually already present on modern Windows)

NuGet:
- `Microsoft.Web.WebView2`

## Build

From the project directory:

```powershell
dotnet restore
dotnet build -c Release
```

## Run

Default startup loads:

- `http://127.0.0.1:5500`

Run:

```powershell
dotnet run
```

Or run the built executable from `bin\Release\...`.

## Use

- Launch the app → it appears in the **system tray**.
- Right-click the tray icon:
  - **Set URL...** to point at:
    - a local dev server (recommended)
    - a hosted page
  - Pick a mode:
    - **Span**: one surface across the entire virtual screen
    - **All monitors**: one surface per monitor
    - **Monitor N**: single monitor wallpaper
  - Use **Reload WebView2** to reload all active wallpaper surfaces.

Keyboard focus behavior:
- Clicking the desktop background (not an icon) sets “wallpaper focused” and forwards keys to the web app.
- Clicking normal windows clears “wallpaper focused”.

Mouse behavior:
- Desktop **right-click** remains the normal Windows desktop menu.
- Clicking on desktop icons behaves normally.
- Clicking elsewhere on the desktop forwards input to the wallpaper WebView surface.
- Mouse wheel is forwarded via WebView2 DevTools protocol (`Input.dispatchMouseEvent`) for consistent scrolling.

## How it works (high level)

- `WallpaperContext` (`ApplicationContext`)
  - Owns the tray icon/menu and the list of `DeskForm` instances
  - Manages monitor mode and recreates forms when switching modes
  - Installs:
    - `WH_MOUSE_LL` to route mouse events to the correct WebView region
    - `WH_KEYBOARD_LL` to route keyboard events when wallpaper is focused
  - Uses desktop ListView memory ops to clear selection (deselect icons) when clicking background

- `DeskForm`
  - Borderless WinForms window with a `WebView2` control docked to fill
  - Reparents itself to the desktop host via `WorkerW.FindDesktopHost()`
  - Navigates to `_url`
  - Exposes helpers:
    - `GetWebViewHandle()` to find `Chrome_RenderWidgetHostHWND`
    - `DispatchScroll()` for wheel input
    - `PostKey()` to forward key messages to the WebView window handle

## Notes / limitations

- This is Windows Shell–dependent behavior (Progman/WorkerW) and can vary across Windows builds/themes.
- Low-level hooks apply system-wide while the app runs. Bugs in hook logic can affect input globally.
- Some web pages may behave poorly as a wallpaper (heavy GPU, autoplay policies, focus issues).
- WebView2 DevTools input dispatch is used for wheel; if DevTools are disabled at runtime in future changes, wheel forwarding would need an alternative.


## License
MIT
See `LICENSE` file
