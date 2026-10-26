# Contributing

Thank you for helping improve Star Rail Companion. The app is a Windows x64
desktop companion built with .NET 8, WPF, C#, and Microsoft WebView2.

## Before opening a pull request

- Search existing issues before reporting a bug or proposing a feature.
- Keep changes focused and preserve the app's read-only interaction with the
  game cache.
- Never log or persist Warp-history links, HoYoLAB cookies, or other
  authorization data.
- Build the application and installer with no compiler warnings:

  ```powershell
  dotnet build src\StarRailCompanion.csproj -c Release
  dotnet build Setup\StarRailCompanionSetup.csproj -c Release
  ```

- Describe what changed and how it was validated in your pull request.

## Privacy and safety

The app must not interact with the active game process, modify game files, or
send a player's authorization data to unrelated services. Cache discovery and
Warp syncing must remain local/read-only except for the official game API
requests that the user initiates.
