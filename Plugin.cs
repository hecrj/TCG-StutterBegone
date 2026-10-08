using System;
using System.Collections;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.UI;

namespace CanvasSplitter
{
    /// <summary>
    /// Generalizes the TooltipStutterFix "dedicated canvas" idea to the whole UI.
    ///
    /// The game pools EVERY full-screen UI under ONE shared ScreenSpaceOverlay canvas
    /// ('Canvas'). With a full mod set that canvas holds ~144,000-150,000 CanvasRenderers
    /// (CheckPriceScreen ~47k, RestockItemScreen ~26k, RestockItemBoardGameScreen ~26k,
    /// PlayCardSetUI ~10k, deck editor, furniture shop, ... plus one panel per card/item in
    /// every content pack). Only ~50-140 are active at a time, but Unity invalidates and
    /// rebuilds at the CANVAS granularity, so anything that dirties that canvas pays the full
    /// ~144k cost. The tooltip was the per-frame churn source (fixed by TooltipStutterFix);
    /// this plugin additionally isolates each big screen onto its own canvas so that
    /// opening / interacting with a screen only rebuilds that screen's canvas, not all 144k.
    ///
    /// Two modes:
    ///   Reparent (default) - create a new root ScreenSpaceOverlay canvas per screen and move
    ///     the screen group onto it (scaler copied, so it renders identically). This is the
    ///     same proven technique as the tooltip fix. By default it EXCLUDES the screens that
    ///     other mods locate by path (RestockItemScreen_Grp, referenced by EPL/ShopOS via
    ///     GameObject.Find("Canvas/RestockItemScreen_Grp")) and the mod-created
    ///     CustomShop_SharedScreen_Grp, so nothing breaks.
    ///   Nested (experimental) - add a nested overrideSorting Canvas to each screen IN PLACE.
    ///     No hierarchy/path change, so it can move EVERY screen (including RestockItemScreen_Grp)
    ///     without breaking any GameObject.Find. Needs in-game visual verification.
    ///
    /// This plugin only uses Unity types (no game-assembly coupling) and makes no Harmony
    /// patches. It runs once per scene load.
    /// </summary>
    [BepInPlugin("hover.canvas.splitter", "Canvas Splitter", "1.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        public enum SplitMode
        {
            /// <summary>No-op (original behavior).</summary>
            Off,
            /// <summary>(Default) Give each qualifying screen its own root overlay canvas.</summary>
            Reparent,
            /// <summary>Experimental: nested canvas in place, no path change.</summary>
            Nested
        }

        private ConfigEntry<bool> _enabled;
        private ConfigEntry<SplitMode> _mode;
        private ConfigEntry<string> _giantName;
        private ConfigEntry<int> _minCR;
        private ConfigEntry<string> _exclude;
        private ConfigEntry<int> _delayFrames;
        private ConfigEntry<int> _baseSortOrder;

        private Canvas _giant;
        private HashSet<string> _excluded = new HashSet<string>(StringComparer.Ordinal);

        // Default excludes protect screens that other code locates by path or that are
        // created by mods (timing-dependent). Reparent mode is safe with these excluded.
        private static readonly string DefaultExcludes = string.Join(",", new[]
        {
            "RestockItemScreen_Grp",          // EPL + ShopOS: GameObject.Find("Canvas/RestockItemScreen_Grp")
            "CustomShop_SharedScreen_Grp",    // created by EPL at runtime
            "InputTooltipListDisplay",        // handled by TooltipStutterFix
            "TooltipStutterFix_DedicatedCanvas"
        });

        private void Awake()
        {
            _enabled = Config.Bind("General", "Enabled", true,
                "Master switch. False = original (single giant canvas) behavior.");

            _mode = Config.Bind("General", "Mode", SplitMode.Reparent,
                "Reparent = new root overlay canvas per big screen (proven visuals; default). " +
                "Nested = nested canvas in place, no path change (experimental, verify visuals). " +
                "Off = do nothing.");

            _giantName = Config.Bind("General", "GiantCanvasName", "Canvas",
                "Name of the shared UI canvas to split. Falls back to the canvas with the most CanvasRenderers if not found by name.");

            _minCR = Config.Bind("General", "MinCanvasRenderers", 2000,
                "Only split top-level screens with at least this many CanvasRenderers. " +
                "Lower = more screens moved (smaller shared canvas) but more changes; higher = less risk.");

            _exclude = Config.Bind("General", "ExcludeNames", DefaultExcludes,
                "Comma-separated screen names to NOT split. Defaults protect path-referenced " +
                "(RestockItemScreen_Grp) and mod-created (CustomShop_SharedScreen_Grp) screens.");

            _delayFrames = Config.Bind("General", "ApplyDelayFrames", 10,
                "Frames to wait after the scene is ready before splitting. Lets other mods finish " +
                "initializing (and cache references) before transforms move.");

            _baseSortOrder = Config.Bind("General", "BaseSortingOrder", 1000,
                "Base sortingOrder for new screen canvases. Original sibling order is added to it to " +
                "preserve layering. Keep it above the shared canvas (0) and below the tooltip canvas (2000).");

            ParseExcludes();
            StartCoroutine(WaitForScene());
            Logger.LogInfo($"[CanvasSplitter] v1.0.0 loaded (Enabled={_enabled.Value}, Mode={_mode.Value}, Giant='{_giantName.Value}', MinCR={_minCR.Value}, Delay={_delayFrames.Value})");
        }

        private void ParseExcludes()
        {
            _excluded.Clear();
            if (_exclude == null) return;
            foreach (var raw in _exclude.Value.Split(','))
            {
                var s = raw.Trim();
                if (s.Length > 0) _excluded.Add(s);
            }
        }

        private IEnumerator WaitForScene()
        {
            while (true)
            {
                _giant = FindGiantCanvas();
                if (_giant != null && _giant.transform.childCount >= 3)
                {
                    if (!_enabled.Value || _mode.Value == SplitMode.Off)
                    {
                        Logger.LogInfo($"[CanvasSplitter] disabled (Enabled={_enabled.Value}, Mode={_mode.Value}) - doing nothing.");
                        yield break;
                    }

                    int d = Mathf.Max(0, _delayFrames.Value);
                    for (int i = 0; i < d; i++) yield return null;

                    ParseExcludes();
                    LogCensus("BEFORE");
                    ApplySplit();
                    LogCensus("AFTER");
                    yield break;
                }
                yield return null;
            }
        }

        private Canvas FindGiantCanvas()
        {
            var go = GameObject.Find(_giantName.Value);
            if (go != null)
            {
                var c = go.GetComponent<Canvas>();
                if (c != null) return c;
            }
            // fallback: the canvas with the most CanvasRenderers
            Canvas[] all = FindObjectsOfType<Canvas>();
            Canvas best = null; int bestCR = -1;
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] == null) continue;
                int cr = all[i].GetComponentsInChildren<CanvasRenderer>(true).Length;
                if (cr > bestCR) { bestCR = cr; best = all[i]; }
            }
            return best;
        }

        private void ApplySplit()
        {
            if (_giant == null) return;
            var children = new List<Transform>();
            for (int i = 0; i < _giant.transform.childCount; i++)
                children.Add(_giant.transform.GetChild(i));

            int moved = 0, skippedSmall = 0, skippedExcl = 0;
            int sortOrder = _baseSortOrder.Value;

            foreach (var t in children)
            {
                if (t == null) continue;
                int cr = t.GetComponentsInChildren<CanvasRenderer>(true).Length;
                if (cr < _minCR.Value) { skippedSmall++; continue; }
                if (_excluded.Contains(t.name))
                {
                    Logger.LogInfo($"[CanvasSplitter]   skip (excluded): '{t.name}' CR={cr}");
                    skippedExcl++;
                    continue;
                }
                if (t.gameObject.activeInHierarchy)
                    Logger.LogWarning($"[CanvasSplitter]   NOTE: '{t.name}' is ACTIVE while being split (may cause a one-frame visual blip).");

                if (_mode.Value == SplitMode.Reparent) ReparentScreen(t, cr, sortOrder);
                else NestScreen(t, cr, sortOrder);
                sortOrder++;
                moved++;
            }

            Logger.LogInfo($"[CanvasSplitter] done: mode={_mode.Value} moved={moved} skippedSmall={skippedSmall} skippedExcluded={skippedExcl}");
        }

        private void ReparentScreen(Transform screen, int cr, int sortOrder)
        {
            try
            {
                string oldPath = GetPath(screen);
                var go = new GameObject("CanvasSplitter_" + screen.name);
                go.layer = screen.gameObject.layer;
                var c = go.AddComponent<Canvas>();
                c.renderMode = RenderMode.ScreenSpaceOverlay;
                c.sortingOrder = sortOrder;
                c.overrideSorting = true;
                c.pixelPerfect = false;

                var src = _giant.GetComponent<CanvasScaler>();
                if (src != null)
                {
                    var s = go.AddComponent<CanvasScaler>();
                    s.uiScaleMode = src.uiScaleMode;
                    s.referenceResolution = src.referenceResolution;
                    s.matchWidthOrHeight = src.matchWidthOrHeight;
                    s.dynamicPixelsPerUnit = src.dynamicPixelsPerUnit;
                }

                screen.SetParent(go.transform, worldPositionStays: false);
                int after = c.GetComponentsInChildren<CanvasRenderer>(true).Length;
                Logger.LogInfo($"[CanvasSplitter]   REPARENT '{screen.name}' CR {cr}->{after} sort={sortOrder}  {oldPath} -> {GetPath(screen)}");
            }
            catch (Exception e)
            {
                Logger.LogError($"[CanvasSplitter] reparent failed for '{screen.name}': {e}");
            }
        }

        private void NestScreen(Transform screen, int cr, int sortOrder)
        {
            try
            {
                var c = screen.gameObject.GetComponent<Canvas>();
                if (c == null) c = screen.gameObject.AddComponent<Canvas>();
                c.renderMode = RenderMode.ScreenSpaceOverlay;
                c.overrideSorting = true;
                c.sortingOrder = sortOrder;
                c.pixelPerfect = false;
                // Intentionally NO CanvasScaler here: the nested canvas inherits the parent's
                // scaling, so the screen should render exactly as before.
                Logger.LogInfo($"[CanvasSplitter]   NEST '{screen.name}' CR={cr} sort={sortOrder} (in place, path unchanged: {GetPath(screen)})");
            }
            catch (Exception e)
            {
                Logger.LogError($"[CanvasSplitter] nest failed for '{screen.name}': {e}");
            }
        }

        private void LogCensus(string phase)
        {
            try
            {
                Canvas[] all = FindObjectsOfType<Canvas>();
                var rows = new List<(string name, int cr, int active)>();
                int total = 0, totalActive = 0;
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i] == null) continue;
                    CanvasRenderer[] crs = all[i].GetComponentsInChildren<CanvasRenderer>(true);
                    int active = 0;
                    for (int j = 0; j < crs.Length; j++) if (crs[j] != null && crs[j].gameObject.activeInHierarchy) active++;
                    total += crs.Length; totalActive += active;
                    rows.Add((all[i].gameObject.name, crs.Length, active));
                }
                rows.Sort((a, b) => b.cr - a.cr);
                Logger.LogInfo($"[CanvasSplitter] CENSUS {phase}: {all.Length} canvases, {total} CanvasRenderers total, {totalActive} active");
                for (int i = 0; i < rows.Count && i < 12; i++)
                    Logger.LogInfo($"[CanvasSplitter]   {phase} {rows[i].name}: {rows[i].cr} CR ({rows[i].active} active)");
            }
            catch (Exception e)
            {
                Logger.LogError($"[CanvasSplitter] census failed ({phase}): {e}");
            }
        }

        private static string GetPath(Transform t)
        {
            string p = t.name;
            while (t.parent != null) { t = t.parent; p = t.name + "/" + p; }
            return p;
        }
    }
}
