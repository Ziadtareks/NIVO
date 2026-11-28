# Verification — 2026-11-28

Version 1.11.0 includes first-run OpenCode discovery, per-turn Talk language preferences and updated Island file handling. A fresh isolated profile selected and persisted an available free model from the installed CLI. Routed drag-and-drop checks cover nested controls and source preservation. Physical Explorer dragging, microphone playback and live provider responses remain unverified for this build.

## Build and local checks

### Version 1.10.0 — Talk inside NIVO

The release review reproduced premature readiness: the native CLI paints an empty `? for shortcuts` prompt before its model/backend finishes initializing. The new handshake requires a complete model-status footer, including when terminal output splits the footer across reads. A failing regression test was observed before the fix. A real isolated CLI 1.5.18 probe then reached the native audio connection stage instead of the earlier Live-unavailable response. It used `ANTIGRAVITY_MIC=127.0.0.1:1` without any microphone server, so the expected result was an explicit cannot-reach-mic-server refusal; no audio hardware or model prompt was used. This is evidence of startup progress, not a successful live audio round trip or proof of voice entitlement.

Audio-bridge refusal now fails promptly with a specific diagnostic; model catalogs explicitly leave voice availability unverified. Reminders and proactive suggestions defer while account Talk connects or runs. Native voice remains experimental and depends on provider terminal markers and runtime compatibility.

User acceptance reported `Live mode isn't available in this session` from the real Antigravity runtime. Hidden terminal hosting works, but native Live voice is not operational in that reported session. Provider documentation lists session restrictions and account availability separately from microphone errors. NIVO now preserves this distinction in its error, without substituting an API or another account. This report motivated the readiness fix above; full physical audio acceptance is still unverified. The earlier prompt-only probe was not a voice availability test.

Release validation passes 213 focused checks with normal exit code 0: 84 account checks (`artifacts/accounts-20261128-030252/results.txt`), 51 assistant checks (`artifacts/assistant-20261128-030327/results.txt`), 25 computer checks (`artifacts/computer-20261128-030332/results.txt`), and 53 motion checks (`artifacts/motion-20261128-030344/results.txt`). Builds report zero warnings/errors. The NuGet vulnerability query against nuget.org reports no vulnerable resolved packages. The 13 bundled dependency/runtime versions match the previous audited distribution, so its full license/notice texts remain applicable. No visual assets were replaced in this round. These deterministic checks do not prove live account voice entitlement or physical audio quality.

Antigravity voice now runs in a private Windows pseudoconsole with no terminal window. NIVO waits for the authenticated prompt, dismisses only informational startup overlays, sends the documented `/voice live` command automatically, and reports a connected session only after the provider's `Started voice chat` marker. Talk again or Escape cancels/ends it. The Island and composer expose Stop Talk while connecting or active. Provider voice-end events close the hidden runtime and audio bridge.

Real installed Antigravity 1.5.18 was probed in a private terminal without a voice command, microphone server or model prompt: unauthenticated and authenticated prompt rendering work; the linked account reaches the actual `? for shortcuts` prompt. No raw token was read/copied and no physical audio or voice inference was sent. The voice transport is experimental and depends on terminal UI markers, account entitlement and audio permissions. Antigravity transcripts/context are not mirrored into NIVO chat. Its native history makes that route unavailable in Incognito; Codex retains its ephemeral inline route.

Process-boundary fixtures verify terminal identity with Console input/output not redirected and its console HWND invisible, exact automatic voice activation, account refusal, provider-ended sessions, cancellation, process-tree cleanup, rapid restarts, decoded split terminal controls, and Stop Talk controls in NIVO. These tests use no hardware or live model. No API voice fallback is introduced.

### Version 1.9.0 — standalone download and Windows startup

The GitHub release supplies one self-contained Windows x64 `NIVO.exe`, including .NET and native dependencies. Third-party license texts are embedded. The README starts with the direct download and ordinary usage, without development/build instructions.

Settings → Desktop exposes a Start NIVO with Windows toggle. It creates/removes only the current user's `NIVO.lnk` Startup shortcut and requires no elevation. Windows Startup state is the source of truth; unrelated preferences saves do not change it. The shortcut stores the exact current executable and working folder, with no additional arguments. Tests create real `.lnk` files only in isolated temporary folders, cover spaces in paths and safe removal, and render the toggle in Arabic and English.

Regression testing also exposed a transient Windows lock during native voice-session cleanup. Cleanup now retries only sharing/lock/directory-not-empty IO errors with bounded delays, preserving the exact temporary-path and junction protections. A synthetic locked-file check and repeated immediate start/stop checks cover this behavior. Real Windows sign-in/reboot and live physical audio are not exercised. The exact renamed standalone EXE passed all four suites with installed-runtime lookup disabled: accounts/startup 69, assistant 51, computer commands 25, motion 53 (198 total). It also started, responded, exited normally with code 0, and relaunched from the Desktop with no adjacent DLLs or configuration files.

### Public repository preparation — 2026-11-27

A fresh Release build and self-contained publish passed. The default build script now runs four isolated suites: accounts (63), assistant (51), computer commands (25), and motion/themes (53), all exiting 0 (192 checks total). The legacy combined WPF harness remains opt-in through `-FullSelfTest` because its teardown can hang; these focused suites do not claim to replace all of its additional coverage. Physical desktop and live audio acceptance remain separate.

NuGet's vulnerability query reported no vulnerable direct or transitive packages for the configured feed at the time of the check. A staged-source credential/personal-path scan found no matches; runtime account profiles, secrets, local data, and generated artifacts are excluded from Git. This is bounded verification, not a guarantee that no bugs exist.

A new Neon review was requested but could not start: T3 reported its managed Antigravity executable was unavailable/changed and required reinstalling. Earlier Neon review findings were addressed in the preceding release; no new Neon approval is claimed for this publication.

### Version 1.8.0 automatic voice account and native Antigravity window

Release build succeeds with zero warnings/errors. The published 63-check account suite exits 0 (`artifacts/accounts-20261126-223714/results.txt`), including account priority and fallback, preserving the text-model preference, strict isolation, cancellation, and synthetic external voice CLI/bridge start/stop/crash/normal-close tests. Temporary NTFS junction cleanup preserves the synthetic account target. Neon identified monitor/shutdown, startup-cancellation and version-stream lifecycle races; these were fixed, and regression checks cover repeated immediate cancellation, failed version exit and cancelled version-process cleanup. No physical microphone, speaker, real voice inference or visible CLI window was used by these tests.

Regression suites exit 0: 51 assistant checks (`artifacts/assistant-20261126-223115/results.txt`) and 53 motion/theme checks (`artifacts/motion-20261126-223232/results.txt`). A metadata-only probe using the user's already linked account resolved `antigravity/gemini-4-neon` as the actual default, without microphone access or model inference. The user confirms Google sign-in works.

Antigravity voice uses the original interactive CLI in an external window; press F6 or `/voice live` there. ACP itself has no audio transport. NIVO does not inject its chat context/system prompt into that interactive window or receive its transcripts. Account voice entitlement, actual console presentation, physical audio and acoustic quality still require user interaction. Codex retains its experimental inline route. No paid API fallback or token copying is introduced. Hardware and live-provider acceptance testing remains incomplete.

### Version 1.7.0 native account voice and auth repair

Release build passes with zero warnings/errors. The 44-check account suite exits 0 (`artifacts/accounts-20261126-220913/results.txt`): Google code-login subprocess and fresh-process persistence, callback state/origin/newline validation, account inference/images, native Talk started-before-capture, real PCM payload transport through an injected device, transcripts, Stop, callback device faults, cleanup failure, oversized transport faults, runtime button-mode changes, and AR/EN settings rendering. No physical audio device or live model was used.

Native unauthenticated/pre-browser probes: 5 checks, exit 0 (`artifacts/account-native-20261126-220957/results.txt`), including Codex voice auth refusal before audio activation and real agy producing a validated Google OAuth URL then cancelling without opening a browser or sending credentials. Regressions: 51 assistant checks (`artifacts/assistant-20261126-220647/results.txt`), 25 computer checks (`artifacts/computer-20261126-220652/results.txt`), and 53 motion/theme checks (`artifacts/motion-20261126-220704/results.txt`), all exit 0. OLED rendering was inspected; its Dock glass is opaque black while other themes retain 0.78 glass opacity.

Neon additionally reports 23 scratch native-voice checks and six disposable real-agy pre-browser/auth-isolation checks. The user reports Codex account chat works. This is not an authenticated voice/mic/speaker acceptance test: live Codex voice entitlement and acoustic behavior, and a complete user Google sign-in remain pending interaction. Native Talk has no computer-action tools; ordinary reviewed text tasks retain computer access. Hardware and live-provider acceptance is not claimed complete.


### Version 1.6.0 connected-account verification

Release builds pass with zero warnings/errors. Account suite: 30 checks, clean exit 0 (`artifacts/accounts-20261126-212115/results.txt`). These cover browser-login immediate-notification races, untrusted login URL rejection, inherited API credential exclusion, actual image attachment blocks, paginated model discovery, reported/unknown image capabilities, Codex and ACP inference routing/streaming, WPF-thread delivery, failed/incomplete turns, Stop, native permission denial, grouped catalogs, logout and Arabic/English settings rendering at 850px. Rendered account settings were inspected. The deterministic fixtures do not verify a provider subscription or actual model quality.

Real installed Codex and Antigravity executables initialize against new isolated profiles and reject unauthenticated model requests without opening a browser (`artifacts/account-native-20261126-211523/results.txt`, exit 0). No existing T3/CLI credential files were read or imported, no login was initiated and no live inference was sent. Completed user sign-in and authenticated live model use remain unverified.

Regression suites: 51 assistant checks (`artifacts/assistant-20261126-211645/results.txt`) and 25 computer checks (`artifacts/computer-20261126-211711/results.txt`), both clean exit 0. No animation implementation changed in this round; prior motion evidence remains applicable.

Account tokens are managed by the native CLI in NIVO-only provider profiles under local app data. Inherited API credentials are excluded from account processes. Codex tool features are disabled and approval requests rejected; Antigravity uses deny rules, empty workspaces, disabled client file/terminal capabilities and cancellation on tool attempts. Antigravity does not expose a schema-removal switch, so this is runtime enforcement rather than an assertion that tool schemas are absent. User scripts still pass through NIVO's existing plan review.

### Version 1.5.0 real computer access and motion

Release build and self-contained x64 publish passed with zero warnings/errors. Published suites exited normally with code 0: 25 computer-execution checks (`artifacts/computer-20261126-201058/results.txt`), 51 assistant/policy checks (`artifacts/assistant-20261126-201110/results.txt`) and 51 motion checks (`artifacts/motion-20261126-201115/results.txt`). The broad run completed all 270 core assertions (`artifacts/test-20261126-200019/results.txt`) plus its nested suites, then hit the already-known native teardown timeout after Application.Exit at 180 seconds. This is not a clean broad-suite process exit.

The computer suite runs actual Windows PowerShell against scratch files/processes: Unicode/quoted paths, native nonzero exit, native informational stderr with exit 0, top-level namespace syntax, script failure, output bounds, Stop/child termination/deadline, action-free replies, real post-review writes, denied/revoked access, partial-failure history, local Memory and read-only transforms. No live AI, private-file operations, elevation or real clipboard/physical input was used. The fixture validates host protocol rather than a live model's reliability.

The render burst of 120 moves creates zero new clocks, allocating 40 bytes in English and 400 bytes in Arabic on this run; elapsed hot-path times were 0.2547 ms / 0.0235 ms. The earlier recorded baseline was ~1,979 clocks, ~6.8 MB and ~175 ms. These are synthetic input/CPU-allocation observations, not a physical-display FPS measurement. Island opening still records one container-size change and two finite content-entrance clocks; it releases its morph participant on hide. Mid-morph output was inspected. High-refresh/mixed-DPI pointer replay remains unrun.

Computer access remains opt-in for new profiles; it is enabled for the requesting user at delivery. Commands use current Windows rights, explicit full-script review and resolved working directories, bounded execution and observation. No Windows-wide permissions/animation/execution-policy setting is altered. Administrator rights, installation success and arbitrary GUI-control success are not inferred from a plan or a zero-code parent launch.

### Version 1.4.0 evidence (historical)


### Version 1.4.0 Windows assistance, bug fixes and fullscreen policy

Release build and self-contained Windows x64 publish succeeded with zero warnings/errors. All 270 core assertions completed in `artifacts/test-20261126-015502/results.txt`. Published focused runs completed normally with exit code 0: 51 assistant/AI/task/shell-policy assertions at `artifacts/assistant-20261126-015750/results.txt`, and 46 motion assertions at `artifacts/motion-20261126-015755/results.txt`.

New coverage includes actual bounded OS/time metadata, policy delivery to the CLI, untrusted-context separation, question-only plans and follow-ups, invalid source paths, truncated-source exclusion, Incognito Workspace guards, invariant reminder dates, empty-chunk retry/fallback, typed diagnostic JSON, pre-stream size limits, confirmation re-entry, a real locked temporary PNG, scanned/large-PDF metadata, unrelated clipboard draft preservation, fullscreen/maximized distinction, negative/high-DPI monitors, auto-hide taskbars, manual/windowed-game suppression, capture guards and game-mode routing on return. Fixtures dispatch on the current request rather than old markers in conversation history. No live AI, real clipboard or physical pointer operations were performed.

**Full-run limitation remains:** although all core and nested assertions completed and Application.Exit was logged, the complete process exceeded its 125-second teardown deadline and was stopped. This is not a clean full-suite process exit. Focused published suites exit 0. Physical game/mixed-DPI acceptance remains deferred. The opt-in native harness was adapted to switch its target between fullscreen and windowed stages, but was not run here. Unknown windowed games require manual Gaming mode; foreground fullscreen coverage does not require a known game name.

### Version 1.3.1 evidence (historical)


### Version 1.3.1 Island stutter fix

Release build passed with zero warnings/errors. The pre-fix motion regression failed and recorded nine container-size changes during opening. After separating destination layout from render-only glass/clip transforms, the same trace recorded one. The shadow now belongs only to a cached glass layer; panel text is not scaled. Header alignment and the bottom light follow the visible edges.

All 46 focused motion checks exited normally with code 0 at `artifacts/motion-20261125-235842/results.txt`, including opening/closing layout bounds, immediate state changes, rapid reversal continuity, content recovery and settled clocks. Mid-transition images were reviewed. All 270 core assertions completed at `artifacts/test-20261125-235639/results.txt`; its nested motion run passed 45 checks before one further header-geometry check was added. The already-known full-suite teardown issue remains: this runner exceeded its 125-second process deadline after Application.Exit, and was stopped. The focused runner exited normally. Physical pointer replay remains deferred; no live AI or real clipboard operations were used in this round.

The final self-contained v1.3.1 executable passed all 46 motion checks and exited 0 at `artifacts/motion-20261125-235948/results.txt`. Its opening trace still records one container-size change. Normal published launch responded, and its quit signal exited 0 before the desktop update.

### Version 1.3.0 evidence (historical)


The v1.3.0 Release build and Windows x64 self-contained publish succeeded with zero warnings/errors. All 270 core assertions completed at `artifacts/test-20261125-233543/results.txt`; the nested motion suite completed another 42 at `artifacts/motion-20261125-233627/results.txt`.

**Runner limitation:** the full-suite process logged Application.Exit after closing its windows, but did not terminate within 145 seconds and was stopped. This is not a clean full-suite process pass. A software-rendering diagnostic also failed to terminate, and one software-rendered timed hover assertion failed; no software-rendering override is shipped. The isolated motion run exited normally with code 0. Repeating all 42 motion checks against the self-contained published executable also exited 0 (`artifacts/motion-20261125-233732/results.txt`). The normal published application opened, responded, and exited through its quit signal with code 0. The complete-run teardown cause remains unverified; it must not be hidden by forced application exit.

Motion-on coverage includes interruptible sizing, committed resting values, transform preservation, same-target deduplication, visible-only activity clocks, rapid Island collapse/reopen, flush completion lighting, bilingual 1280-DIP hover/spacing, Focus contraction and all six themes on one persistent dock. Generated shell images were inspected; an exposed rectangular blur outside rounded corners was corrected and re-rendered.

Coverage includes encrypted secrets, model isolation/fallback/cooldown, real subprocess NDJSON streaming/cancellation, non-modal approval, attached-source edit hashes/backups/BOM and writer exclusion, error-awareness dwell/guards, real PDF→DOCX and XLSX→PDF, smaller FFmpeg output, direct-conversion state/Stop, nine crop actions/Escape, negative-monitor/DPI geometry, all eight Hub pages, six shell palettes and bilingual rendering. A fixture proves the application protocol, not a live provider's capabilities.

The Windows x64 self-contained v1.3.0 publish and both portable/source ZIPs were rebuilt successfully. Archive paths were inspected: no absolute/traversal/backslash entries, secret files or environment files. The directory is not a Git checkout, so no Git diff or commit was available; changed source and generated artifacts were reviewed directly.

## Live OpenCode and Gemini

OpenCode CLI 2.3.2, `google/gemini-3.5-flash-lite`, user-supplied Google AI Studio credential:

- Text marker, synthetic clipboard rewrite, image marker reading and Arabic presentation planning/output/export passed in `artifacts/live-20261125-004851/results.json`.
- The initial Gemini HTTP 400 was reproduced: agent `steps=1` causes OpenCode to append an unsupported final assistant prefill. A normal text/image request now has two allowed steps while tools remain denied.
- Follow-up exact-six-slide planning, OpenXML validation and PDF export passed in `artifacts/live-20261125-011036/results.json`; the renderer no longer adds an unwanted seventh slide. One intervening response lacked the required JSON and was rejected without execution. Model output still requires the normal full-content review.
- The Google key is stored with Windows user encryption outside the repository, passed through the OpenCode child environment, and excluded from strict built-in free mode. Tests verify this using a synthetic key. Google account quotas and billing determine Free Tier eligibility; successful inference does not prove zero billing or permanent free availability.
- No automatic text or vision fallback is enabled in the configured user profile.

The built-in anonymous free tier remains blocked by its separate quota/reset window. Live TinyFish search is unverified. Historical text success via another user provider is not a current availability guarantee.

## Converters and visual review

LibreOffice 26.8.3.1 was provisioned as a local administrative image from the official MSI, with SHA-256 verification, outside the repository. Real XLSX→PDF text extraction passed. Windows uses `soffice.com`, a short private profile path, and new output filenames. The portable package does not bundle LibreOffice or FFmpeg.

The initial Arabic deck was rendered and all seven pages reviewed for typography/overflow; the follow-up export was confirmed to contain exactly six pages, rendered, and all six pages reviewed with no clipping. This verifies layout, not factual correctness of every generated sentence. PDF→Word now reconstructs editable paragraphs, page geometry, basic two-column reading order, Arabic direction and embedded images. Synthetic text/image/mixed-size/RTL fixtures and OpenXML validation passed; real LibreOffice round-trip preserves image horizontal placement. Native Windows rendering covers scanned/vector-only pages as images without OCR. Unsupported visual pages and size/image budgets fail without partial output; an existing destination is preserved. Complex tables/equations/floating layouts remain approximate.

All six expanded Island/Dock palettes were rendered and reviewed. Status/icon and Smart Clipboard contrast defects were corrected. Screenshots use synthetic data and do not by themselves establish live desktop behavior.

## Performance and outstanding acceptance

The published v1.2.0 build measured 0.21% mean normalized CPU and 238 MiB peak working set over 60 seconds on 12 logical processors (`artifacts/performance-published-v1.2.json`). Its recorded SHA-256 identified the v1.2.0 assembly at measurement time. This is a historical idle observation, not a new v1.2.1 measurement; it excludes AI/converter subprocesses and is not a universal guarantee.

A 60-second desktop-idle observation of the published v1.1.0 build measured 0.94% mean normalized CPU and 272 MiB peak working set on 12 logical processors (`artifacts/performance-published-v1.1.json`). The report records the published assembly SHA-256. This is the NIVO process only; it excludes OpenCode/LibreOffice/FFmpeg workloads and is not a universal limit.

Earlier native acceptance passed 25 checks in `artifacts/desktop-20261125-160909/results.txt`. The expanded lifecycle run later passed 29 checks, including hidden-Island preference, permanent language teardown, retired-Island completion forwarding and timer/suggestion suppression during selection. These are earlier run evidence, not a claim that the final expanded native suite passed.

The final local suite verifies repeated hover movement at 1280 DIPs with eight pins and six synthetic running apps in English/Arabic, non-overlapping capsules, all six Settings tabs in both languages, minimum-window Save visibility, initial/edit/after-save notices, nullable/string/oversized retry hints, cooldown reset and dedicated vision consent. Real odd-size yuv444p video was converted to smaller 162×92 yuv420p output and inspected with FFprobe. Cardinal PDF rotations and Word/PPTX XML/Arabic checks passed.

Instrumented real-cursor runs matched app labels to icon centers for both first/last pinned apps in Arabic and English. The latest expanded desktop run (`artifacts/desktop-20261125-164051`) passed those hover checks, capture/exclusion and clipboard-refusal paths, but did not complete repeated-error help: foreground/UI Automation availability changed during probing. The user explicitly deferred further mouse/keyboard desktop testing. The final package was therefore built with `-SelfContained`, without claiming a pass for `-DesktopAcceptance` on this revision. Real mixed-DPI hardware and wallpaper/motion behavior remain subject to acceptance testing. Voice/wake word stay deferred until separate speech API configuration.

## Readiness follow-up — 1.2.1

A new regression failed on 1.2.0: an ordinary chat answer replaced a newly opened clipboard preview. Clipboard streaming now tracks its own assistant message, and Refresh detaches a late reply. UI-button replay checks original text/image copying, transformed-text copying, refresh during processing and empty previews using injected readers/writers; the real clipboard stays untouched. A real native image-sharing dialog is declined only after verifying the dialog belongs to the test process; prior capture/mode and the original clipboard image remain intact, without a model request.

One native run failed its final focus assertion without before/after focus evidence. Instrumented reruns passed with the same foreground HWND before/after showing the suggestion and at assertion; no production focus workaround was added from that unexplained run. NuGet vulnerability scanning, including transitive dependencies, reported no known vulnerable packages from the configured nuget.org source on 2026-11-25. This is not a complete security certification.


The final Neon integration review found no High/Critical blocker and reported two smaller defects. Both were fixed locally: retry hints accept null/string inputs safely with finite one-day bounds, and Settings compares model edits against live saved values after Save. Regression tests reproduced the null-hint failure before its fix and verify both cases. The NIVO orb now shares the same single-tooltip policy as other dock apps. Quoted OpenCode executable/PATH entries are normalized. No additional live AI requests were made in this readiness round.


## 1.2.2 direct image sharing

At the user's explicit request, image-sharing confirmation is optional and off by default, including profiles created before the setting existed. Controller image questions now send directly to the configured vision route. Settings → Desktop can opt back into the confirmation; model privacy/route information stays available in Settings. The legacy native refusal check explicitly enables that optional confirmation. No interactive desktop/physical-pointer run was performed in this follow-up, respecting the earlier deferral.

The 1.2.2 fixture verifies a real PNG reaches the configured vision model directly through Controller without an image-confirmation dialog, then confirms capture state and the temporary PNG are cleared. Both language layouts expose the optional confirmation switched off. All 261 local checks passed.
