# Car detail window — the per-car inspector (both clients)

> **Status 2026-10-24: the REVIEWER half is BUILT** — the window
> (`CarDetailWindow.cs`), the right-click menu, the chain, the event list and
> the `held_by` data layer it reads; `--test-slot-buttons` drives and measures
> it (see §"Built in the reviewer"). **Not built:** the viewer's wiring, the
> peek card and the session file.
>
> Every entry below is a binding
> user decision taken in the discussion of 2026-10-24 (voice notes transcribed
> into this document; where a wording was ambiguous the reading is stated as a
> reading, never as a decision). It **supersedes** the three car slots of
> [`SIMULATIONS_REVIEWER.md` § "Three car slots"](SIMULATIONS_REVIEWER.md),
> which now describes the retired instrument.
>
> Neither the tape column nor the `/state` keys this needs exist yet. When
> they land, [RECORD_PLAYBACK.md](RECORD_PLAYBACK.md) and
> [REST_API.md](REST_API.md) must be updated from §"The chain (row 2) and what
> it requires" and §"What this costs the sim, the tape and the API" below —
> those documents describe what exists, so they are deliberately untouched
> now.

## What it is

One window per car the operator opened, showing **that car**: who holds it,
whom it holds, and the list of decisions and events that made it behave the way
it does at the frame on screen. The same window in the Simulationsviewer (live
`/state`) and in the Simulationsreviewer (a tape frame) —
**identical in both clients** (user decision 2026-10-24: "der Live-Viewer und
der Reviewer sollten sich grundsätzlich komplett gleich verhalten"). Today the
viewer has no per-car window at all and the reviewer has three fixed slots;
both give way to this.

The one honest asymmetry (accepted, not hidden): **a step row can seek in the
reviewer and cannot in the viewer**, because the live client has no tape to
jump to. There it is drawn inert (not styled as a link).

## Opening, closing, how many

| rule | decision |
|---|---|
| how a car is opened | **right-click the car on the map** — the ONLY gesture that opens a persistent window (2026-10-24). **Left-press on a car = the transient peek card** (§"The peek card", user decision 2026-10-24); everywhere else the left button keeps its old job: a pan drag. |
| the menu | a `PopupMenu` at the cursor. **No window open yet** → one entry: `New slot`. **Windows open** → one entry per open window, labelled with the car it shows (`#65 slow_car`), then `New slot`. |
| picking a window's entry | that window **replaces** its car with the clicked car. No swapping, no "slot" object beyond the window itself. |
| picking `New slot` | a new window, bound to the clicked car. |
| how many | **no cap** (2026-10-24: "die Begrenzung auf drei Fahrzeuge können wir komplett weglassen… er kann jederzeit Fenster schließen"). Twelve windows is the operator's business. |
| none open | nothing is on screen: no empty placeholder panels. |
| closing | the window's own close button removes it; closing the last one returns the client to "nothing opened". |

**Consequence of dropping the cap** (the guard was tuned for "a handful of
windows"): `EnforceWindows()` runs up to 60 separation passes and answers
`WINDOWS-TIGHT` when the main window cannot hold them all inside with the
5 px belt. That is the existing, binding behaviour and it now applies to N
windows: they stay inside, they stay clear of each other, and the shortfall is
named rather than hidden. Marker lane and chart, which today draw for ≤ 3
selected cars, draw for every open window — one colour per car, the car's own
`color` from the tape / `/state`.

## Layout, top to bottom

Everything scales with the client's existing HUD factor (one factor for fonts
and the boxes holding them — [SIMULATIONS_REVIEWER.md § "One fixed HUD
size"](SIMULATIONS_REVIEWER.md)); the sizes below are the unscaled design
values.

| # | row | content | font |
|---|---|---|---|
| 0 | native title bar | `#65 slow_car` — identity only. **A car the scenario never named reads `#65 no-name`**: a test label (`24/27`) is not a name, it says which scenario the car belongs to, and the readout and the Test panel already say that (user decision 2026-10-24). **The footprint is dropped entirely.** | OS |
| 1 | header row | `[sprite]` · **`65 km/h`** · ●brake ●blinker · `x y h` right-aligned | **1.5 × body** (`HeadFontPx` 36) |
| 2 | chain | `whom I hold up → me → who holds me up` — see below | `ChainFontPx` 32 |
| — | divider | | |
| 3 | event list | **the current event in the middle**, `AroundEvents` = 10 of the car's events on each side of it; the anchor row gets a band of colour and a ▸, earlier lines are dimmer, later lines dimmer still (they are the future of this frame) | `EventFontPx` 30 (address column 26, seconds 24) |
| 4 | status line | five icon-only controls, no words: **follow**, **centre**, then **↑ earlier / ↓ later / ◎ back to the current event**; the rule owning the cap (`avoid#26`, `free`) at the far right | `IconPx` 34 in a `StatusBtnPx` 52 box |

There is **no class line** and the constants are the state after the first
operator review (below) — the window opened with a class word, a 24 px chain
and a 24 px event list, and all three were wrong.

## Changed by the first operator review (2026-10-24, from the live window)

The reviewer ran, the operator looked at it and said what was wrong; all six
were true of the build, and each is now what the table above states.

| complaint | what it was | the fix |
|---|---|---|
| "the icons of the cars are stretched" | `CarSpriteView` drew every sprite into its SQUARE box, so a 4.4 × 1.8 m sedan came out as a bus and the truck as a squashed van | the window is handed the vehicle's footprint (`Config.SizeForColor`) and draws the TEXTURE into a rect of that aspect, long side along the row, rotated to the heading |
| "the chain uses too small fonts" | the chain ran at the 24 px body size under a 36 px speed | `ChainFontPx` 32 |
| "increase the font-size of the event log" | rows at 24 px read as footnotes | `EventFontPx` 30 (address 26, seconds 24) |
| "prefer the cars' names over their number" | the chain said `#27 → me → #61` | each entry is the **role the scenario gave it** (`slow_car`, `oncoming_car`) — the uid stays inside the token for the click/hold; a car the run never named keeps its number, and a test label like `20/27` is NOT a name (it would read as noise in a chain), so those fall back to `#uid` too |
| "find icons for the follow and centre buttons" | two bare words | `assets/ic_follow.svg` (crosshair, centre taken, motion chevrons) and `assets/ic_centre.svg` (plain crosshair), rasterised to `IconPx` through an `AtlasTexture`; the word stays, because an icon nobody has to learn is an icon nobody learns. Godot's own icon set is an editor resource (`GD.GetEditorIcons()`), so the client owns these two |
| "omit the class name of the car" | a line reading `heavy_truck` above the sprite | gone — the sprite IS the class |

## Why the big number and the dots are what they are

- **The big number is speed in km/h**; the red dot is the brake engaged, the
  yellow dot a blinker (confirmed 2026-10-24). The car's identity colour needs
  no chip of its own any more: the **sprite carries it**.
- **Sprite**: the car's own PNG, **rotated to the heading** `h` (user liked the
  idea on review), sized to sit in row 2 beside the speed — derived from the
  row's font size, not a constant.
- **Buttons in the status line, not the title bar**: Godot's title bar is OS
  chrome and cannot host Controls, so the fallback the operator offered
  ("eine Statuszeile unterhalb der Eventliste") is the design.
- `follow` is a toggle, `centre` momentary — the same two instruments the slots
  carry today. In the live viewer `follow` means the camera follows that car;
  in the reviewer it means the camera follows the tape pose of that car. Same
  word, same meaning (2026-10-24).

## Resizing

The window is resizable. **Rows 1–3 and 5 keep their height; the event list
takes every pixel of growth and shrinkage** (user decision 2026-10-24). Default
size = the static part plus **exactly three event rows**.

That means a real layout (`VBoxContainer`: fixed rows at their natural height,
the `ScrollContainer` around the list with `Expand | Fill` vertically) instead
of the slots' per-frame `Label.Size` stamp from `GetVisibleRect()`.

## Copying an event out for analysis

**Right-click an event row** and the whole address of that event goes to the
clipboard (user decision 2026-10-24, three sentences that turn out to be one
rule):

```
tape_T_249_0_20261024_204743.txt -> #32 -> #34291  571.53 s  [R1] off-road at (579, 1128)
└───────────── tape ─────────────┘   └ car ┘   └────────── the row, as painted ──────────┘
```

- **The tape comes first** because a line without its run cannot be re-watched —
  the point of copying one is to ask *why it happened*, and an answer has to be
  able to go back and look.
- **The car id** (`#32`, plus the role name when the cast has one) came from the
  operator's own correction: *"you are right, we also need the car id."* A row
  about "the parked car" is useless without saying which car that was in the
  run. `no-name` stays off the clipboard: a filler word in an analysis line is
  noise, and the uid is the fact.
- **The row is spelled exactly as the window paints it** — address, seconds,
  text — so a paste can be found again by eye in the window.
- The window's status corner answers `copied → clipboard` for a moment: the hand
  that clicked needs to know it worked, and the tape is on the clipboard rather
  than on the screen.

Measured (the probe right-clicks the anchored row through the real input path and
prints what the clipboard holds):

```
DETAIL-PROBE open #32 (no-name) … title='#32 no-name'
DETAIL-PROBE right-clicked the current row; clipboard now reads:
  tape_T_249_0_20261024_204743.txt -> #32 -> #34291  571.53 s  [R1] off-road at (579, 1128)
```

## The peek card (left-press)

A **transient** car detail card (operator proposal, 2026-10-24): left-press a
car and see it, release the button and it is gone.

| rule | content |
|---|---|
| trigger | **left button DOWN on a car** → the card appears at the cursor; **button UP → gone**, wherever the pointer ended up. Movement past the hand-over threshold retires it (§"The card and the pan"). |
| shape | a `Window` with **`Borderless`** — no title bar, no status line, no scrollbar, no buttons, not resizable. Nothing has to be re-implemented, because there is nothing to re-implement: it is undraggable, unresizable, and the hand holding the button is its close button. |
| content | exactly the **static section of the detail window**: header row (sprite at the vehicle's own proportions, `65 km/h`, ●brake ●blinker, `x y h`) · chain line · divider · **one event line** — the event the car is acting under at the frame (live: the newest). No list, no older entries, no paging controls. |
| size / place | fits its content; anchored beside the cursor, flipped when it would run past an edge; registered with the window guard, so it stays inside the main window. |
| lifetime | never in the session file, never one of the persisted `windows`, never a marker-lane or chart subject. It observes; it does not select. |
| both clients | identical in viewer and reviewer (the binding rule). |

This is the borderless technique the **persistent** window deliberately declined
(there it would cost us our own drag, close and chrome math) — here it is free,
because the card has no chrome to replace.

**The card and the pan — settled (user decision 2026-10-24: "c").** The left
button was already the map pan, and the three candidate rules were:

| # | rule | verdict |
|---|---|---|
| a | show the card **and** pan from the same press | rejected — the card flies across the map while panning |
| b | show the card and **suppress** the pan for that gesture | rejected — you could no longer grab the map where a car sits, and with 107 cars on screen that is most of the map |
| **c** | the card appears on press; past a small movement threshold the **card retires and the pan takes over** | **chosen** — a deliberate stationary press peeks, a drag pans, and nothing is ever suppressed |

So the gesture is a **hand-over**: `peek → pan` within one press, never both, never
blocked. The threshold is one named constant (`PeekGiveUpPx`, starting at 6 px —
car bodies are ~30 px long at the default zoom, so 6 px is clearly "the hand
moved to drag" and clearly not "the pulse jittered"); the probe
`--test-peek-pan` presses on a car, walks the pointer 0/4/8/40 px and prints
`card visible · pan active · which took the press` at each step, so the number is
measured rather than argued about.

**Shown immediately** on button-down — no delay. A fast pan that happens to
start on a car flashes the card for a frame or two; making every deliberate peek
wait ~80 ms to avoid that is the worse trade (assistant's recommendation,
accepted with "c").

## The chain (row 2) and what it requires

Read left → right: **whom I hold up → me → who holds me up** (the operator's
wording: "ganz links: wen halte ich auf … ganz rechts: durch wen werde ich
aufgehalten"). `me` is static text; the two ends are the two directions of the
same negotiation.

The list form (user decision 2026-10-24): when a side holds more than one car,
**show the whole list**, not one name and `+2`:

```
holding up: #71, #68        me        held up by: #59 → #61 → #44
```

- **left** = every car whose `held_by` is me, directly — a flat list, uid order;
- **right** = my direct holder first, then the **walk** away from me, `→` reading
  "…which is itself held up by" (so the queue I am stuck behind is on one line).
  *My reading of "die Liste anzeigen", not a stated decision: the transitive
  walk goes on the right, the direct list on the left.*

- **Today this is a guess and must stop being one.** `Car.ActiveCapKey` is a
  *rule* key — `r32#26`, `passage#12`, `avoid#12`, `r14#<node>#<uid>` and,
  undeniably, `r32#passive`, which names nobody. The reviewer derives both
  directions with `CapKey.EndsWith(uid)`, which is wrong for every key that
  does not end in a uid and O(cars) per panel per frame besides.
- **The sim answers the fact**: per car, `held_by` (uid, `0` = nothing holds me)
  and `res` (the resource key I lost to). "Whom I hold" and the full list are
  then a **walk** of `held_by` — cheap, and it needs no second field that could
  contradict the first.
- A walk that returns to the car itself **is the R28 lockdown**
  (`FindLockdownCycle`). The chain row prints `LOCKDOWN` there instead of
  drawing an arrow into itself. Measured on the full-suite tape, a walk closing
  on itself is COMMON (7 788 substeps where two cars hold each other) and is
  not by itself a lockdown — so the walk carries a visited set and cuts the
  branch, and only what R28 itself declares is printed as a lockdown.
- **Ids in the chain are interactive** (user decision 2026-10-24):
  - **press and hold** an id → that car is highlighted **on the map** for as
    long as the button is down (a red box around its body — the reviewer draws
    it from the tape pose and `Config.SizeForColor`, the viewer from `/state`);
  - a **short click** → this window switches to that car.
  Implementation: a `RichTextLabel` with `[url=#71]#71[/url]` per id, so the
  engine does the per-token hit testing. What it offers (verified against the
  Godot 4.7 class reference, not assumed): the signals **`meta_clicked`**,
  **`meta_hover_started`** and **`meta_hover_ended`** — and there is **no**
  `get_hovering_meta()` method to query at press time, so the press-hold state
  is latched from `meta_hover_started`/`_ended` and read when the button goes
  down. Those hover signals have open reliability reports
  ([godotengine/godot-proposals#7477](https://github.com/godotengine/godot-proposals/issues/7477)),
  so the first probe in this window's family proves them on screen; if they
  flake, the fallback is one small `Button` per id in an `HBoxContainer` — more
  nodes, but the hit area is Godot's own.

## The event list (row 3)

| rule | decision |
|---|---|
| column 1 | **the tape's `step`** — the host's absolute substep counter, what you would quote in a bug report. Displayed with the run-relative time beside it, `(step − firstStep) × dt_fixed`, because that is the number the scrub bar shows and an absolute step means nothing without it. |
| order | **neither**: the list is centred on the event the car is acting under at the frame on screen, `AroundEvents` (10) before it and 10 after — see the row-cap row below. |
| click on the step number | **seek** the tape to that frame (reviewer only; inert in the live viewer). Step → index is the existing binary search over the tape index. |
| scope | this car's own decisions **and the events that come from outside** — explicitly including "someone drove into me" (2026-10-24). |
| list model | **not a dump**: a window of `AroundEvents` (10) events on each side of **the event the car is acting under at the frame on screen** — the newest at or before the cursor. The operator reads the story around the moment they are looking at, and pages through the rest with the ↑/↓/◎ controls in the status line. Supersedes the "newest 100 + `show all`" plan (user decision 2026-10-24, from the live window: "do NOT show all events — give the user the chance to see the events AROUND the currently selected frame/time"). For the numbers that sized the old plan: median 6 events per car, a suite run peaks at 32, the record 953. |
| paging lives in the status line | A footer button at the ends of the scrolled list is off-screen exactly when the list is centred on the anchor — measured: the probe's press for "earlier events" landed at local **y = −192**, above the window, and nothing paged. The footers in the list are plain text now (`↑ 128 earlier events`), and the controls are always visible below. |

**"From outside" is already recorded — verified, not assumed.** The crash
episode logs a decision on **every** car in contact, each naming the other
(`[R2] CRASH: contact with car 7 (my speed 12.4 km/h)`, `CarCollisions`’
contact loop), and `crash_episodes` + `contact_with` are already in `/state`.
So the victim's `crash: hit by car 26` line needs **no sim change** — only a
list that shows the events instead of one collapsed line. (An earlier draft of
this document claimed the victim only got a counter; that was wrong.)

## What this costs the sim, the tape and the API

**Status 2026-10-24: the first five rows are BUILT** (this is the data layer the
window reads); the rest of the document is still open.

| where | change | state |
|---|---|---|
| `Car` | `HeldByUid` — the CAR owning the active cap, 0 = nobody. Set beside `ActiveCapKey` where a cap is taken or replaced, cleared when the brake decision is released. | **built** |
| `CarCollisions` | `OfferCap(…, int by = 0)` carries the actor as a VALUE from the rule that already knew it (`_v1By`, `byUid32`, `otherUid`, `blk.Other`, `blockedBy`, `e14.Value`) — no reader has to parse a uid out of a rule string again. The R32 creep now names the car that makes the stretch tight instead of leaving the `-1` sentinel (`avoid#-1`), and keeps `held_by = 0` there, because the KERB is what holds a creep — a chain that named a driver would invent one. | **built** |
| `Tape.cs` | the per-car record is `…,color,lamps,heldBy,capKey` — **`heldBy` before `capKey`**, because a cap key can contain a comma (`r32#12,passage#4`) and the reader joins everything from the cap key to the end of the record: appending after it would have swallowed the number into the key. Tape **generation 4**; generations 2 and 3 still load, with `heldBy = 0` and the same cap key (`DrivingGame.Sim.Tests/TapeHeldByTests.cs`). | **built** |
| `/state` | `"held_by"` (uid) and `"cap_key"` (the rule) per car, **in both payload builders** — the followed-car branch and the fleet branch — per the rule the camera-focus work established. (`cap_key` was never in `/state` at all before this, though the tape had it since October.) | **built** |
| `POST /tape/frame` | `"held_by"` beside `"cap_key"`, so a client reading a tape frame over HTTP gets the same contract as one reading `/state`. | **built** |
| `GET /car/{uid}/decisions` | answers `step` beside `t` + `msg` (it already existed, its comment naming the car detail window), so live rows and review rows have one shape. | **built** |
| the reviewer's own tape reader (`MapRenderer.review.cs`) | it parses tapes itself — it needed the same four-generation rule, or the new number would have arrived inside `CapKey`. | **built** |
| the crash | nothing to build — see above. | — |

Measured on the first runs recorded with the field. A single
`park_block_overtake` (`tests/tape_T_248_17_…`, 968 substeps) reads
`car 2 held by 1 (avoid#1)` for 312 substeps and `car 1 held by 2 (avoid#2)` for
161 — and never both in the same substep.

**The full suite says that last part is NOT a general law**
(`tests/tape_T_249_0_…`: 126 152 car records, all generation 4, 46 779 substeps
naming a holder, **7 788 of them with A and B holding each other** in the same
substep, **0 with a car holding itself**). A mutual pair is ordinary negotiation
— two cars in one pinch each seeing the other as its own threat — so:

- `held_by` is a **per-direction fact about one car**, not a relation that
  implies anything about the other end;
- the chain walk needs a **visited set from the first hop** (a cycle is common,
  not an emergency). `LOCKDOWN` stays what R28's own test defines it as — a
  cycle with no base case — and is NOT the same thing as "the walk closed".

## Geometry and session memory

Persisted to **`user://car_windows.cfg`** (Godot's `ConfigFile`; the client
writes no settings at all today, so this is the first — the resolved absolute
path is printed when it is written, so it can be found and deleted). Written on
window create/close/resize-end, on tape load, on scrub pause, and at quit —
**not** per frame, and CrashDumper gets the same snapshot so a crash does not
lose the session either.

What is remembered, on close (user decision 2026-10-24: "der gesamte Zustand
des Reviewers… dieselbe Stelle, direkt weiterarbeiten"):

| key | content |
|---|---|
| `tape` | absolute path **plus** the tape header's `map` + `map_hash` + frame count, so a moved, replaced or re-recorded tape is detected instead of silently restoring a cursor into the wrong run |
| `cursor` | the step the cursor sat on (restored by binary search to an index) |
| `windows` | one entry per open window: `uid` + position + size, in stacking order |
| `geometry` | the ordered list of window **rectangles** the operator last used, independent of any uid — the placement template a fresh session (or a session on a different tape) draws from, and where "find a free spot" starts looking |
| `camera`, `zoom` | the view the operator left, in metres and px/m |

Restore rules:

1. **Same tape** (path readable, `map_hash` matches, step count ≥ cursor): the
   cursor, the camera and every window whose `uid` actually appears in that
   tape are restored; a uid that is gone is skipped and the skip is printed —
   uids are per-run, so replaying yesterday's list against today's tape is a
   different cast.
2. **Different or missing tape**: geometry and camera are restored, windows are
   not. The client opens as it does today (newest tape / no tape) and says why
   the windows did not come back.
3. **A restored camera is a hand-pin.** The existing priority chain — a slot's
   `follow` > the hand > `--center`/`--review-name` > the scenario's declared
   focus ([TESTING.md § Camera focus](TESTING.md)) — means a restored view stops
   the recording from re-claiming the frame at the next scenario boundary. That
   is the intent of "I am exactly where I was", and it is the reason the camera
   is stored at all.
4. The **live** client has no tape and no cursor: it persists `windows`,
   `camera` and `zoom` in a second section of the same file.

## Built in the reviewer (2026-10-24)

What exists, and how each claim was measured (`--test-slot-buttons`, which kept
the flag of the slot test it replaces and now drives the window):

| claim | the line the probe prints |
|---|---|
| a window per car, no cap | `DETAIL-OPEN #28 (oncoming_car) · 1 window(s) open · at (14, 206) size (620, 316)`, then a second `DETAIL-OPEN #27 … · open=2` |
| right-click names the windows | `DETAIL-MENU car #26 (20/27) at (2510,1148) · 3 entries (2 window(s) already open)` — one per open window plus `New slot` |
| the header fits a real car | `speed='0 km/h' rect=(94, 59), (122, 50) … pose='x 101.8 y 228.8 h 0°' rect=(377, 67), (231, 34) · head=(596, 72)` inside a 620 px window |
| resizing grows ONLY the list | `resized +220px: header 176 -> 176 px (UNCHANGED) · list 90 -> 310 px (took the growth)` |
| follow is the pin, riding the car | `DETAIL-FOLLOW #28 ON — the pin rides the car · followed=28` and `cam moved=(1.75,16.20) · pin=(101.75, -228.799)` — the pin IS the car's pose |
| a step row jumps the tape | `DETAIL-SEEK step 17470 -> frame 17469/35482 (was 21000/35482) · SIM 291.172 s`, pressed on the row it names (`seek of #17470`) |

Four things this work settled, each because a first attempt looked right and
measured wrong:

- **`RichTextLabel` in an `HBoxContainer` has no natural width.** The two lamp
  dots as one bbcode label were squeezed to 1 px wide, wrapped into 124 px of
  height, reported that as their minimum — and the window grew to 827 × 1200 px
  for a car. Two `Label`s, one glyph each, is the container-friendly form.
- **`ClipText = true` on a `Label` in a container collapsed the header row** to
  1 px labels: the text was set, the row was 72 px high, and the picture showed
  a sprite with nothing beside it. Clipping is the subwindow's job.
- **The minimum a container reports is what its children WANT**, not what the
  operator asked for. Deriving `MinSize` from it made the window demand the
  width of the longest event line; the floor is a constant, and a longer line
  scrolls (`HorizontalScrollMode` stays on).
- **A pushed mouse event does not reach this client's `_Input` once satellite
  windows exist** — measured: not ONE button event arrived at `_Input` across a
  whole probe run, while presses aimed at a window's buttons were answered by
  that window. So the probe feeds the pick a constructed `InputEventMouseButton`
  and its own line says the delivery is Godot's, verified by hand; what is
  measured is the client's half: the 12 m pick radius, the decline over the
  instrument strip, and the menu the pick opens.

## Implementation surface

- New `CarDetailWindow.cs` — one class, one layout, both clients; a `Window`
  subclass in the file's own right, the way `MinimapNode` and `ReviewScrubZone`
  are done in this client.
- Fed by a source seam (the same seam that already splits the two clients:
  "the map drawing is unchanged, only the SOURCE of the cars changes"):
  frame-at-cursor · `decisions(uid)` · `seek(step)` · `pose(uid)`. Viewer:
  `/state` + `/car/{uid}/decisions`, `seek` absent. Reviewer: the cached tape.
- Every window **registers** with `RegisterWindow` so the guard (inside-ness +
  the 5 px belt) holds for N of them; a new window still picks a *free* spot
  deliberately (the next unused rectangle of the `geometry` template, otherwise
  the first position that collides with nothing) rather than relying on
  `EnforceWindows` to shove it there. With no cap, "slot N has a remembered
  place" becomes "window N in the list has a remembered place" — the template
  is the list, not a fixed trio.
- Probes: `--test-slot-buttons` retires; the family that replaces it drives a
  detail window — open by right-click, resize and measure that only the list
  grew, click a step and print the cursor before/after, press-hold a chain id
  and print the highlighted uid, and `--test-peek-pan` for the peek→pan
  hand-over. Each turns a claim into a measured line, as the rest of the family
  does.
- Row nodes: a page is 21 rows plus up to three footer lines, so the list is
  ordinary `Label`s and no virtualisation is needed. The client hands the whole
  history in once (953 rows is a few tens of kilobytes of strings) and the
  window slices what it paints; if that ever measures slow, the slicing is the
  one function to replace.

## What changes in the other documents when this is built

- `SIMULATIONS_REVIEWER.md`: §"Three car slots" and the ⚠ row in
  §"Implementation status" are replaced by a pointer here; the marker-lane
  sentence ("clicking a car slot") becomes "opening a car detail window".
Status of each, 2026-10-24:

- ✅ `RECORD_PLAYBACK.md` — the `heldBy` column and generation 4 are documented.
- ✅ `REST_API.md` — `/state` per-car `held_by` + `cap_key` (both builders),
  `step` in `/car/{uid}/decisions`, `held_by` in `POST /tape/frame`.
- ✅ `SIMULATIONS_REVIEWER.md` — the slot section is marked retired, the camera
  precedence lists the follow as the hand's pin, the probe list names what
  `--test-slot-buttons` drives now.
- ✅ `TESTING.md` — "the hand always outranks the run" and the identity-line
  example (`#2 slow_car`; an unnamed car reads `#2 no-name`).
- ⏳ `GODOT_FRONTEND.md` § "What the client draws today" — waits for the
  viewer's windows (they do not exist yet, and that list is code-verified).
- ✅ `README.md` — the reviewer's description says a window per car, and the doc
  index points here.
- `AGENTS.md`: nothing — no rule changed.
