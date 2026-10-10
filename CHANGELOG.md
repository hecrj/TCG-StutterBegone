# Changelog

User-facing changes only. For the full config reference, see [README](README.md).

## v1.2.0

- **Fixed:** The plugin no longer gives up waiting for content loading after a fixed time.
  It now waits as long as needed, so it won't split the canvas before it's fully built
  (relevant on very long loading screens with a large mod set).

## v1.1.0

- **Added** `RescanCanvasesKey` (no default). Set a key and press it to shift new runtime
  canvases (e.g. a mod's overlay/modal such as Binder Overhaul's filter/sort modal) above the
  reparented screens, fixing z-order conflicts where a mod's UI appeared behind a screen.
- **Removed** the `Mode` setting (`Reparent`/`Nested`/`Off`). The plugin now always reparents
  screens (the previous default behavior).

## v1.0.0

- Initial release. Fixes the hover stutter by reparenting every top-level UI screen onto its
  own small canvas, so a change only rebuilds that screen's canvas instead of the giant shared
  one (~144k CanvasRenderers). Tooltips stay fully visible.
- Config options to enable/disable, exclude specific screens, and tune timing.
