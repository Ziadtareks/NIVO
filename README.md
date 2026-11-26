# NIVO

Your Windows desktop companion: a floating dock, a smart island, and AI help for everyday tasks. Available in English and Arabic.

## Download

**[Download NIVO.exe for Windows](https://github.com/Ziadtareks/NIVO/releases/latest/download/NIVO.exe)**

1. Download **NIVO.exe** and keep it in a permanent folder.
2. Double-click to open it. No build tools or .NET installation needed.
3. Open **Settings → AI & accounts** to connect your AI provider.

Requires Windows 10 version 2004 or newer, or Windows 11 (64-bit). The app includes .NET; AI features use a supported installed provider runtime and your account. Provider availability and quotas apply.

## What it does

- Floating dock with animated icons, focus profiles, and seven themes including OLED black.
- AI chat, screen questions, clipboard summaries, translations, and rewrites.
- Connected Codex/ChatGPT and Antigravity/Google accounts.
- Reviewed Windows tasks, local memory, routines, and document creation.
- PDF-to-Word conversion; optional video/Office conversions with compatible tools installed.
- Automatic hiding during fullscreen apps and recognized games.

## Start with Windows

Open **Settings → Desktop**, enable **Start NIVO with Windows**, then click **Save preferences**. Turn it off in the same place to disable it. No Administrator access required. If you move the executable, turn this option off and save, then enable it again to update its location.

## Voice

Talk uses the available connected account. Codex voice is experimental and depends on account access. Antigravity opens its native window: press **F6** there to begin voice. Press Talk again to stop. Custom speech APIs are optional.

## Your data

History and account profiles stay under `%LOCALAPPDATA%\NIVO.Local`. Prompts and captures are sent to your selected provider. Computer actions require reviewing the proposed plan; stopping them does not undo completed changes.

NIVO is an independent project inspired by AYO's public features. Live voice and physical desktop behavior are still being tested. See [features](docs/FEATURES.md), [known limits](docs/VERIFICATION.md), and [third-party notices](docs/THIRD_PARTY.md).
