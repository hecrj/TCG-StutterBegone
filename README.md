# Canvas Splitter (TCG Card Shop Simulator)

BepInEx 5 plugin. Generalizes the **TooltipStutterFix** "dedicated canvas" idea to the
whole game UI.

## Why

The game pools **every** full-screen UI under one shared `ScreenSpaceOverlay` canvas
(`Canvas`). With a full mod set that canvas holds ~144,000–150,000 `CanvasRenderer`
components (only ~50–140 active at a time):

| Subtree | CanvasRenderers | Notes |
|---|---|---|
| `CheckPriceScreen_Grp` | ~47,800 | vanilla screen, grown by EPL (one panel per card/item) |
| `RestockItemScreen_Grp` | ~26,700 | vanilla screen, **path-referenced by EPL/ShopOS** |
| `RestockItemBoardGameScreen_Grp` | ~26,700 | vanilla screen, grown by EPL |
| `PlayCardSetUIScreenGrp` | ~10,800 | |
| `DeckEditCanvasGrp` | ~5,800 | |
| `CustomShop_SharedScreen_Grp` | ~5,200 | **created by EPL** for custom-shop mods |
| `FurnitureShopUIScreen_Grp` | ~4,700 | |
| … ~47 more screens … | | |

Unity invalidates and rebuilds at the **canvas** granularity, so anything that dirties that
canvas pays the full ~144k cost. Two things make it dirty:

1. **The tooltip** (per-frame churn) — every hover add/remove rebuilds the canvas ~100x/sec.
   Fixed by moving the tooltip onto its own tiny canvas (this plugin does it; see below).
2. **The big screens** (opening/interacting) — each rebuilds all ~144k. Fixed by moving each
   big screen onto its own canvas, so only that screen's canvas rebuilds.

This plugin does **both**: it splits the big screens *and* moves the tooltip off the shared
canvas (the tooltip fix is ported in from **TooltipStutterFix**, so you no longer need that
plugin). One plugin, full fix.

> **Use just this plugin.** The tooltip fix is built in (`TooltipDedicatedCanvas = true` by
> default), so the hover stutter is fixed here too. **Disable `TooltipStutterFix`** to avoid two
> plugins fighting over the tooltip — this plugin detects if the tooltip was already moved and
> skips it, so it's safe to leave both on, but only one is needed.

## Modes

- **`Reparent`** (default) — create a new root `ScreenSpaceOverlay` canvas per qualifying
  screen and move the screen group onto it (the original canvas's `CanvasScaler` is copied,
  so it renders identically). This is the same proven technique as the tooltip fix.
  By default it **excludes** the screens that other code locates by path
  (`RestockItemScreen_Grp` — used by EPL/ShopOS via
  `GameObject.Find("Canvas/RestockItemScreen_Grp")`) and the mod-created
  `CustomShop_SharedScreen_Grp`, so nothing breaks.
- **`Nested`** (experimental) — add a nested `overrideSorting` `Canvas` to each screen **in
  place**. No hierarchy/path change, so it can move *every* screen (including
  `RestockItemScreen_Grp`) without breaking any `GameObject.Find`. Needs in-game visual
  verification (scaling/rendering of a nested overlay canvas).
- **`Off`** — do nothing.

## Tooltip fix (built in)

The hover stutter you originally reported is caused by the **tooltip** churning the shared
canvas: every hover over a shelf item adds/removes tooltip elements, which dirties the ~144k-
element canvas ~100x/sec. This plugin moves the tooltip UI (`InputTooltipListDisplay`) onto its
own tiny dedicated overlay canvas (`CanvasSplitter_TooltipCanvas`, sort 2000, above everything).
A tooltip transition then rebuilds ~20 elements instead of ~144k. **Tooltips stay fully visible**
— only the canvas they live on changes. This is the same proven technique as `TooltipStutterFix`.

Toggle it with `TooltipDedicatedCanvas` (default `true`). If you also run `TooltipStutterFix`,
it moves the tooltip first and this step detects that (tooltip no longer under the shared
canvas) and skips — no double move. Prefer running just this plugin.

## Config (`BepInEx/config/hover.canvas.splitter.cfg`)

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | `true` | Master switch. |
| `Mode` | `Reparent` | `Reparent` / `Nested` / `Off`. |
| `GiantCanvasName` | `Canvas` | The shared canvas to split (falls back to the biggest canvas). |
| `MinCanvasRenderers` | `2000` | Only split screens with ≥ this many CanvasRenderers. Lower = more moved, higher = less risk. |
| `ExcludeNames` | (see source) | Comma-separated screen names to skip. Defaults protect path-referenced + mod-created screens. |
| `ApplyDelayFrames` | `10` | Extra frames to wait after the canvas is found, before the settle wait begins. |
| `BaseSortingOrder` | `1000` | Base `sortingOrder` for new screen canvases (sibling order added to preserve layering). Keep above the shared canvas (0) and below the tooltip canvas (2000). |
| `ProbeInterval` | `30` | Frames between canvas-size samples while waiting for the UI to finish building. |
| `SettleFrames` | `90` | How long the element count must be unchanged before splitting. |
| `MaxWaitFrames` | `900` | Give up the settle wait after this and split anyway (also scales the loading-done hard cap). |
| `MinTotalCanvasRenderers` | `5000` | The canvas must reach this many CanvasRenderers before it counts as "built". |
| `UseEplSignal` | `true` | Use EPL's `OnBundleLoadingComplete` event to detect the end of loading. |
| `TooltipDedicatedCanvas` | `true` | Move the tooltip UI onto its own tiny canvas (the hover-stutter fix, ported from TooltipStutterFix). Tooltips stay visible. |

> **Timing matters (two gates).** The game shows a loading screen that can last *well over
> 15 s* with many content packs; during it the shared canvas only holds the base UI
> (~8 children / ~2k CR). This plugin waits in two phases:
> 1. **Loading done** — subscribes (via reflection, no hard dependency) to EPL's
>    `OnBundleLoadingComplete` event, which is *hot/cold* (fires immediately if loading already
>    finished). Without EPL it falls back to the canvas reaching `MinTotalCanvasRenderers`.
> 2. **Canvas built** — waits until the element count **settles** (unchanged for
>    `SettleFrames`) after loading, since the shop panels are added after loading.
>
> You'll see `subscribed to EPL OnBundleLoadingComplete`, then `EPL OnBundleLoadingComplete
> fired`, then `building: children X->Y, CanvasRenderers A->B` lines, then
> `CENSUS BEFORE` / `CENSUS AFTER`.

## How it keeps screens interactive
A `ScreenSpaceOverlay` canvas only receives clicks/hovers if it has a
**`GraphicRaycaster`**. This plugin adds one to every canvas it creates (settings
copied from the shared canvas) and **registers it with the game's
`RaycasterManager`** (via reflection) so the game's `SetUIRaycastEnabled` toggles it
together with the rest. (Earlier versions omitted the raycaster, which is why
reparented screens like the price-check and board-game shop rendered but were not
clickable.)

## Will it break anything?

- **`Reparent` (default): low risk.** Reparenting is the same proven move as the tooltip fix.
  The only screens referenced *by path* are `RestockItemScreen_Grp` (EPL/ShopOS) and the
  mod-created `CustomShop_SharedScreen_Grp`; both are excluded by default, so no
  `GameObject.Find` breaks. The game itself only references the `Canvas` **root** (to
  enable/disable it on scene transitions), which this plugin leaves in place.
  If you remove `RestockItemScreen_Grp` from `ExcludeNames`, you risk breaking EPL/ShopOS
  custom-shop setup (it `Find`s that screen once at init).
- **`Nested`: no path risk, but verify visuals.** Because nothing is reparented, no
  `GameObject.Find` can break — you can move every screen. The uncertainty is whether a
  nested `ScreenSpaceOverlay` canvas renders/scales exactly like before; check each screen
  in-game.

## Building

```
dotnet build -c Release -p:GameDir="C:/path/to/TCG Card Shop Simulator" -o out
```
Deploy `out/CanvasSplitter.dll` to `BepInEx/plugins/CanvasSplitter/`.

## Uninstall

Delete `BepInEx/plugins/CanvasSplitter/`. No game or mod files are modified.
