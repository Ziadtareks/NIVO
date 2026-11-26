# Third-party dependencies

The application uses these NuGet packages and their transitive dependencies. Their respective licenses apply; the portable package is not a license grant for those projects or external models.

- **DocumentFormat.OpenXml 3.3.0** — MIT. https://github.com/dotnet/Open-XML-SDK
- **PdfPig 0.1.11** — Apache-2.0. https://github.com/UglyToad/PdfPig
- **NAudio 2.2.1** — MIT. https://github.com/naudio/NAudio
- **.NET / Windows SDK projection runtime** — Microsoft and .NET project licensing. https://github.com/dotnet/runtime

External tools are not bundled:

- **OpenCode v2** — required when using OpenCode model IDs. https://opencode.ai/docs/cli/
- **Codex CLI** — required for connected ChatGPT account inference through app-server. https://developers.openai.com/codex/app-server
- **Antigravity CLI v1.5+** — required for connected Google account inference through ACP. Uses an already installed runtime (including an installed T3-managed executable when available), never bundled T3 account credentials.
- **FFmpeg** — optional video conversion. The licensing depends on the installed build. https://ffmpeg.org/legal.html
- **LibreOffice** — optional Office-to-PDF conversion. https://www.libreoffice.org/about-us/licenses/

Google OAuth/Gmail, OpenCode model providers, and user-supplied speech providers have their own terms, privacy policies, quotas, and availability. Free access today is not a promise of permanent free access. The application ships without provider secrets or Google OAuth credentials.

Design review used the Unslop skill by Damir Sokolovsky ([@heydamir](https://x.com/heydamir), [unslop](https://heydamir.design/unslop)), licensed CC BY 4.0. NIVO retains the user-requested glass direction; no AYO visual asset was copied.
