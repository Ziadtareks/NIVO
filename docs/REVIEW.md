# Gemini 4 Neon review reconciliation — NIVO 0.2.1

An independent review examined NIVO source and rendered WPF interfaces. Findings were checked against current code before changes were applied. UI reviews do not establish successful live provider behavior.

## Findings addressed

- **Escape scope:** the unmodified global Escape shortcut is now registered only for active Agent/routine work, including plan approval. Chat and background checks leave other apps' Escape key available. In-app Stop remains available.
- **Non-intrusive suggestions:** borderless glass cards appear without activation or keyboard-focus changes. Do it opens an editable Agent draft; it never silently approves execution.
- **Island layout:** a dedicated header column contains activity bars, separate from the arrow button. Compact idle dimensions, state dot, button-ancestor drag checks and soft elevation replace the bulky idle card.
- **Desktop dock:** wallpaper blur is clipped inside its own layer, keeping magnification headroom transparent. Press and magnification transforms compose; transparent icon controls are borderless and the brand label is centered.
- **Themes:** swatches stretch to equal widths; leaving Settings restores an unsaved preview. Warning text has a semantic palette color. Window controls are circular, with a red close hover; selected question modes use glass pills.
- **Crop tools:** Explain, Translate, Summarize, Fact-check, Abbreviation and Table use the preferred response language. Fact-check selects Web. Copy as image is local. Note/Task save the image with a user-entered title and a Workspace reference, without OCR or an AI request.
- **Memory map:** a dark radial stage contains four tier colors, visible filter-toggle states, node hover enlargement, tilt and drag deceleration. Counts and nodes come from real local memories; only QA renders contain synthetic sample facts.
- **Task approval:** full plan review is non-modal and asynchronous. No action runs before Allow. The task dock exposes Review & allow / Deny while waiting; Deny, closing review, or Stop executes no pending step.
- **Daily-use details:** file picker supports multiple selection; Inbox includes local note/task/reminder actions; personas include distinct tone instructions; About shows the app version and shortcuts. Minecraft Java detection also checks the window title, avoiding unrelated Java tools.
- **Local check-in:** with proactive mode enabled, a due-soon task and a distraction title persisting for two minutes can trigger a local nudge, with a 15-minute cooldown. It uses no model and is suppressed during quiet/busy/Incognito states and while NIVO is foreground. Keywords are configurable.
- **Agent edge indicator:** an optional, non-activating click-through cyan edge overlay is shown during active task work. It is hidden during gaming/quiet states and reduced motion. Its native window styles are checked in the self-test suite.

Some documentation findings referred to files read before concurrent updates. The current feature inventory and verification record supersede the older 30/58-check and projected-map descriptions.

## Implementation boundaries

- Voice and wake-word activation remain deferred until the user configures speech APIs.
- NIVO uses native WPF rendering and sampled wallpaper glass.
- NIVO does not replace the Windows desktop, global cursors, or external provider infrastructure.
- The reviewed agent supports an explicit action set. It does not offer arbitrary mouse/keyboard automation, password entry or learned input macros.
- File drops are bounded to documents/media and one image. Folders are rejected; choose files explicitly. PDF-to-Word is text-based, not full layout reconstruction or scanned-page OCR.
- Capture uses the primary monitor. Live speech, Gmail authorization, model-generated text/vision/planning/search and broad device/game compatibility remain unverified. The last live OpenCode request returned the free-provider quota limit; no paid fallback was used.

The final Release build has zero compiler warnings/errors and passes 79 deterministic checks.

See [FEATURES.md](FEATURES.md) and [VERIFICATION.md](VERIFICATION.md) for implementation and test limits.


## 1.2.1 readiness follow-up

The second Neon audit identified concrete lifecycle defects in addition to visual defects. Desktop timer/suggestion work is now suppressed during capture; language replacement permanently disposes the old Silent Ask; completion callbacks on a retired Island forward to its current replacement; shutdown is guarded against re-entry. A single live taskbar watchdog is reused instead of spawning one on each hide transition.

Inline image follow-ups reset the consumed capture/mode; busy/unsupported/folder drops are rejected, multi-file previews keep controls visible, and Back/Cancel release pinning. Clipboard transformations track their own response, and Refresh detaches late results.

The user-reported dock label drift came from measuring DesiredSize after assigning a horizontal Margin: that margin entered the next measured width. Label positioning now uses an independent TranslateTransform and one visible hover pill. The dock also measures status reservations, aligns context/focus classifications, avoids CoreAudio polling on every streamed text chunk and handles unavailable audio.

Settings uses existing native theme resources with grouped sections, wrapped explanatory controls, separate model routes, lazy cancellable catalog checks and pinned Save/Revert. Quota parsing handles nested provider status codes; configuration changes clear cooldowns; image consent discloses the dedicated vision fallback. No fallback is enabled automatically.

Converters normalize cardinal PDF text rotations, preserve scanned-page orientation, retry oversized scanned renders within image budgets, emit valid Arabic Word styles and sanitize XML text. Video recompression accepts odd dimensions and emits even-sized yuv420p output. Complex floating PDF layouts, OCR and arbitrary oblique text remain bounded fidelity limitations. Final run evidence is recorded in VERIFICATION.md; hardware coverage and material rendering remain subject to testing.

## Version 1.3.0 motion reconciliation

Neon reviewed NIVO's dock and island interactions. Its Dock implementation adds cosine proximity magnification, lateral spacing, focus width transitions, context-label anchoring, active-app halos and per-capsule wallpaper sampling. The parent integrated Island morphing, flush bottom lighting, interrupted-collapse recovery and visible-only activity waveforms.

Actual motion-on tests exposed two shared WPF defects: attaching animation storage discarded `SetCurrentValue` resting sizes, and a fresh clock exposed its target before the first tick. Motion-owned properties now commit their base value with `SetValue`, start from the displayed value using an aligned clock seek, deduplicate identical targets and release completed clocks. App-only animation is explicitly supported because this device disables Windows interface animation. No system preference was modified.

## Version 1.3.1 Island stutter regression

The user localized visible stutter to Island opening/closing. A failing motion-on test measured nine frame-size/layout changes during expansion. Island layout now commits its destination dimensions once, while a shared render transform morphs the glass surface and content clip. Shadow rendering is isolated on a cached background, and header width/bottom-light position follow the displayed bounds. The same container trace now records one size change. Fast reversal retains current visual bounds; focused regression checks and mid-transition renders pass. This round did not rewrite Dock magnification or alter Windows animation preferences.

## Version 1.4.0 assistant review

The parent traced a planning protocol defect: instructions required asking for missing dates/targets, while ParsePlan required 1–12 executable steps and had no question response. ActionPlan now has a bounded clarification field that is valid only with an empty steps array. Controller displays it without review or side effects; Agent follow-up payloads retain recent conversation. The system prompt also describes real NIVO capabilities and includes a Windows support playbook and minimal, non-identity runtime metadata.

Focused process-boundary fixtures verify that the system policy reaches OpenCode, reference data is not promoted to system authority, and successive clarification turns preserve context while leaving Workspace, task history and output untouched. These deterministic checks validate application plumbing, not live-model expertise.

Neon's independent AI/task review also identified empty-chunk retry/fallback suppression, typed-NDJSON failures, cleanup-induced Busy lockups, confirmation re-entry, truncated edit-source eligibility, scanned-PDF planning failure, Incognito Workspace writes, malformed source-path errors, locale-sensitive dates, review/conversion time budgets and unrelated clipboard attachments. These were checked against the call sites and fixed; focused fixtures exercise the failure paths without live AI or the real clipboard. Clarification auto-hide had already been corrected during the parent work; question JSON now accepts omitted/null steps and case variants while rejecting mixed questions/actions. Existing generic JSON consumers keep their exact property matching.

A separate Neon implementation adds physical-window fullscreen/game suppression, cached foreground identity and shared reveal guards. The parent reviewed the integration, replaced newly introduced broad native-fallback catches with specific logged exceptions, and added synthetic geometry/state tests. The opt-in native desktop harness now moves its synthetic target between fullscreen and windowed stages so ordinary hover/suggestion checks do not contradict the new production suppression rule; that physical harness remains unrun in this round.

## Version 1.5.0 execution and animation review

Neon implemented the render-tracker changes in Motion/Dock/Island. Its final handoff lacked a completed narrative report; the parent inspected the actual source diff and verified clock/allocation, geometry, visibility and settling behavior directly. The previous 120-event diagnostic recorded ~1,979 clocks and ~6.8 MB allocated. The new measured burst records zero clocks and under 1 KB.

A separate read-only Neon review of Computer access confirmed the host review gate and identified transform-to-planner escalation, working-directory ambiguity, lost failure/partial outputs, timeout/disable-state issues, local Memory loss, prompt contradictions, and review presentation/null-field problems. These were addressed with read-only transform routing and a separate reference field, absolute canonical review targets, preserved results/history, execution budgets plus bounded explanation, a busy-state revocation path, Memory/context and per-call policy separation, wrapped script review, current-role disclosure and normalized optional timeouts. Invisible bidirectional override controls are rejected.

The reported Windows PowerShell native-stderr failure was not reproduced with external stderr redirection: the real scratch command exited 0 and continued its script. A regression retains that behavior. Windows PowerShell 5.1 syntax is explicit in the prompt, and scripts are parsed as standalone scriptblocks so top-level namespace statements work. Tests execute only scratch-file/process operations without elevation, live models, private files or the real clipboard.


## Version 1.6.0 connected-account review

Neon inspected installed T3 Code provider adapters and implemented the two native transports. Codex uses a private CODEX_HOME, browser ChatGPT authentication, account checks and a paginated model catalog. Neon reports 48 additional scratch checks, including zero model-visible tools and strict configuration acceptance against codex-cli 0.166.0. Parent verification separately covers 30 fixture/UI checks, native unauthenticated probes, and the existing assistant/computer suites; the packaged source hashes match the reviewed implementation.

Parent fixes preserve WPF-thread streaming delivery, reject incomplete ACP output, fail visibly on logout-token cleanup errors, remove inherited API credentials, and handle closed ACP transport races and connection diagnostics. Actual authenticated user login/inference remains pending browser interaction. The subsequent integration review found the Antigravity auth storage mismatch and native voice lifecycle issues described in the 1.7 notes below.


## Version 1.7.0 auth and native Talk review

The account review identified the actual Antigravity failure: login used the Windows keyring while later processes used SSH-selected private file storage; the claimed FORCE_FILE_STORAGE variable was absent from agy 1.5.13. The repair keeps SSH-selected file storage on every call and runs the real `agy acp --login` code-exchange flow, verifies Google URL/state, and checks persistence in a fresh process. Invalid unsandboxed permission rules were removed, built-in customizations/swarm disabled, and inherited provider/auth/permission overrides stripped. CLI path Save and tab-unload cancellation notices now persist correctly, and proactive Codex timeouts are handled.

The Talk review found callback-thread device disposal, output-lock disposal, activation/shutdown races, and cleanup aborts that could leave Completion pending. Parent fixes schedule shutdown off device callbacks, detach playback before disposing outside its lock, serialize activation with resource snapshots, always clean up and complete the session while reporting cleanup failures, and make app Stop await startup rollback/owner cleanup. Quit waits for voice cleanup. The unverified 16kHz fallback was removed: capture uses PCM16 mono 24kHz. Button re-entrancy now reads current preferences and Escape stops Talk in NIVO windows.

Parent account checks exercise fresh-process Google code exchange, invalid OAuth state/origin/newlines, voice device faults, cleanup failure, oversized RPC output, native/legacy button-mode switching and the previous inference/UI cases. Real mic/speaker audio, AEC/echo behavior and account voice entitlement remain unverified; no live microphone was accessed during these tests.
