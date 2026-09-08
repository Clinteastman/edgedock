# Technology choice

EdgeDock is a Windows app, not a replacement for other desktop applications.
Its own screen combines a web view with native media controls. Users can put
an ordinary terminal or another application in front of it at any time.

## Chosen approach

**C#, WinUI 3, Windows App SDK and WebView2.** This follows the requested native
Windows direction and avoids adding a JavaScript framework or Rust just to
host a web page. .NET 10 provides a stable baseline. Exact build versions are
pinned in the project and global.json; use those rather than preview SDKs.

- WinUI provides native controls, keyboard focus and touch input.
- WebView2 displays Home Assistant as a first-party web page. Login happens
  inside that page; EdgeDock does not need a Home Assistant API token.
- Windows GlobalSystemMediaTransportControls supplies track information and
  playback actions for applications that publish a media session.
- AppWindow's FullScreen presenter is true full screen, distinct from maximising.

WPF remains a sound mature option, but WinUI fits a new Windows-native project.
Tauri is useful for cross-platform web applications, but would add a second
language and native bridging here without meeting an existing requirement.

## Deployment and validation

Start with a local development build. Prefer a signed MSIX for a future
installer: package identity is relevant to Windows capabilities including
globalMediaControl. An unpackaged prototype must report media access failures
honestly rather than pretend every player is controllable.

Use Evergreen WebView2 so security updates follow the installed Microsoft
runtime. Keep the profile under the current user's local application data.
Never ignore certificate errors or pass credentials into command-line arguments.

Physical touch, display scaling, Home Assistant authentication and media-player
compatibility require real-device testing. A successful build alone proves none
of those. There is no dependency on Corsair iCUE for the app's dashboard UI.

## Official references

- [Windows app development](https://learn.microsoft.com/en-us/windows/apps/get-started/)
- [Windows App SDK downloads](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/downloads)
- [WebView2 in WinUI 3](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/webview2)
- [WebView2 runtime distribution](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution)
- [FullScreenPresenter](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.windowing.fullscreenpresenter)
- [Windows media session manager](https://learn.microsoft.com/en-us/uwp/api/windows.media.control.globalsystemmediatransportcontrolssessionmanager)
- [App capability declarations](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/app-capability-declarations)
- [Mica material and content layering](https://learn.microsoft.com/en-us/windows/apps/design/style/mica)

Research checked September 2026. SDK release pages and package feeds can differ;
the restored package and successful build are the reproducibility authority.
