using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using EnhancedPrefabLoader.API;
using UnityEngine;
using UnityEngine.UI;

namespace StutterBegone
{
    /// <summary>
    /// Splits the game's giant shared UI canvas into one small canvas per screen.
    ///
    /// The game pools EVERY full-screen UI under ONE shared ScreenSpaceOverlay canvas
    /// ('Canvas'). With a full mod set that canvas holds ~144,000-150,000 CanvasRenderers
    /// (only ~50-140 active at a time). Unity invalidates and rebuilds at the CANVAS
    /// granularity, so anything that dirties that canvas (a tooltip transition, opening a
    /// screen) pays the full ~144k cost. This plugin reparents every top-level screen onto
    /// its own tiny overlay canvas, so a change only rebuilds that screen's canvas.
    ///
    /// Every top-level child is moved to a new root ScreenSpaceOverlay canvas with
    /// sortingOrder = BaseSortingOrder + its original sibling index, which preserves the
    /// exact original layering (z-order). A GraphicRaycaster is added to each new canvas
    /// (and registered with the game's RaycasterManager) so the UI stays clickable.
    ///
    /// The tooltip (InputTooltipListDisplay) is moved too - it is the per-frame churn
    /// source, and on its own canvas a tooltip transition rebuilds ~20 elements instead of
    /// ~144k, which is the root fix for the hover stutter. No separate tooltip handling.
    ///
    /// Screens listed in ExcludeNames are left on the shared canvas. The default is empty
    /// (reparent everything); add a name there if a screen breaks (e.g. one a mod locates by
    /// path or a direct-child Transform.Find that runs at runtime).
    ///
    /// This plugin makes no Harmony patches. It runs once per scene load.
    /// </summary>
    // EPL is a soft dependency: if it's installed it loads first (so Epl.IsAvailable is true
    // when we run); if it's missing we still load and fall back to canvas-size heuristics.
    [BepInDependency("EnhancedPrefabLoader", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInPlugin("hecrj.stutter.begone", "StutterBegone", "1.1.0")]
    public class Plugin : BaseUnityPlugin
    {
        private ConfigEntry<bool> _enabled;
        private ConfigEntry<string> _giantName;
        private ConfigEntry<string> _exclude;
        private ConfigEntry<int> _delayFrames;
        private ConfigEntry<int> _baseSortOrder;
        private ConfigEntry<int> _probeInterval;
        private ConfigEntry<int> _settleFrames;
        private ConfigEntry<int> _maxWaitFrames;
        private ConfigEntry<int> _minTotalCR;
        private ConfigEntry<bool> _useEplSignal;

        private Canvas _giant;
        // Canvas watcher state: ourMaxSort = first sortingOrder slot above ALL our
        // reparented screens (= BaseSortingOrder + totalTopLevelChildren). New runtime
        // canvases are shifted to ourMaxSort + their own sortingOrder (preserves relative order).
        private int _ourMaxSort;
        private HashSet<GameObject> _knownCanvases;
        private bool _watcherStarted;
        private bool _bundleComplete;
        private bool _eplSubscribed;
        private HashSet<string> _excluded = new HashSet<string>(StringComparer.Ordinal);

        // Default: reparent EVERYTHING (empty exclude list). If a screen breaks (a mod
        // locates it by path, or a direct-child Transform.Find that runs at runtime), add
        // its name to ExcludeNames.
        private static readonly string DefaultExcludes = string.Empty;

        private void Awake()
        {
            _enabled = Config.Bind("General", "Enabled", true,
                "Master switch. False = original (single giant canvas) behavior.");

            _giantName = Config.Bind("General", "GiantCanvasName", "Canvas",
                "Name of the shared UI canvas to split. Falls back to the canvas with the most CanvasRenderers if not found by name.");

            _exclude = Config.Bind("General", "ExcludeNames", DefaultExcludes,
                "Comma-separated screen names to leave on the shared canvas (NOT reparented). " +
                "Default is empty (reparent everything). Add a name here if that screen breaks.");

            _delayFrames = Config.Bind("General", "ApplyDelayFrames", 10,
                "Frames to wait after the scene is ready before splitting. Lets other mods finish " +
                "initializing (and cache references) before transforms move.");

            _baseSortOrder = Config.Bind("General", "BaseSortingOrder", 1000,
                "Base sortingOrder for new screen canvases. Each moved screen gets " +
                "BaseSortingOrder + its original sibling index, which preserves the original " +
                "layering. Keep it above the shared canvas (0).");

            _probeInterval = Config.Bind("General", "ProbeInterval", 30,
                "Frames between canvas-size samples while waiting for the UI to finish building. " +
                "Higher = fewer (cheaper) samples, slower to detect stability.");

            _settleFrames = Config.Bind("General", "SettleFrames", 240,
                "How long (frames) the canvas element count must be unchanged before it is considered " +
                "fully built and safe to split.");

            _maxWaitFrames = Config.Bind("General", "MaxWaitFrames", 900,
                "Give up waiting for stability after this many frames and split anyway.");

            _minTotalCR = Config.Bind("General", "MinTotalCanvasRenderers", 5000,
                "The shared canvas must reach at least this many CanvasRenderers before it is considered " +
                "built (guards against splitting a canvas that never grows).");

            _useEplSignal = Config.Bind("General", "UseEplSignal", true,
                "Use EPL's OnBundleLoadingComplete event to know when content loading finishes " +
                "(recommended; handles the long loading screen). False = fall back to canvas-size heuristics.");

            ParseExcludes();
            StartCoroutine(WaitForScene());
            Logger.LogInfo($"[StutterBegone] v1.1.0 loaded (Enabled={_enabled.Value}, Giant='{_giantName.Value}', EplSignal={_useEplSignal.Value}, Exclude=[{_exclude.Value}])");
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
                // NOTE: the loading/base canvas is DESTROYED and replaced when the game
                // transitions to the shop scene (shortly after EPL's OnBundleLoadingComplete).
                // So we never trust a cached canvas reference - we re-find it every probe and
                // guard against it being gone. _giant is only a convenience cache.

                // Phase 1: wait for the game to be up (a canvas exists).
                while (FindGiantCanvas() == null) yield return null;

                if (!_enabled.Value)
                {
                    Logger.LogInfo($"[StutterBegone] disabled (Enabled={_enabled.Value}) - doing nothing.");
                    yield break;
                }

                for (int i = 0; i < Mathf.Max(0, _delayFrames.Value); i++) yield return null;

                // Phase 2a: wait for CONTENT LOADING to finish (the long loading screen).
                // EPL fires OnBundleLoadingComplete (hot/cold) when the bundles finish loading.
                bool loadingDone = false;
                int aFrame = 0, aHardCap = Mathf.Max(60, _maxWaitFrames.Value * 6);
                while (!loadingDone && aFrame < aHardCap)
                {
                    yield return null;
                    aFrame++;
                    if (_useEplSignal.Value && !_eplSubscribed && aFrame % 10 == 0)
                    {
                        if (TrySubscribeBundleComplete())
                        {
                            _eplSubscribed = true;
                            Logger.LogInfo("[StutterBegone] subscribed to EPL OnBundleLoadingComplete (hot/cold).");
                        }
                    }
                    if (_bundleComplete)
                    {
                        loadingDone = true;
                        Logger.LogInfo($"[StutterBegone] EPL OnBundleLoadingComplete fired (frame {aFrame}) - content loaded.");
                    }
                    else if (!_useEplSignal.Value && aFrame % 30 == 0)
                    {
                        var g = FindGiantCanvas();
                        if (g != null && CountCR(g) >= _minTotalCR.Value)
                        {
                            loadingDone = true;
                            Logger.LogInfo("[StutterBegone] no EPL signal; canvas reached threshold - assuming content loaded.");
                        }
                    }
                }
                if (!loadingDone)
                    Logger.LogWarning($"[StutterBegone] loading-done wait hit hard cap ({aHardCap}f); proceeding.");

                // Phase 2b: wait until the REAL canvas is FULLY built. After the event the game
                // switches to the shop scene (old canvas destroyed, new one created), and the
                // shop panels are added, growing it from ~2k to ~144k. Re-find every probe.
                int lastCR = -1, lastCC = -1, lastChangeFrame = 0, frame = 0;
                int maxWait = Mathf.Max(1, _maxWaitFrames.Value);
                int probe = Mathf.Max(1, _probeInterval.Value);
                while (frame < maxWait)
                {
                    yield return null;
                    frame++;
                    _giant = FindGiantCanvas();
                    if (_giant == null) continue;   // canvas gone during scene transition; wait
                    int cc = _giant.transform.childCount;                    // cheap (O(1))
                    int cr = (frame % probe == 0) ? CountCR(_giant) : lastCR; // expensive, throttled
                    if (frame % probe == 0 && (cr != lastCR || cc != lastCC))
                    {
                        Logger.LogInfo($"[StutterBegone]   building: children {lastCC}->{cc}, CanvasRenderers {lastCR}->{cr}");
                        lastCR = cr; lastCC = cc; lastChangeFrame = frame;
                    }
                    else if (cc != lastCC)
                    {
                        lastCC = cc; lastChangeFrame = frame;
                    }
                    if (lastCR >= _minTotalCR.Value && (frame - lastChangeFrame) >= _settleFrames.Value)
                        break;
                }
                if (frame >= maxWait)
                    Logger.LogWarning($"[StutterBegone] settle wait hit max ({maxWait}f, CR={lastCR}); splitting anyway.");

                _giant = FindGiantCanvas();
                if (_giant == null)
                {
                    Logger.LogError("[StutterBegone] canvas not found at split time - aborting.");
                    yield break;
                }

                try
                {
                    ParseExcludes();
                    LogCensus("BEFORE");
                    ApplySplit();
                    LogCensus("AFTER");
                }
                catch (Exception e)
                {
                    Logger.LogError("[StutterBegone] split failed: " + e);
                }
                yield break;
        }

        /// <summary>
        /// Subscribes to EPL's OnBundleLoadingComplete (linked directly, per the EPL guide).
        /// The event is hot/cold - it fires immediately if loading already finished. Returns
        /// true once subscribed. If EPL (or its API assembly) is not installed, returns false
        /// and the plugin falls back to canvas-size heuristics.
        /// </summary>
        private bool TrySubscribeBundleComplete()
        {
            try
            {
                if (!Epl.IsAvailable) return false;
                Epl.Api.Events.OnBundleLoadingComplete += () => _bundleComplete = true;
                return true;
            }
            catch (Exception e)
            {
                // EPL API assembly not resolvable (EPL not installed) or other error.
                Logger.LogWarning("[StutterBegone] EPL API unavailable: " + e.Message);
                return false;
            }
        }

        private static int CountCR(Canvas c)
        {
            return c.GetComponentsInChildren<CanvasRenderer>(true).Length;
        }

        /// <summary>
        /// Adds a GraphicRaycaster to a canvas root (so its UI is clickable) and registers it
        /// with the game's RaycasterManager so the game's SetUIRaycastEnabled toggles it
        /// together with the rest. Settings are copied from the shared canvas's raycaster.
        /// </summary>
        private void AddAndRegisterRaycaster(GameObject canvasRoot)
        {
            try
            {
                var rc = canvasRoot.GetComponent<GraphicRaycaster>();
                if (rc == null)
                {
                    rc = canvasRoot.AddComponent<GraphicRaycaster>();
                    var src = _giant != null ? _giant.GetComponent<GraphicRaycaster>() : null;
                    if (src != null)
                    {
                        rc.blockingObjects = src.blockingObjects;
                        rc.ignoreReversedGraphics = src.ignoreReversedGraphics;
                    }
                }
                RegisterWithRaycasterManager(rc);
            }
            catch (Exception e)
            {
                Logger.LogWarning("[StutterBegone] failed to add raycaster to " + canvasRoot.name + ": " + e.Message);
            }
        }

        /// <summary>
        /// Best-effort: add a GraphicRaycaster to RaycasterManager.Instance.m_RaycasterList
        /// (via reflection, no hard game-assembly dependency) so the game manages it.
        /// </summary>
        private void RegisterWithRaycasterManager(GraphicRaycaster rc)
        {
            try
            {
                Type rmType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try { rmType = asm.GetType("RaycasterManager"); } catch { }
                    if (rmType != null) break;
                }
                if (rmType == null) return;
                var instProp = rmType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                if (instProp == null) return;
                object inst = instProp.GetValue(null);
                if (inst == null) return;
                var listField = rmType.GetField("m_RaycasterList", BindingFlags.Public | BindingFlags.Instance);
                if (listField == null) return;
                var list = listField.GetValue(inst) as System.Collections.IList;
                if (list == null) return;
                if (!list.Contains(rc)) list.Add(rc);
            }
            catch (Exception e)
            {
                Logger.LogWarning("[StutterBegone] could not register raycaster with RaycasterManager: " + e.Message);
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
            int n = _giant.transform.childCount;
            // Snapshot the children BEFORE reparenting: moving a child out of the shared canvas
            // changes childCount/GetChild, so we iterate a fixed copy.
            var children = new Transform[n];
            for (int i = 0; i < n; i++)
                children[i] = _giant.transform.GetChild(i);
            int baseSort = _baseSortOrder.Value;

            int moved = 0, skippedExcl = 0;

            // Reparent EVERY top-level child (except ExcludeNames) onto its own canvas.
            // sortingOrder = base + original sibling index preserves the exact original
            // layering (z-order): an overlay in front of a screen (higher sibling index) stays
            // in front of it after both are moved.
            for (int j = 0; j < n; j++)
            {
                var t = children[j];
                if (t == null) continue;
                if (_excluded.Contains(t.name))
                {
                    Logger.LogInfo($"[StutterBegone]   skip (excluded): '{t.name}'");
                    skippedExcl++;
                    continue;
                }
                int cr = t.GetComponentsInChildren<CanvasRenderer>(true).Length;
                int sortOrder = baseSort + j;

                if (t.gameObject.activeInHierarchy)
                    Logger.LogWarning($"[StutterBegone]   NOTE: '{t.name}' is ACTIVE while being split (may cause a one-frame visual blip).");

                ReparentScreen(t, cr, sortOrder);
                moved++;
            }

            Logger.LogInfo($"[StutterBegone] done: moved={moved} skippedExcluded={skippedExcl}");

            // Watch for NEW root canvases created at runtime (e.g. a mod's modal) and shift
            // them above all our reparented screens, preserving their relative order:
            //   newSort = BaseSortingOrder + totalTopLevelChildren + canvas.sortingOrder
            _ourMaxSort = _baseSortOrder.Value + n;
            _knownCanvases = new HashSet<GameObject>();
            foreach (var c in FindObjectsOfType<Canvas>())
                if (c != null) _knownCanvases.Add(c.gameObject);
            if (!_watcherStarted)
            {
                _watcherStarted = true;
                StartCoroutine(WatchNewCanvases());
            }
            Logger.LogInfo($"[StutterBegone] canvas watcher armed (ourMaxSort={_ourMaxSort}, known={_knownCanvases.Count} canvases at split)");
        }

        private IEnumerator WatchNewCanvases()
        {
            int counter = 0;
            while (true)
            {
                yield return null;
                if (++counter % 5 != 0) continue; // check every 5 frames
                Canvas[] all = FindObjectsOfType<Canvas>();
                for (int i = 0; i < all.Length; i++)
                {
                    var c = all[i];
                    if (c == null) continue;
                    if (_knownCanvases.Contains(c.gameObject)) continue;
                    if (!IsRootCanvas(c)) continue; // nested canvases stay unmarked (re-checked each poll)
                    _knownCanvases.Add(c.gameObject);
                    int old = c.sortingOrder;
                    int newSort = _ourMaxSort + old;
                    c.sortingOrder = newSort;
                    Logger.LogInfo($"[StutterBegone]   shifted new runtime canvas '{c.gameObject.name}' sort {old} -> {newSort}");
                }
            }
        }

        private static bool IsRootCanvas(Canvas c)
        {
            Transform t = c.transform.parent;
            while (t != null)
            {
                if (t.GetComponent<Canvas>() != null) return false; // has an ancestor canvas -> nested
                t = t.parent;
            }
            return true;
        }

        private void ReparentScreen(Transform screen, int cr, int sortOrder)
        {
            try
            {
                string oldPath = GetPath(screen);
                var go = new GameObject("StutterBegone_" + screen.name);
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

                // CRITICAL: a ScreenSpaceOverlay canvas needs a GraphicRaycaster for its UI to
                // be clickable/hoverable. Without one the screen renders but the EventSystem
                // can't raycast it (the reported "not interactable" bug).
                AddAndRegisterRaycaster(go);

                screen.SetParent(go.transform, worldPositionStays: false);
                int after = c.GetComponentsInChildren<CanvasRenderer>(true).Length;
                Logger.LogInfo($"[StutterBegone]   REPARENT '{screen.name}' CR {cr}->{after} sort={sortOrder}  {oldPath} -> {GetPath(screen)}");
            }
            catch (Exception e)
            {
                Logger.LogError($"[StutterBegone] reparent failed for '{screen.name}': {e}");
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
                Logger.LogInfo($"[StutterBegone] CENSUS {phase}: {all.Length} canvases, {total} CanvasRenderers total, {totalActive} active");
                for (int i = 0; i < rows.Count && i < 12; i++)
                    Logger.LogInfo($"[StutterBegone]   {phase} {rows[i].name}: {rows[i].cr} CR ({rows[i].active} active)");
            }
            catch (Exception e)
            {
                Logger.LogError($"[StutterBegone] census failed ({phase}): {e}");
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
