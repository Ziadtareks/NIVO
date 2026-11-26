# NIVO feature inventory

This inventory distinguishes implemented application behavior from unverified external integration. NIVO does not claim pixel-identical reproduction of a proprietary app from marketing pages.

| Area | Implemented | Limits / external requirements |
|---|---|---|
| Name and identity | NIVO / نيڤو, full bilingual Arabic (RTL) + English (LTR) interface with live 1-click toggle, custom upright SVG mark, tray icon | Independent product; no AYO assets copied |
| Windows shell | 8-state top-edge cosmic glass Living Notch (`IslandWindow`: Idle, Working progress pill, Completed `Open` pill, Inline Proactive Suggestion pill, 4×2 Command Center + Recent Creations, Inline Silent Ask with capture thumbnail, Inline Smart Clipboard with dual text+image preview, Direct 1-click PDF/MP4/Office Converter Drop Zone); 3-capsule wallpaper-sampled Liquid Glass Dock (`DockWindow`: Start/Search/Focus profiles, NIVO orb + Pinned + Live Running + Context-Suggested apps with magnification & hover pills, Clock/Volume/Wi-Fi/Language/Quick Tray status capsule) | Primary monitor; no Explorer binary modifications |
| Ready-to-run download | A self-contained single Windows x64 EXE in GitHub Releases; no build tools or separate .NET installation | AI backend runtimes/accounts remain external |
| Windows startup | Settings → Desktop → Start NIVO with Windows; current-user Startup shortcut, no Administrator rights | Off unless enabled; keep the EXE in a permanent folder; moving it requires updating the option |
| Taskbar replacement | Opt-in hide/restore of the native taskbar, crash watchdog | Primary taskbar only; third-party shell replacements not tested |
| Context apps | VS Code/Studio and Office/PDF process-based suggestions | Rules, not AI prediction; pin order is retained, but absolute positions can shift as dock width changes |
| Focus | Profile-aware filtering of unpinned running apps, keeping all pins | Does not close or suppress other apps or browser content |
| Themes | Frost, Horizon, Frutiger Aero, Nebula, Grey, Pearl, plus OLED | OLED uses pure-black backgrounds and opaque black shell glass; other themes retain wallpaper sampling. Independent interpretations, not AYO theme assets |
| Text assistant | OpenCode v2 CLI (uses configured user providers/logins by default or optional strict free-only mode), dynamic catalog refresh, configurable fallback model with 429 cooldown, JSON event streaming | Live text and image inference verified via `google/gemini-3.5-flash-lite`; encrypted optional Google credential; built-in `opencode/*-free` tier shares provider quota limits |
| Connected accounts | Browser sign-in and private provider profiles for Codex/ChatGPT and Antigravity/Google; dynamic protocol catalogs, image capability labels, chat/image routing and streaming | One account per provider; CLI required; fixture and unauthenticated native probes verified; Codex account chat works per user report, live native voice and Google code sign-in require user interaction. Catalog is not entitlement/quota proof. Talk automatically selects a connected account: experimental Codex inline or original Antigravity external CLI (F6); ACP itself has no Live voice, and neither account route enables image generation or native web search. |
| Silent Ask | Bottom composer, corner answer card, Tab/Shift+Tab modes, drag/drop, attachment preview, Arabic input direction | Four global shortcuts configurable in Settings; conflicts reported, with tray fallback |
| Screen / Crop | Capture on the monitor under the cursor, region selector, nine quick actions, image drop and direct sending; optional image-sharing confirmation | Image-capable free model required; provider privacy restrictions apply |
| Hover | Armed cursor-following spotlight; click/Enter confirms, Esc/right-click cancels | Single-monitor region per capture; real mixed-DPI hardware remains unverified |
| Agent | Reviewed allowlisted plans, image-aware planning, Working/Needs you/Done task dock, asynchronous full-content approval, cancellation and history | No arbitrary mouse/keyboard or shell execution |
| Supported actions | Notes, tasks, reminders, memory, HTTP(S) link, standard app, clipboard copy, text/DOCX/PPTX output, video compression, document conversion, reviewed text/code replacement | Every plan is reviewed first; modified source must be explicitly attached UTF-8 text/code ≤256 KB; verified backup and hash precondition |
| Presentations | Real PPTX decks with cover and content slides, Arabic direction; planned slide count includes the cover | Text layout; no AI image generation, imported template preservation, or render guarantee |
| Clipboard | Explicit read, preview, summarize/translate/rewrite/explain/task prompts | No passive clipboard collection; AI transformations need free-model availability |
| Documents | Local extraction of PDF text, DOCX, PPTX, XLSX, text formats | 15 MB per source file, extraction bounds; scanned PDF through screenshot |
| PDF → Word | Editable paragraphs, page geometry, basic columns, Arabic direction and embedded images; native rendering of no-text pages | Cardinal page rotation and native scanned-page orientation supported; approximate flow, no native table/equation reconstruction or OCR. 25 MB, 200 pages, 50 rendered pages |
| Settings | Six grouped tabs, wrapped descriptions, pinned Save/Revert, key/catalog notices, four focus profiles, Arabic/English layout | Keys remain masked; API speech endpoints are optional and configured explicitly |
| Office → PDF | LibreOffice headless conversion | Requires local LibreOffice; `soffice.com` preferred on Windows; real XLSX→PDF verified |
| Video | FFmpeg H.264/AAC MP4 conversion | Requires FFmpeg; rejects recompression that does not reduce file size |
| Local memory | Add, edit, tier, delete, clear, export; explicit remember/تذكر/افتكر/احفظ commands | Explicit facts; no automatic personal profiling |
| Memory map | Native WPF Viewport3D rings and shaded nodes, tier colors, dark stage, node hover, tilt, drag inertia, tier filters and real counts | No fabricated nodes; empty tiers remain empty; list remains accessible |
| Workspace | Notes, tasks, completion, dated reminders, recent output files | Reminders require NIVO to be running |
| Your Usual | Named app/link launch sets, optional music and focus, capture/replay of matching app window positions, /usual command | Positions match visible executable windows; browser tabs and shortcuts cannot be matched by URL; no arbitrary input macro recorder |
| Proactive help | Opt-in title/due-task model suggestions on 30/60/120-second timer; local due-soon distraction check-in with cooldown | No background images; does not poll an unchanged title repeatedly |
| Gaming | Known-process quiet mode, Game question mode | Wiki research uses experimental keyless search, not direct game memory access |
| Web search | Explicit Web/Game mode, read-only OpenCode TinyFish search selection | Experimental; no search API key inherited; service may require setup or quota; not live-verified |
| Inbox | Optional Google Desktop OAuth + PKCE, metadata-only refresh, task/note/reminder creation | Requires user OAuth app; no background polling, email bodies, attachments, or sending |
| Voice | Automatic connected-account Talk: Codex inline realtime with transcripts or original Antigravity external CLI (F6); explicit stop; optional legacy speech APIs | Account feature/quota access required; native speech layer differs from selected reasoning model. Antigravity voice requires its external CLI window; no inline transcripts or NIVO prompt injection there. No computer-action tools within Talk, no live authenticated mic/playback testing yet |
| Repeated-error awareness | Opt-in bounded visible UI Automation error text, dwell/debounce/cooldown and an offer to help | Unsupported apps return unavailable; no screenshot/OCR fallback, no microphone |
| Wake word | Not implemented | Deferred with voice; no always-on microphone |
| Incognito | Separate in-memory conversation, no NIVO chat/memory writes, capture/proactive blocked | Does not erase existing data or guarantee provider/CLI zero retention |
| Deployment | Portable Windows build and packaging scripts | No code signing, automatic updater, accounts, subscriptions, or hosted backend |
| Performance | Native WPF interface, no Electron runtime | No claim of 1% CPU / <1 GB RAM; OpenCode and conversions are separate processes |

Public references used for feature discovery: https://heyayo.com/ and https://github.com/yazanAydi/ayo-releases/releases/tag/v3.0.1.

## Reference review for version 0.2

On 2026-11-24 the live [Meet AYO](https://heyayo.com/#meet-ayo), [Features](https://heyayo.com/#features), toolbox popups and Settings tabs were inspected. The site explicitly labels Silent Ask answers as scripted, Lore Web facts as made up, and proactive timing as sped up. Those demos establish visible interaction intent, not successful production behavior. Only three themes can be previewed on the website; the other three are named.

The largest gaps corrected in 0.2 were the dark-only Frost palette, large centered Silent Ask window, static island, single-icon hover effect, incomplete theme coverage, projected 2D memory map, missing task-state card, absent drop/Tab interactions, and recreating every message control on each streamed chunk.

A follow-up independent Gemini 4 Neon review produced version 0.2.1. See [the reconciled findings](REVIEW.md).

Motion uses short eased entrance/press transitions, interruptible 220 ms island surface/clip transforms with destination panel layout and content fades, cosine dock magnification with neighbor spacing, animated Focus contraction, busy/speech activity bars, optional click-through task edge indication, task/card entrances and memory rotation with deceleration. Reduce motion and Follow Windows animations are independent preferences: NIVO can animate while Windows animation is disabled, without changing Windows settings. Finite transitions release their clocks; repeating activity bars run at 30 fps only while visible and active. Wallpaper blur is clipped within rounded capsule geometry, and each capsule samples its own screen region.

### Remaining differences

- This is an independent native WPF implementation. NIVO does not contain AYO's Electron/React code, proprietary materials, voices, cloud infrastructure or identical rendered pixels. Wallpaper sampling is not a live DWM backdrop implementation.
- Website desktop folders, custom website cursor, window demonstrations and browser icon arrangement belong to its desktop simulation. NIVO does not replace Explorer's actual desktop or globally change the Windows cursor.
- Wake word remains deferred; native Codex account Talk is now being integrated at the user's request. Gmail and live model/search/speech success need external service availability and user credentials where required.
- The agent executes reviewed supported actions; it is not a universal mouse/keyboard automation agent. Your Usual replays selected launch/layout steps, not arbitrary recorded input.
- Capture/crop targets the monitor under the cursor. Primary-monitor native acceptance passed; mixed-DPI hardware and taskbar crash restoration still need device testing.
- PDF-to-Word reconstructs editable text, basic layout and images with approximate flow. Scanned pages remain images; OCR and exact complex-layout reconstruction are not bundled. Office-to-PDF needs LibreOffice; video compression needs FFmpeg.
- No CPU/RAM figure or permanent free-provider availability is promised. Live text inference passed on the user's configured OpenCode CLI model (`numes/dealignai/deepseek-v4.1-flash-uncensored-fp8`), while built-in `opencode/*-free` models remain subject to provider quota resets; deterministic tests are listed separately in VERIFICATION.md.

### Windows support instructions and clarification

The shared AI policy includes symptom-led Windows 10/11 troubleshooting, least-privilege diagnostics, shell-specific quoting/elevation guidance, version-sensitive navigation, official-source verification for current issues, and the actual boundaries of NIVO chat/Agent/capture/voice/conversion features. Runtime context automatically includes only reported OS version/architecture, app version, local time with offset and timezone; it does not inventory apps, read private directories or test administrator privileges. Context and attachments stay separate from system authority.

Agent supports an explicit clarification response with zero steps. Such a response is displayed in chat without execution/review, and recent conversation is supplied for subsequent planning. Mixed clarification/action plans fail validation. This fixes the previous contradiction between asking for missing details and a schema that demanded at least one executable step.

### Fullscreen and games

A shared foreground policy suppresses Dock and Island for full-monitor fullscreen, recognized windowed games and manual Gaming mode. Physical bounds distinguish full-monitor coverage from a maximized work-area window, including negative-coordinate monitors, DPI-scaled monitors and auto-hide taskbars. Desktop/shell windows and NIVO overlays do not themselves count as fullscreen. Foreground identity is cached; a 300 ms visibility timer does not enumerate running applications. Returning to an ordinary app restores the shell while preserving a deliberate Island hide. Suppressed states also defer reminder popups/proactive suggestions and release the native taskbar. An automatic switch to Game mode returns to General after leaving the game, without replacing a separately selected mode. Unknown windowed games need manual Gaming mode.

### AI/task correctness fixes

Empty streamed text no longer disables retry/fallback; malformed diagnostic JSON is ignored by type; oversized chunks are rejected before reaching the UI. Temporary-file cleanup retries expected sharing/access failures and cannot leave Busy permanently set. Confirmation re-entry is guarded and requests own their cancellation sources. Planning, human review and conversion execution have separate time budgets.

Reviewed source eligibility now also requires the complete original within the 60,000-character context limit (in addition to the byte/type limits), preventing a truncated preview from authorizing replacement of an unseen tail. Agent can plan conversion from scanned or 15–25 MB PDF metadata without pretending to know its content. Agent Workspace writes are blocked in Incognito. Reminder parsing is invariant/Gregorian. Clipboard transformations isolate and restore unrelated composer attachments/capture/mode.

### Version 1.5.0 computer execution

Computer access adds a reviewed `powershell` action and routes chat requests through the planner when enabled. Pure replies use an action-free answer envelope. Commands use the exact reviewed script and canonical working directory, Windows PowerShell 5.1, the current Windows account token, no profile/interactive stdin, bounded output, per-command deadlines and process-tree Stop. Command startup and pipe draining run off the UI thread. No Windows execution policy or system-wide permissions are changed. Failed later steps preserve earlier outputs and the failure in chat/history. Native stderr with exit 0 remains a successful command, and top-level `using namespace` statements are supported.

The model's native OpenCode shell/filesystem tools remain denied; NIVO's host executes approved plans. There is no automatic execution of model scripts before review, no UAC bypass, and no automatic shell access for background suggestions or one-click transformations. Clipboard reference text is separated from the trusted transform request. Computer access uses local Memory context and can be revoked while busy through Settings Save. Larger/unattached file operations are possible through reviewed commands; unlike `edit_file`, arbitrary commands do not get an automatic backup/undo guarantee.

### Version 1.5.0 animation work

Continuous Dock input is coalesced at rendering cadence and followed by a single render participant with smooth convergence. The Island morph also uses the shared render loop, moving its header edges without animating header layout width per frame. Wallpaper blur and glass surfaces are cached. Recent outputs avoid redundant enumeration/rebuild, and unchanged inline replies do not restart size work. All render participants stop when idle, hidden, closed or reduced motion is enabled.
