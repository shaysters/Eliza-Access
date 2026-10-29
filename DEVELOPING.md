# Eliza Access: developer notes

In-process .NET 4.7.2 x64 mod that makes the visual novel **Eliza**
(Zachtronics, custom Mono-based engine internally named *Nagoya*) fully
playable by blind users. The mod reads every screen aloud through a
screen-reader abstraction (Prism), drives the engine's controller-mode
focus model from the keyboard, and adds custom navigation where the
engine's own input doesn't suffice.

The intended user is the developer of this mod — blind, daily-driving
the build. Speech responsiveness, clarity, and correctness matter more
than visual polish.

## Repo layout

| Path | Purpose |
|---|---|
| `ElizaAccess-v2.csproj` | Project file. net472, x64. Builds `ElizaAccessDll.dll` (and the loader below). |
| `Bootstrap/` | The loader: `ElizaAccessBootstrap.dll` (an `AppDomainManager`) and the `Eliza.exe.config` that activates it. See *Injection method*. |
| `Entry.cs` / `Main.cs` / `SpeechEngine.cs` / `Polyfills.cs` | Top-level entry, poll loop, TTS, net472 polyfills |
| `Common/` | Framework primitives (`Nagoya`, `ScreenView`, `SubView`, `ScreenViewManager`, `CursorFocus`, `SpeechEngine`, `PrismNative`, etc.) |
| `Views/` | Per-game-screen `ScreenView` and per-screen `SubView` implementations |
| `prism.dll` | Vendored native screen-reader abstraction. Lifted from the Prismatoid NuGet package's `runtimes/win-x64/native/` because the managed wrapper is `net10.0`-only. |
| `README.md` | User guide: install, setup, keys. |
| `DEVELOPING.md` | This file. |

End-user install is five flat files in the game directory, all
produced by the Release build (see *Building* below). None of them
replaces a file the game ships.

### Game source (not in this repo)

Working on engine internals usually means consulting a local de4dot
decompile of the game's `Nagoya.dll`. We don't commit it — both for
size and because it's Zachtronics' IP. Two passes are useful:

- **Raw decompile** keeps the obfuscated identifiers
  (`#=q...` Base64-ish names). The source of truth for reflection.
- **Clean decompile** renames obfuscated symbols to readable
  `GClassNN` / `gclass_N_M` / `smethod_N` forms. Easier to read for
  semantics, but those names **do not correspond to anything at
  runtime** — always cross-reference back to the raw decompile before
  relying on a name.

## What it does (in plain terms)

Eliza renders everything as custom-drawn pixel graphics — no native
text controls for a screen reader to pick up. The mod hooks the
engine's drawing and input pipelines via Harmony, mirrors the screen
stack, and for each screen reads the underlying game state (or
captured-text labels paired with hotspots) and speaks them via
Prism.

Behaviours, broadly:

- **Focus tracking**: when the controller cursor moves between
  hotspots, the focused control's label is spoken.
- **Dialogue**: unvoiced lines are spoken via the screen reader.
  Voiced lines stay silent (the engine plays the audio) but the text
  is recorded so the R-key replay surfaces it.
- **In-session features**: star ratings, character notifications,
  proxy-response prompts, post-session results panel, transparency-
  mode inline phone rendering.
- **Phone apps**: home grid, Mail, Chat, Fortune (with hand-written
  scene descriptions for the four pre-baked images), Eliza (3-tab
  profile/inbox/sessions with custom grid nav for the locked-achievement
  tab), Solitaire (custom row-jump nav between tableau and free cells).
- **Modal/menu reading**: pause menu, settings, confirm dialogs,
  chapter-select, history (transcript), credits, end-of-game
  Wellness intake/outro.
- **Global shortcuts**: `R` repeats the last spoken text (works from
  anywhere); any key press stops in-flight speech (so the user's intent
  to act doesn't get talked over).

## Building

The mod uses a single `dotnet build` for everything. From the repo
root:

```
dotnet build            # Debug, deploys
dotnet build -c Release # Release, deploys + bundles
```

### Debug / Release deploy

Every build (Debug or Release) auto-copies the build outputs into the
game directory so the next launch of Eliza picks them up:

- `ElizaAccessDll.dll` (+ `.pdb`)
- `0Harmony.dll` (from the `Lib.Harmony` NuGet package)
- `prism.dll` (vendored at repo root)
- `ElizaAccessBootstrap.dll` and `Eliza.exe.config` (the loader)

This is the `DeployToGameDir` MSBuild target in
`ElizaAccess-v2.csproj`. It auto-skips when the game directory
doesn't exist (so a fresh clone on a non-gaming machine still builds
cleanly).

If `Eliza.exe` is running, the DLLs in the game directory are locked
and the Copy step fails with a "file in use" error. Close the game
and retry.

### Release bundle

In Release configuration, an additional `BundleRelease` target runs
after the deploy and emits a flat zip the user can drop straight into
their game directory:

```
bin/Release/net472/ElizaAccess.zip
├── ElizaAccessDll.dll
├── 0Harmony.dll
├── prism.dll
├── ElizaAccessBootstrap.dll  ← loader (see "Injection method")
└── Eliza.exe.config          ← activates the loader
```

`/p:SkipBundle=true` opts out.

### MSBuild property overrides

| Property | Effect |
|---|---|
| `/p:GameDir='<path>'` | Use a non-default game install path (default is the standard Steam path) |
| `/p:SkipDeploy=true` | Build but don't deploy |
| `/p:SkipBundle=true` | Release: build but don't bundle zip |

## Runtime log

The mod writes a per-session log to:

```
%LOCALAPPDATA%\ElizaAccess\Logs\eliza-access.log
```

Settings (`config.json`) live in the same `ElizaAccess` folder. It's in
Local AppData rather than Documents so OneDrive folder redirection and
syncing never touch it. Older versions used
`Documents\My Games\Eliza\Accessibility`; on first run the mod copies
`config.json` over from there.

(Previous session is preserved as `eliza-access.prev.log` — the file
rotates on each `Entry.Initialize` call.)

This is the authoritative source for "what did the mod see / do
during that play session". Always check it directly when debugging.
Useful patterns:

- `[Main] ...` — process lifecycle, observer construction
- `[Speech] ...` — Prism init, backend, per-call errors
- `[ScreenViewManager] ...` — screen push/pop, view dispatch
- `[<ViewName>] ...` — per-view diagnostics

## Injection method

Eliza is a .NET Framework 4 program and ships no `Eliza.exe.config`.
We add one, and it names our loader as the process's
`AppDomainManager`:

```xml
<appDomainManagerAssembly value="ElizaAccessBootstrap, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null" />
<appDomainManagerType value="ElizaAccessBootstrap.Bootstrap" />
```

The .NET runtime constructs `ElizaAccessBootstrap.Bootstrap` in the
default domain before the game's `Main` runs. It starts a background
thread that loads `ElizaAccessDll.dll` from the game folder by path and
calls:

```csharp
ElizaAccess.Entry.Initialize(string gamePath) → int
```

`gamePath` is the game directory. The mod waits for the game assembly
(Nagoya) to load before doing anything, so starting early is fine.

Why a separate loader assembly:

- **Stable identity.** The config names the loader by exact display
  name, so its `AssemblyVersion` is pinned to 1.0.0.0 and the project
  rarely changes. Mod updates only replace `ElizaAccessDll.dll`.
- **Fail-safe.** The loader runs before the game; an exception escaping
  it would stop the game launching. It catches everything (logging to
  `%TEMP%eliza-access-bootstrap.log`) and has no compile-time
  reference to the mod, so a missing or broken mod just means no mod.

Delete `Eliza.exe.config` to launch the game without the mod. Steam's
"Verify integrity of game files" leaves our files alone.

**Old manual installs** used a native proxy `libvorbisfile.dll` (with
the real one renamed to `libvorbisfile_orig.dll`), which called the
same `Entry.Initialize`. If that proxy is still present alongside the
new loader, both call in; `Entry.Initialize` records a flag on the
AppDomain and only the first call starts the mod. (A static flag
wouldn't work — the two loaders load the assembly in different load
contexts.) To clean up an old install, run Steam's "Verify integrity of
game files", which restores the real `libvorbisfile.dll`; the leftover
`libvorbisfile_orig.dll` is harmless and can be deleted.

## Engine facts

- Custom Mono / .NET 4.x x64 assembly, name **"Nagoya"** (PE32+, not
  Unity, no IL2CPP).
- SDL2-based. Custom `IScreen` / `ScreenStack` rendering with its own
  `VirtualInput` controller-cursor abstraction (registers per-frame
  hotspot bounds; D-pad / arrow keys snap the cursor to the nearest
  hotspot).
- **Type names are NOT obfuscated. Field, method, and local-type names
  ARE obfuscated** via Eazfuscator's `#=q...` Base64-ish identifiers.
  Resolve members by signature where possible; treat obfuscated names
  as a fallback that may shift across game updates.
- Strings used in the game are encrypted in the assembly and decoded
  at runtime; `Common/Localizer.cs` wraps the decode pipeline so we
  can look up game-rendered text by integer key.
- Game bitness is x64 — all native binaries (Prism) must
  match.

## Architecture

```
                          Entry.Initialize(gamePath)
                                    │
                                    ▼
                              Main.Run()
                  ┌─────────────────┼───────────────────┐
                  ▼                 ▼                   ▼
          HarmonyBootstrap    ScreenViewManager   InputInterrupter
          (one-time patches)   (poll loop)        SpeechRepeater
                                    │
                       ┌────────────┼────────────┐
                       ▼            ▼            ▼
                 ScreenView … ScreenView … ScreenView
                  (one per registered game screen type)
                       │
                       │ (some hosts SubViews)
                       ▼
                  SubView … SubView … SubView
                  (e.g. WorldScreenView's per-feature subs)
```

### Framework primitives (`Common/`)

- **`Nagoya`** — finds the loaded game assembly by name, provides
  `FindType(name)`. Everything reflection-based starts here.
- **`ScreenStackBinding`** — reflection-cached helpers for reading the
  game's `ScreenStack` and the `Maybe<T>` it returns (top-screen
  presence + value).
- **`ScreenView`** (abstract) — handler for one game screen. Lifecycle
  hooks: `OnAdded(instance)`, `OnFocus`, `OnUnfocus`, `OnUpdate`,
  `OnRemoved`. Built-in focus-change dispatch via `ReadCurrentFocus()`
  → `OnFocusChanged(key, label)` with key-based dedup.
- **`SubView`** (abstract) — child handler hosted under a parent
  `ScreenView` (mainly `WorldScreenView`). Like a `ScreenView`, but
  multiple subs share one parent screen and each gates its own
  activity via `IsActive`.
- **`ScreenViewManager`** — mirrors the game's `ScreenStack` each
  poll, constructs/destructs the registered `ScreenView` for each
  push/pop, dispatches lifecycle hooks.
- **`CursorFocus`** — "which hotspot is the cursor in?" reader,
  shared by every cursor-based view.
- **`Patches/HotspotLabelCapture`** — Harmony patches on the engine's
  text-draw and hotspot-register calls. Pairs the most recent
  drawn-text with the bounds of the hotspot that contains it; views
  call `GetLabel(hotspotIdx)` to read the label.
- **`SpeechEngine`** — Prism-backed TTS. Public surface:
  `Speak(text, interrupt=false)`, `RecordSilent(text)` (updates the
  R-key replay buffer without speaking — for voiced lines),
  `Stop()`, `LastSpoken`, `Dispose`. Foreground-window gated so the
  mod stays quiet when the user alt-tabs away. Backend selection is
  delegated to Prism's own `prism_registry_acquire_best`.
- **`PrismNative`** — P/Invoke layer for `prism.dll`. Managed
  Prismatoid wrapper is `net10.0`-only so we bind the C ABI ourselves.
  UTF-8 marshalled manually (net472 has no `LPUTF8Str`).
- **`InputInterrupter`** — polled before everything else each tick.
  On any keyboard rising edge, calls `SpeechEngine.Stop()` so the
  user's intent to act invalidates any in-flight speech.
- **`SpeechRepeater`** — polled after `InputInterrupter`. On `R`
  rising edge, re-speaks `SpeechEngine.LastSpoken` with
  `interrupt=true`.
- **`Patches/KeyboardToController`** — Harmony patches the engine's
  axis/button getters to inject keyboard contributions. The game runs
  in forced controller-mode (see `ControllerModeLock`); the keyboard
  appears to the engine's input pipeline as a controller via this
  patch.
- **`Common/Announcer`**, **`Localizer`**, **`MouseInject`**,
  **`Keyboard`** — assorted small helpers.

### Views (`Views/`)

Each game screen has a `ScreenView` registered in `Main.cs`:

| Game screen | View | Notes |
|---|---|---|
| `TitleScreen` | `TitleMenuView` | Main menu |
| `EscapeMenuScreen` | `EscapeMenuView` | In-game pause menu |
| `WorldScreen` | `WorldScreenView` | Hosts most gameplay subs |
| `SettingsScreen` | `SettingsView` | Sliders + display modes |
| `FakeAppScreen` | `FakeAppScreenView` | Generic stub-app popups |
| `PhoneScreen` | `PhoneScreenView` | Hosts phone-app subs |
| `ConfirmDialogScreen` (obfuscated name) | `ConfirmDialogView` | Universal yes/no/okay dialog |
| `CreditsScreen` | `CreditsView` | End-of-game credits readout |
| `SolitaireScreen` | `SolitaireView` | Custom 40-card freecell variant with row-jump nav |
| `ChapterSelectScreen` (obfuscated name) | `ChapterSelectView` | Episode picker |
| `HistoryScreen` | `HistoryView` | Scrollable transcript |

`WorldScreenView` is the busiest — it hosts most of the gameplay
subviews under `Views/WorldScreenSubs/`:
`Choice`, `Exploration`, `Session`, `TextingOverlay`, `Transparency`,
`WellnessIntake`, `MailApp` / `ChatApp` / `HomeGrid` / `ElizaApp`
(transparency-mode inline phone), `Dialogue`, `Notification`,
`Narration`, `SessionResults`, `ChapterTitle`.

`PhoneScreenView` hosts the phone-app subs under
`Views/PhoneScreenSubs/`: `HomeGrid`, `MailApp`, `ChatApp`,
`FortuneApp`, `ElizaApp`.

## Discipline

Conventions that apply to every view and sub-view:

- **Read real game state.** Don't infer game state from input or audio
  — reflect on the actual fields/methods.
- **Resolve members by signature first.** Use obfuscated names as a
  fallback only.
- **Cross-reference the raw decompile** before trusting any
  `#=q...` name — the clean-decompile's friendly names don't map back
  cleanly across game updates.
- **Focus key dedup.** A `ScreenView`'s `ReadCurrentFocus` returns
  `(key, label)`; `OnFocusChanged` only fires when `key` changes.
  Make the key reflect everything that should re-fire speech
  (sometimes that means including pile contents, not just identity).
- **Don't reset dedup state on transient nulls.** Hotspot lists rebuild
  every frame; a momentary `idx == -1` shouldn't cause the next valid
  read to re-speak.
- **Log on meaningful change only.** Transitions, errors, init
  findings. No per-poll spam — the log file is for debugging, not
  telemetry.
- **Don't trust V2 mappings** (historical: there used to be a parallel
  `V2*Observer` layer; it's fully retired). Field-name comments from
  that era were exploratory and often wrong.

## Known good obfuscated names

Stable across builds we've shipped against. New code can use these as
starting points but should verify they still resolve before relying on
them.

| Purpose | Obfuscated name |
|---|---|
| `VirtualInput.list_0` (hotspot list) | `#=qLRAytqUGdmjcRW4AWR7fiQ==` |
| `VirtualInput` cursor pos method | `#=qQFwke8KooHPmoeHCDWk6kCP_QX42euARDHwVCmLJSSM=` |
| `VirtualInputHotspot.bounds2_0` | `#=qHgVzu7XkH8DOgyBAmZM7nw==` |
| `Maybe<T>.HasValue()` | `#=qgvgNPArrY68Bug9C$7DmMg==` |
| `Maybe<T>.GetValue()` | `#=qE_kIMPOWNCxXDU2FQiQZEw==` |
| `WorldScreen.method_0()` → current `ScriptEvent` (`GClass10`) | `#=qMDFlLtHz0Ua9czODgxkx3p6mGjdLqbVep4UBRuPqjUQ=` |
| `GClass10.method_25()` → `IsShowElizaApp` | `#=q66wGW8bX1eJblqmtQvZv6Q==` |
| `LocalizedText.ToString` (`smethod_36` in clean) | `#=qTivbHO_rGrDAcTGXa1E4rg==` |
| `ScriptCharacter.firstName` (`gclass23_0`) | `#=qVcZOuQei28m8v7C2MtsdYA==` |
| `ScriptCharacter.lastName` (`gclass23_1`) | `#=q0XfuGfoz9CcWkzQ03CuAxw==` |
| `VirtualInput.SetCursor(Vector2)` | `#=qT_SlnKGmQty0k3OLe9Ydng==` |

Each view that uses one of these declares them as `const string` at
the top of the file — search the codebase for the obfuscated name to
find prior art.
