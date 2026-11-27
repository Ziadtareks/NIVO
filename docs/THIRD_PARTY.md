# Third-party notices

NIVO's own source is MIT-licensed. Third-party components retain their own licenses; NIVO's MIT grant does not relicense Microsoft SDK binaries, fonts, external tools, or provider services.

## Resolved packages and bundled components

The inventory below was read from the actual 1.9.0 single-file dependency manifest, cached NuGet `.nuspec` metadata, and the exact package/upstream license files. [The manifest](licenses/manifest.json) records metadata sources, package hashes, pinned source revisions and license-file hashes. Framework/host versions are distribution versions, not guessed component versions.

| Component | Version | License | Link to the license text |
|---|---|---|---|
| Microsoft.NETCore.App.Runtime.win-x64 | 10.0.13 | MIT; bundled third-party terms below | [Full text](licenses/Microsoft.NET-LICENSE.txt) |
| Microsoft.WindowsDesktop.App.Runtime.win-x64 | 10.0.13 | MIT; WPF/WinForms notices below | [Full text](licenses/WindowsDesktop-LICENSE.txt) |
| Microsoft.Windows.SDK.NET.Ref | 10.0.19041.57 | Microsoft Windows SDK terms (not MIT) | [Full text](licenses/WindowsSDK-License.txt) |
| DocumentFormat.OpenXml | 3.3.0 | MIT | [Full text](licenses/OpenXML.txt) |
| DocumentFormat.OpenXml.Framework | 3.3.0 | MIT | [Full text](licenses/OpenXML.txt) |
| NAudio | 2.2.1 | MIT | [Full text](licenses/NAudio.txt) |
| NAudio.Asio | 2.2.1 | MIT | [Full text](licenses/NAudio.txt) |
| NAudio.Core | 2.2.1 | MIT | [Full text](licenses/NAudio.txt) |
| NAudio.Midi | 2.2.1 | MIT | [Full text](licenses/NAudio.txt) |
| NAudio.Wasapi | 2.2.1 | MIT | [Full text](licenses/NAudio.txt) |
| NAudio.WinForms | 2.2.1 | MIT | [Full text](licenses/NAudio.txt) |
| NAudio.WinMM | 2.2.1 | MIT | [Full text](licenses/NAudio.txt) |
| PdfPig | 0.1.11 | Apache-2.0; additional BSD/Adobe notices included | [Full text](licenses/PdfPig.txt) |
| C#/WinRT (`WinRT.Runtime.dll`, supplied by the SDK package above) | 2.2.0.48161 | MIT; SDK redistribution terms also apply | [MIT](licenses/CsWinRT.txt) · [SDK terms](licenses/WindowsSDK-License.txt) |
| .NET Windows x64 apphost | 10.0.13 host pack | MIT (.NET host distribution) | [Full text](licenses/Microsoft.NET-LICENSE.txt) |
| PdfPig: PDFBox / FontBox contributions | As included in 0.1.11 | BSD-3-Clause terms | [Full text](licenses/PdfPig.txt) |
| PdfPig: Adobe Font Metrics and CMaps | As included in 0.1.11 | Adobe AFM permission / BSD-style CMap terms | [Full text](licenses/PdfPig.txt) |
| WPF: zlib | 1.2.11 in upstream notice | Zlib | [Full text](licenses/WPF-THIRD-PARTY-NOTICES.txt) |
| WPF: Json.NET | Not specified in upstream notice | MIT | [Full text](licenses/WPF-THIRD-PARTY-NOTICES.txt) |
| WinForms: Ookii.Dialogs (upstream heading: Ookie.Dialogs) | Not specified in upstream notice | BSD-3-Clause terms | [Full text](licenses/WinForms-THIRD-PARTY-NOTICES.txt) |

`Microsoft.Windows.SDK.NET.Ref` ships two assemblies used by this app: `Microsoft.Windows.SDK.NET.dll` (file version 10.0.19041.55) and `WinRT.Runtime.dll`. The package version is 10.0.19041.57. The SDK [REDIST list](licenses/WindowsSDK-REDIST.txt) explicitly permits distributing the unmodified files as part of a Windows application calling WinRT APIs, subject to the [SDK terms](licenses/WindowsSDK-License.txt). The original [RTF terms](licenses/WindowsSDK-License.rtf) are retained as well. Use of those SDK components is governed by those terms, including their downstream-use restrictions; they are not covered by NIVO's MIT grant.

## Runtime upstream-notice inventory

The unmodified runtime notice file is retained in full. Its list is intentionally a conservative superset across runtime platforms: inclusion here does not claim that every listed algorithm/library is present in this Windows binary. Individual versions are not specified unless stated in the notice; all rows below refer to the 10.0.13 runtime distribution. License descriptions are read from the supplied text; custom notices are not assigned an invented SPDX identifier.

| Component | Version | License | Link to the license text |
|---|---|---|---|
| ASP.NET | Runtime 10.0.13; individual version not specified | Apache-2.0 | [Full notice](licenses/Microsoft.NET-THIRD-PARTY-NOTICES.txt#L11) |
| Slicing-by-8 | Runtime 10.0.13; individual version not specified | BSD-style terms; see exact notice | [Full notice](licenses/Microsoft.NET-THIRD-PARTY-NOTICES.txt#L20) |
| Unicode data | Runtime 10.0.13; individual version not specified | MIT terms | [Full notice](licenses/Microsoft.NET-THIRD-PARTY-NOTICES.txt#L32) |
| zlib-ng | Runtime 10.0.13; individual version not specified | Zlib-style permission notice | [Full notice](licenses/Microsoft.NET-THIRD-PARTY-NOTICES.txt#L68) |
| opentelemetry-dotnet | Runtime 10.0.13; individual version not specified | Apache-2.0 | [Full notice](licenses/Microsoft.NET-THIRD-PARTY-NOTICES.txt#L93) |
| LinuxTracepoints | Runtime 10.0.13; individual version not specified | MIT terms | [Full notice](licenses/Microsoft.NET-THIRD-PARTY-NOTICES.txt#L105) |
| Mono | Runtime 10.0.13; individual version not specified | MIT terms | [Full notice](licenses/Microsoft.NET-THIRD-PARTY-NOTICES.txt#L132) |
| International Organization for Standardization | Runtime 10.0.13; individual version not specified | Custom permission notice; see full text | [Full notice](licenses/Microsoft.NET-THIRD-PARTY-NOTICES.txt#L159) |
| Intel | Runtime 10.0.13; individual version not specified | BSD-style terms; see exact notice | [Full notice](licenses/Microsoft.NET-THIRD-PARTY-NOTICES.txt#L167) |
| Xamarin and Novell | Runtime 10.0.13; individual version not specified | MIT terms | [Full notice](licenses/Microsoft.NET-THIRD-PARTY-NOTICES.txt#L193) |
| Bit Twiddling Hacks | Runtime 10.0.13; individual version not specified | Public-domain statement (full notice retained) | [Full notice](licenses/Microsoft.NET-THIRD-PARTY-NOTICES.txt#L253) |
| Brotli | Runtime 10.0.13; individual version not specified | MIT terms | [Full notice](licenses/Microsoft.NET-THIRD-PARTY-NOTICES.txt#L268) |
| Json.NET | Runtime 10.0.13; individual version not specified | MIT terms | [Full notice](licenses/Microsoft.NET-THIRD-PARTY-NOTICES.txt#L350) |
| vectorized base64 encoding / decoding | Runtime 10.0.13; individual version not specified | BSD-style terms; see exact notice | [Full notice](licenses/Microsoft.NET-THIRD-PARTY-NOTICES.txt#L376) |
| vectorized hex parsing | Runtime 10.0.13; individual version not specified | BSD-style terms; see exact notice | [Full notice](licenses/Microsoft.NET-THIRD-PARTY-NOTICES.txt#L408) |
| RFC 3492 | Runtime 10.0.13; individual version not specified | Custom permission notice; see full text | [Full notice](licenses/Microsoft.NET-THIRD-PARTY-NOTICES.txt#L438) |
| Algorithm from RFC 4122 - | Runtime 10.0.13; individual version not specified | Custom permission notice; see full text | [Full notice](licenses/Microsoft.NET-THIRD-PARTY-NOTICES.txt#L493) |
| The LLVM Compiler Infrastructure | Runtime 10.0.13; individual version not specified | NCSA / University of Illinois terms | [Full notice](licenses/Microsoft.NET-THIRD-PARTY-NOTICES.txt#L514) |
| Bob Jenkins | Runtime 10.0.13; individual version not specified | Custom permission notice; see full text | [Full notice](licenses/Microsoft.NET-THIRD-PARTY-NOTICES.txt#L552) |
| Greg Parker | Runtime 10.0.13; individual version not specified | Public-domain statement (full notice retained) | [Full notice](licenses/Microsoft.NET-THIRD-PARTY-NOTICES.txt#L558) |
| libunwind based code | Runtime 10.0.13; individual version not specified | MIT terms | [Full notice](licenses/Microsoft.NET-THIRD-PARTY-NOTICES.txt#L565) |
| Printing Floating-Point Numbers (Dragon4) | Runtime 10.0.13; individual version not specified | Zlib-style permission notice | [Full notice](licenses/Microsoft.NET-THIRD-PARTY-NOTICES.txt#L587) |
| Printing Floating-point Numbers (Grisu3) | Runtime 10.0.13; individual version not specified | BSD-style terms; see exact notice | [Full notice](licenses/Microsoft.NET-THIRD-PARTY-NOTICES.txt#L614) |
| xxHash | Runtime 10.0.13; individual version not specified | BSD-style terms; see exact notice | [Full notice](licenses/Microsoft.NET-THIRD-PARTY-NOTICES.txt#L644) |
| Berkeley SoftFloat Release 3e | Runtime 10.0.13; individual version not specified | BSD-3-Clause terms | [Full notice](licenses/Microsoft.NET-THIRD-PARTY-NOTICES.txt#L680) |
| xoshiro RNGs | Runtime 10.0.13; individual version not specified | CC0 / public-domain dedication | [Full notice](licenses/Microsoft.NET-THIRD-PARTY-NOTICES.txt#L722) |
| fastmod | Runtime 10.0.13; individual version not specified | Apache-2.0 | [Full notice](licenses/Microsoft.NET-THIRD-PARTY-NOTICES.txt#L733) |
| sse4-strstr | Runtime 10.0.13; individual version not specified | BSD-style terms; see exact notice | [Full notice](licenses/Microsoft.NET-THIRD-PARTY-NOTICES.txt#L750) |
| The C++ REST SDK | Runtime 10.0.13; individual version not specified | MIT terms | [Full notice](licenses/Microsoft.NET-THIRD-PARTY-NOTICES.txt#L779) |
| MessagePack-CSharp | Runtime 10.0.13; individual version not specified | MIT terms | [Full notice](licenses/Microsoft.NET-THIRD-PARTY-NOTICES.txt#L808) |
| lz4net | Runtime 10.0.13; individual version not specified | BSD-style terms; see exact notice | [Full notice](licenses/Microsoft.NET-THIRD-PARTY-NOTICES.txt#L835) |
| Nerdbank.Streams | Runtime 10.0.13; individual version not specified | MIT terms | [Full notice](licenses/Microsoft.NET-THIRD-PARTY-NOTICES.txt#L852) |
| RapidJSON | Runtime 10.0.13; individual version not specified | MIT terms | [Full notice](licenses/Microsoft.NET-THIRD-PARTY-NOTICES.txt#L877) |
| DirectX Math Library | Runtime 10.0.13; individual version not specified | MIT terms | [Full notice](licenses/Microsoft.NET-THIRD-PARTY-NOTICES.txt#L894) |
| ldap4net | Runtime 10.0.13; individual version not specified | MIT terms | [Full notice](licenses/Microsoft.NET-THIRD-PARTY-NOTICES.txt#L920) |
| vectorized sorting code | Runtime 10.0.13; individual version not specified | MIT terms | [Full notice](licenses/Microsoft.NET-THIRD-PARTY-NOTICES.txt#L933) |
| musl | Runtime 10.0.13; individual version not specified | MIT terms | [Full notice](licenses/Microsoft.NET-THIRD-PARTY-NOTICES.txt#L958) |
| "Faster Unsigned Division by Constants" | Runtime 10.0.13; individual version not specified | Public-domain statement (full notice retained) | [Full notice](licenses/Microsoft.NET-THIRD-PARTY-NOTICES.txt#L985) |
| mimalloc | Runtime 10.0.13; individual version not specified | MIT terms | [Full notice](licenses/Microsoft.NET-THIRD-PARTY-NOTICES.txt#L995) |
| remote stack unwind | Runtime 10.0.13; individual version not specified | Apache-2.0 with LLVM exception | [Full notice](licenses/Microsoft.NET-THIRD-PARTY-NOTICES.txt#L1020) |
| Apple header files | Runtime 10.0.13; individual version not specified | BSD-style terms; see exact notice | [Full notice](licenses/Microsoft.NET-THIRD-PARTY-NOTICES.txt#L1037) |
| JavaScript queues | Runtime 10.0.13; individual version not specified | CC0 / public-domain dedication | [Full notice](licenses/Microsoft.NET-THIRD-PARTY-NOTICES.txt#L1071) |
| FastFloat algorithm | Runtime 10.0.13; individual version not specified | MIT terms | [Full notice](licenses/Microsoft.NET-THIRD-PARTY-NOTICES.txt#L1098) |
| MsQuic | Runtime 10.0.13; individual version not specified | MIT terms | [Full notice](licenses/Microsoft.NET-THIRD-PARTY-NOTICES.txt#L1118) |
| m-ou-se/floatconv | Runtime 10.0.13; individual version not specified | BSD-style terms; see exact notice | [Full notice](licenses/Microsoft.NET-THIRD-PARTY-NOTICES.txt#L1127) |
| code from The Practice of Programming | Runtime 10.0.13; individual version not specified | Custom permission notice; see full text | [Full notice](licenses/Microsoft.NET-THIRD-PARTY-NOTICES.txt#L1153) |
| amd/aocl-libm-ose | Runtime 10.0.13; individual version not specified | BSD-style terms; see exact notice | [Full notice](licenses/Microsoft.NET-THIRD-PARTY-NOTICES.txt#L1170) |
| fmtlib/fmt | Runtime 10.0.13; individual version not specified | MIT terms | [Full notice](licenses/Microsoft.NET-THIRD-PARTY-NOTICES.txt#L1197) |
| Jb Evain | Runtime 10.0.13; individual version not specified | MIT terms | [Full notice](licenses/Microsoft.NET-THIRD-PARTY-NOTICES.txt#L1222) |
| MurmurHash3 | Runtime 10.0.13; individual version not specified | Custom permission notice; see full text | [Full notice](licenses/Microsoft.NET-THIRD-PARTY-NOTICES.txt#L1253) |
| Fast CRC Computation | Runtime 10.0.13; individual version not specified | BSD-style terms; see exact notice | [Full notice](licenses/Microsoft.NET-THIRD-PARTY-NOTICES.txt#L1261) |
| C# Implementation of Fast CRC Computation | Runtime 10.0.13; individual version not specified | Apache-2.0 | [Full notice](licenses/Microsoft.NET-THIRD-PARTY-NOTICES.txt#L1294) |
| the Teddy multi-substring searching implementation | Runtime 10.0.13; individual version not specified | MIT terms | [Full notice](licenses/Microsoft.NET-THIRD-PARTY-NOTICES.txt#L1305) |
| Avx512Vbmi base64 encoding / decoding | Runtime 10.0.13; individual version not specified | BSD-style terms; see exact notice | [Full notice](licenses/Microsoft.NET-THIRD-PARTY-NOTICES.txt#L1332) |
| FormatJS Intl.Segmenter grapheme segmentation algorithm | Runtime 10.0.13; individual version not specified | MIT terms | [Full notice](licenses/Microsoft.NET-THIRD-PARTY-NOTICES.txt#L1366) |
| SharpFuzz and related samples | Runtime 10.0.13; individual version not specified | MIT terms | [Full notice](licenses/Microsoft.NET-THIRD-PARTY-NOTICES.txt#L1380) |
| National Institute of Standards and Technology ACVP Data | Runtime 10.0.13; individual version not specified | Custom permission notice; see full text | [Full notice](licenses/Microsoft.NET-THIRD-PARTY-NOTICES.txt#L1409) |

## Redistribution checklist

- MIT copyright/permission notices and the full PdfPig license, including its PDFBox/FontBox and Adobe terms, are included. No changes were made to upstream library source or binaries.
- The complete .NET, WPF and WinForms third-party notices are retained, rather than replacing them with a generic MIT label.
- This product includes software developed by the University of California, Berkeley and its contributors. This acknowledgment accompanies the retained BSD advertising clause in the runtime notices.
- No GPL/LGPL/MPL dependency or other copyleft license was identified in the resolved shipped-package metadata and supplied notice texts. No copyleft-specific dynamic-linking accommodation was therefore introduced. Separate, unmodified managed/native libraries are preserved by single-file bundling; packaging does not change their licenses.
- All full license texts, the NIVO MIT license and a consolidated readable notice are embedded as resources. A readable `NIVO.THIRD_PARTY_NOTICES.txt` and `LICENSE` also accompany publish output. Ship those notices with the EXE; embedding is not used as a substitute for a readable copy.
- The already-published original 1.9.0 EXE predates the expanded runtime/SDK notices. Source changes do not retroactively alter that asset. Redistribution of that original EXE requires the readable companion notices from this audit. The separately validated local repack additionally embeds the expanded notices. No replacement EXE or new release is published by this pass.

## External integrations and assets

OpenCode, Codex CLI, Antigravity CLI, FFmpeg and LibreOffice are not bundled. Their installations, licenses, account permissions and provider terms are separate. Windows supplies the system fonts and glyphs used at runtime; no font or sound files are redistributed in `assets/` or `docs/images/`. Those directories contain original NIVO vector/icon artwork and NIVO UI renders made with synthetic data.

Development design review used Unslop by Damir Sokolovsky ([project](https://heydamir.design/unslop)), credited under CC BY 4.0. The skill itself is not shipped in NIVO.
