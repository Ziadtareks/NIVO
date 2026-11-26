# NIVO

A native Windows desktop companion with a floating dock, an interactive island, and AI assistance through OpenCode or connected Codex and Antigravity accounts. Built with C# and WPF.

## Features

- Wallpaper-tinted dock, seven themes including OLED, animated app magnification, and focus profiles.
- Text chat, screen capture, point-and-ask, and clipboard summaries, translations, and rewrites.
- Separate Codex/ChatGPT and Antigravity/Google sign-in with dynamic model discovery and reported image capabilities.
- Reviewed Windows tasks: inspect the proposed commands before they run with your current Windows permissions.
- Local memory, routines, document creation, PDF-to-Word conversion, and optional Office/video conversion.
- English and Arabic interfaces, with automatic hiding during foreground fullscreen apps and recognized games.

## Requirements

- Windows 10 version 2004 or newer, or Windows 11, x64.
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) to build from source.
- An installed CLI for your chosen backend: [OpenCode](https://opencode.ai), [Codex](https://developers.openai.com/codex/), or Antigravity v1.5+ with ACP support.
- Optional: FFmpeg for video conversion and LibreOffice for Office-to-PDF conversion.

Provider CLIs are not bundled. Account access, model availability, and quotas depend on the provider; NIVO includes no paid subscription or automatic paid fallback.

## Build and run

```powershell
git clone https://github.com/Ziadtareks/NIVO.git
cd NIVO
./scripts/build.ps1 -SelfContained
./scripts/run.ps1
```

The build script runs the account, assistant, computer-command, and motion test suites in separate processes. It then creates a portable application in `artifacts/NIVO-win-x64/` and a ZIP beside it. Omit `-SelfContained` if the target computer already has the .NET 10 Desktop Runtime.

Open **Settings → AI & accounts** to choose OpenCode models or connect an account. Configure **Settings → Voice** separately. Optional installation for the current Windows user:

```powershell
./scripts/install.ps1
```

## Voice

**Auto** prefers the most recently connected account, independently of the saved text model.

- **Codex:** experimental native realtime voice inside NIVO, subject to account availability.
- **Antigravity:** opens its original CLI window using NIVO's connected account. Press **F6** or enter `/voice live` there. Transcripts remain in that window.
- **Custom speech APIs:** optional user-configured STT/TTS endpoints.

Press Talk again to stop. Native account voice has no computer-action tools. Physical microphone/speaker behavior and live voice entitlement have not been fully verified.

## Privacy and limitations

Settings, history, and isolated account profiles are stored under `%LOCALAPPDATA%\NIVO.Local`. Saved API keys use Windows user-scoped DPAPI encryption. Chats are local; prompts, attachments, and selected captures are sent to the chosen provider. Existing T3 Code credentials are not copied.

Computer access is opt-in for new profiles and requires reviewing each execution plan. Commands use NIVO's current permissions; Administrator access is never automatic. Stopping a task does not undo completed changes.

NIVO is an independent implementation inspired by AYO's public feature set. It is not AYO, does not include its proprietary code or assets, and does not claim complete feature or visual parity. Scanned PDFs are preserved as page images rather than editable OCR text.

See [features](docs/FEATURES.md), [verification and known limits](docs/VERIFICATION.md), and [third-party notices](docs/THIRD_PARTY.md). The legacy combined WPF harness is available through `-FullSelfTest`; its teardown can hang. Physical desktop interaction tests require `-DesktopAcceptance` and an idle interactive desktop.
