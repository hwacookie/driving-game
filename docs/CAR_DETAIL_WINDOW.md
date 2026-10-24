# Car detail window — the per-car inspector (both clients)

> **Status 2026-10-26: specified, NOT built.** Every entry below is a binding
> user decision taken in the discussion of 2026-10-26 (voice notes transcribed
> into this document; where a wording was ambiguous the reading is stated as a
> reading, never as a decision). It **supersedes** the three car slots of
> [`SIMULATIONS_REVIEWER.md` § "Three car slots"](SIMULATIONS_REVIEWER.md) —
> that section describes what is BUILT today and stays as the current state
> until this is implemented.
>
> Neither the tape column nor the `/state` keys this needs exist yet. When
> they land, [RECORD_PLAYBACK.md](RECORD_PLAYBACK.md) and
> [REST_API.md](REST_API.md) must be updated from §"The chain (row 3) and what
> it requires" and §"What this costs the sim, the tape and the API" below —
> those documents describe what exists, so they are deliberately untouched
> now.

## What it is

One window per car the operator opened, showing **that car**: who holds it,
whom it holds, and the list of decisions and events that made it behave the way
it does at the frame on screen. The same window in the Simulationsviewer (live
`/state`) and in the Simulationsreviewer (a tape frame) —
**identical in both clients** (user decision 2026-10-26: "der Live-Viewer und
der Reviewer sollten sich grundsätzlich komplett gleich verhalten"). Today the
viewer has no per-car window at all and the reviewer has three fixed slots;
both give way to this.

The one honest asymmetry (accepted, not hidden): **a step row can seek in the
reviewer and cannot in the viewer**, because the live client has no tape to
jump to. There it is drawn inert (not styled as a link).

## Opening, closing, how many

| rule | decision |
|---|---|
| how a car is opened | **right-click the car on the map** — the ONLY gesture that opens a persistent window (2026-10-26). **Left-press on a car = the transient peek card** (§"The peek card", user decision 2026-10-26); everywhere else the left button keeps its old job: a pan drag. |
| the menu | a `PopupMenu` at the cursor. **No window open yet** → one entry: `New slot`. **Windows open** → one entry per open window, labelled with the car it shows (`#65 slow_car`), then `New slot`. |
| picking a window's entry | that window **replaces** its car with the clicked car. No swapping, no "slot" object beyond the window itself. |
| picking `New slot` | a new window, bound to the clicked car. |
| how many | **no cap** (2026-10-26: "die Begrenzung auf drei Fahrzeuge können wir komplett weglassen… er kann jederzeit Fenster schließen"). Twelve windows is the operator's business. |
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
| 0 | native title bar | `#65 slow_car` — identity only. Class and footprint left the title bar; **the footprint is dropped entirely** (2026-10-26). | OS |
| 1 | class line | the vehicle class (`Config.VEHICLE_CLASS[color]`), left-aligned, its own line above the header | body |
| 2 | header row | `[sprite]` · **`65 km/h`** · ●brake ●blinker · `x y h` right-aligned | **1.5 × body** |
| 3 | chain | `whom I hold up → me → who holds me up` — see below | body |
| — | divider | | |
| 4 | event list | `#8803  brake: yield sign: car 26 (19 m out)`, newest FIRST, older ones below (scroll down into the past) | body |
| 5 | status line | `follow` · `centre` | body |

- **The big number is speed in km/h**; the red dot is the brake engaged, the
  yellow dot a blinker (confirmed 2026-10-26). The car's identity colour needs
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
  word, same meaning (2026-10-26).

## Resizing

The window is resizable. **Rows 1–3 and 5 keep their height; the event list
takes every pixel of growth and shrinkage** (user decision 2026-10-26). Default
size = the static part plus **exactly three event rows**.

That means a real layout (`VBoxContainer`: fixed rows at their natural height,
the `ScrollContainer` around the list with `Expand | Fill` vertically) instead
of the slots' per-frame `Label.Size` stamp from `GetVisibleRect()`.

## The peek card (left-press)

A **transient** car detail card (operator proposal, 2026-10-26): left-press a
car and see it, release the button and it is gone.

| rule | content |
|---|---|
| trigger | **left button DOWN on a car** → the card appears at the cursor; **button UP → gone**, wherever the pointer ended up. Movement past the hand-over threshold retires it (§"The card and the pan"). |
| shape | a `Window` with **`Borderless`** — no title bar, no status line, no scrollbar, no buttons, not resizable. Nothing has to be re-implemented, because there is nothing to re-implement: it is undraggable, unresizable, and the hand holding the button is its close button. |
| content | exactly the **top section of the detail window**: class line · header row (sprite, `65 km/h`, ●brake ●blinker, `x y h`) · chain line · divider · **one event line** — the newest event at or before the cursor (live: the newest). No list, no older entries. |
| size / place | fits its content; anchored beside the cursor, flipped when it would run past an edge; registered with the window guard, so it stays inside the main window. |
| lifetime | never in the session file, never one of the persisted `windows`, never a marker-lane or chart subject. It observes; it does not select. |
| both clients | identical in viewer and reviewer (the binding rule). |

This is the borderless technique the **persistent** window deliberately declined
(there it would cost us our own drag, close and chrome math) — here it is free,
because the card has no chrome to replace.

**The card and the pan — settled (user decision 2026-10-26: "c").** The left
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

## The chain (row 3) and what it requires

Read left → right: **whom I hold up → me → who holds me up** (the operator's
wording: "ganz links: wen halte ich auf … ganz rechts: durch wen werde ich
aufgehalten"). `me` is static text; the two ends are the two directions of the
same negotiation.

The list form (user decision 2026-10-26): when a side holds more than one car,
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
  drawing an arrow into itself.
- **Ids in the chain are interactive** (user decision 2026-10-26):
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

## The event list (row 4)

| rule | decision |
|---|---|
| column 1 | **the tape's `step`** — the host's absolute substep counter, what you would quote in a bug report. Displayed with the run-relative time beside it, `(step − firstStep) × dt_fixed`, because that is the number the scrub bar shows and an absolute step means nothing without it. |
| order | newest at the TOP; older entries below, reached by scrolling down. |
| click on the step number | **seek** the tape to that frame (reviewer only; inert in the live viewer). Step → index is the existing binary search over the tape index. |
| scope | this car's own decisions **and the events that come from outside** — explicitly including "someone drove into me" (2026-10-26). |
| row cap | **the newest 100 rows**, then a footer row **`show all (853 more)`** which drops the cap for that window until the car changes (user decision 2026-10-26). Measured on the tapes we own: a normal full-suite run peaks at **32** events per car, so 100 rows is never reached there; the record is **953** (`#3` of the fig8 stress family, 951 of them `brake: …` at ~3.9/s), i.e. only the dense multi-minute stress runs need the footer. Median car: 6 events. |

**"From outside" must be recorded, not reconstructed.** A collision today
bumps the victim's `crashes` counter and nothing else. Spec: at the crash,
`RecordDecision` on **both** cars — `crash: hit by car 26` on the victim,
`crash: hit car 65` on the striking one. It lands in the existing event stream,
so the tape line `~uid=text~`, the reviewer's index and
`GET /car/{uid}/decisions` all carry it without a new format.

## What this costs the sim, the tape and the API

| where | change |
|---|---|
| `Car` / `SimEngine` | per car per substep: `HeldByUid`, `HeldByRes` — set where the cap is awarded (`CarCollisions` `OfferCap`/`ActiveCapKey` assignment), cleared with it. |
| `CarCollisions` | `RecordDecision` on both cars at a crash. |
| `Tape.cs` | a new per-car field **inserted BEFORE `capKey`**. The reader joins *everything* from index 11 to the end as the cap key (`fld.Length >= 12 ? string.Join(",", fld, 11, …)`), so a field appended after it would be silently swallowed into the cap string. Old files keep parsing → tape generation **4**, same as the colour/lamps/capKey generations. |
| `GameApi` / `/state` | `held_by` + `held_by_res` per car **in both payload builders** (the followed-car branch and the fleet branch) — the rule the camera-focus work established: run/car state must not depend on which builder answered. |
| `GET /car/{uid}/decisions` | exists already (its comment says "car detail window") but answers only `t` + `msg` → add `step`, so live rows can be the same shape as review rows. |

## Geometry and session memory

Persisted to **`user://car_windows.cfg`** (Godot's `ConfigFile`; the client
writes no settings at all today, so this is the first — the resolved absolute
path is printed when it is written, so it can be found and deleted). Written on
window create/close/resize-end, on tape load, on scrub pause, and at quit —
**not** per frame, and CrashDumper gets the same snapshot so a crash does not
lose the session either.

What is remembered, on close (user decision 2026-10-26: "der gesamte Zustand
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
- Row nodes: 100 rows per window are ordinary `Label`s. The `show all` path is
  what can reach ~950 rows in ~13 windows at once, so the list is built through
  one function that a pooled/virtualised implementation can replace later
  without touching the layout — decided only when a `show all` on a stress tape
  is measurably slow, not before.

## What changes in the other documents when this is built

- `SIMULATIONS_REVIEWER.md`: §"Three car slots" and the ⚠ row in
  §"Implementation status" are replaced by a pointer here; the marker-lane
  sentence ("clicking a car slot") becomes "opening a car detail window".
- `RECORD_PLAYBACK.md`: the per-car column and generation 4.
- `REST_API.md`: `/state` per-car `held_by` (+ `held_by_res`) in both builders,
  `step` in `/car/{uid}/decisions`.
- `GODOT_FRONTEND.md` § "What the client draws today": the viewer gains the
  windows.
- `TESTING.md`: the reviewer's identity-line example (title bar now reads
  `#2 slow_car`), and the probe list.
- `AGENTS.md`: nothing — no rule changes.
