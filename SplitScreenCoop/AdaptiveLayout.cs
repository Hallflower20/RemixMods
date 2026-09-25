using System;
using System.Collections.Generic;
using UnityEngine;

namespace SplitScreenCoop
{
    /// <summary>
    /// The Adaptive split style (2026-09-22). Game-independent, so it is tested like
    /// the old solver. Coordinates are normalized, bottom-left to top-right.
    ///
    /// Rain World is a flip-screen game: every camera shows one prebaked screen that
    /// stays still while the player moves and cuts when the player leaves it. The
    /// Dynamic style treated it as a scrolling game (cells chasing their players,
    /// merging by distance, a layout tree restructuring whenever any pair changed),
    /// which players found disorienting, worst with three and four. This style
    /// merges by screen instead and keeps the picture still:
    ///  - Players on one prebaked screen already see the same picture. When all of
    ///    them are on one screen there is one full view; otherwise the screen splits.
    ///  - The split depends only on how many players are alive: two side-by-side halves
    ///    (full height, native scale) or a 2x2 grid of quarters. A quarter has the
    ///    screen's own aspect, so it shows its player's whole screen at half size and
    ///    never moves. With three players the fourth quarter is the spare (map and
    ///    shared meters). Partial groups change nothing: two quarters simply show the
    ///    same picture. Deaths and revivals reflow the slots by rank.
    ///  - A half cannot show the whole screen, so it shows a window of it that sits at
    ///    one of three steps (left, centre, right) and only moves, briefly, when its
    ///    own player nears an edge (<see cref="StepFraming"/>).
    ///  - The only other motion is the change between one view and the split, as a
    ///    zoom: a quarter grows to fill the screen or shrinks back into its corner, a
    ///    half widens or narrows. It never happens because a player crossed a screen
    ///    boundary for a moment: merging waits until the group has been together for
    ///    <see cref="Settings.mergeDelay"/>, splitting waits
    ///    <see cref="Settings.splitGrace"/> in case the others follow, and a split
    ///    blocks the next merge for <see cref="Settings.remergeCooldown"/>.
    ///
    /// A view maps a texture window onto its cell as texcoord = window + (p - cell.min)
    /// / zoom, with one zoom for both axes, so the picture is never squashed, including
    /// halfway through a transition.
    /// </summary>
    public sealed class AdaptiveLayout
    {
        public sealed class Settings
        {
            /// <summary>Everyone on one screen for this long before the views join.</summary>
            public float mergeDelay = 1f;
            /// <summary>Someone off the shared screen for this long before the view splits; a group crossing a boundary together never splits.</summary>
            public float splitGrace = 0.5f;
            /// <summary>After a split, no merge for this long, so a player hovering on a boundary cannot pump the layout.</summary>
            public float remergeCooldown = 2f;
            /// <summary>Length of the zoom between one view and the split.</summary>
            public float zoomSeconds = 0.35f;
            /// <summary>Length of the slide when a death or a revival reflows the slots.</summary>
            public float reflowSeconds = 0.5f;
            /// <summary>Length of a half view's framing step.</summary>
            public float stepSeconds = 0.35f;
            /// <summary>A half view steps when its player comes this close to an edge, as a fraction of the view's width.</summary>
            public float edgeMargin = 0.22f;
        }

        public struct Player
        {
            public int camera;
            /// <summary>Equal for players whose cameras show the same prebaked screen (or are heading to the same room).</summary>
            public long screenKey;
        }

        public struct Box
        {
            public float x, y, w, h;
            public Box(float x, float y, float w, float h) { this.x = x; this.y = y; this.w = w; this.h = h; }
            public float xMax => x + w;
            public float yMax => y + h;
            public Vector2 Min => new Vector2(x, y);
            public Vector2 Center => new Vector2(x + w * 0.5f, y + h * 0.5f);
            public static readonly Box Full = new Box(0f, 0f, 1f, 1f);
            public static Box Lerp(Box a, Box b, float t)
            {
                return new Box(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t, a.w + (b.w - a.w) * t, a.h + (b.h - a.h) * t);
            }
            public bool Approximately(Box other, float epsilon = 0.0005f)
            {
                return Mathf.Abs(x - other.x) < epsilon && Mathf.Abs(y - other.y) < epsilon &&
                    Mathf.Abs(w - other.w) < epsilon && Mathf.Abs(h - other.h) < epsilon;
            }
            public Vector2[] Polygon()
            {
                return new[] { new Vector2(x, y), new Vector2(xMax, y), new Vector2(xMax, yMax), new Vector2(x, yMax) };
            }
            public override string ToString() => $"({x:0.###},{y:0.###} {w:0.###}x{h:0.###})";
        }

        public enum Shape { Single, Halves, Grid }

        public sealed class View
        {
            /// <summary>The player's camera number; -1 for the spare quarter.</summary>
            public int camera;
            /// <summary>Rank among the living players, which picks the slot.</summary>
            public int rank;
            public Box cell;
            public float zoom = 1f;
            /// <summary>Texture uv shown at the cell's bottom-left corner.</summary>
            public Vector2 window;
            public bool visible = true;
            /// <summary>Being covered by a growing view; hidden once that zoom ends.</summary>
            public bool leaving;
            /// <summary>Half view: native scale, window follows <see cref="StepFraming"/>.</summary>
            public bool native;
            /// <summary>Larger draws later.</summary>
            public int order = 1;
            /// <summary>Latest framing target for a half view (window x).</summary>
            public float framing = -1f;

            internal Box cellFrom, cellTo;
            internal float zoomFrom = 1f, zoomTo = 1f;
            internal float geoT = 1f, geoDuration = 1f;
            internal Vector2 windowFrom, windowTo;
            internal float winT = 1f, winDuration = 1f;

            public bool Moving => geoT < 1f || winT < 1f;
            public Box TargetCell => cellTo;
            public float TargetZoom => zoomTo;
            public Vector2 TargetWindow => windowTo;
        }

        public Shape shape { get; private set; } = Shape.Single;
        /// <summary>One view for everyone (all on one screen, or a single survivor).</summary>
        public bool full { get; private set; }
        /// <summary>The camera whose view fills the screen while <see cref="full"/>.</summary>
        public int fullCamera { get; private set; } = -1;
        public readonly List<View> views = new List<View>(5);
        /// <summary>The fourth quarter when three players are alive and split; null otherwise.</summary>
        public View spare { get; private set; }
        /// <summary>What the last <see cref="Update"/> did, for the log: "", "merge", "split", "reflow", "switch".</summary>
        public string lastEvent { get; private set; } = "";

        private readonly List<int> alive = new List<int>(4);
        private readonly Dictionary<int, long> keys = new Dictionary<int, long>();
        private readonly List<int> scratch = new List<int>(4);
        private float togetherFor, apartFor, sinceSplit = 1000f;
        private View mergeAnchor;
        private bool initialized;

        public bool FullAtRest
        {
            get
            {
                if (!full) return false;
                foreach (View view in views) if (view.visible && view.Moving) return false;
                return true;
            }
        }

        public View ViewOf(int camera)
        {
            foreach (View view in views) if (view.camera == camera && view != spare) return view;
            return null;
        }

        public void Reset()
        {
            views.Clear();
            alive.Clear();
            keys.Clear();
            spare = null;
            mergeAnchor = null;
            full = false;
            fullCamera = -1;
            shape = Shape.Single;
            togetherFor = 0f;
            apartFor = 0f;
            sinceSplit = 1000f;
            initialized = false;
            lastEvent = "";
        }

        public static Shape ShapeFor(int count)
        {
            return count <= 1 ? Shape.Single : count == 2 ? Shape.Halves : Shape.Grid;
        }

        /// <summary>The resting cell of rank <paramref name="rank"/>: halves left to right, quarters top-left, top-right, bottom-left, bottom-right.</summary>
        public static Box Slot(Shape shape, int rank)
        {
            if (shape == Shape.Halves) return rank <= 0 ? new Box(0f, 0f, 0.5f, 1f) : new Box(0.5f, 0f, 0.5f, 1f);
            if (shape == Shape.Grid)
                switch (rank)
                {
                    case 0: return new Box(0f, 0.5f, 0.5f, 0.5f);
                    case 1: return new Box(0.5f, 0.5f, 0.5f, 0.5f);
                    case 2: return new Box(0f, 0f, 0.5f, 0.5f);
                    default: return new Box(0.5f, 0f, 0.5f, 0.5f);
                }
            return Box.Full;
        }

        /// <summary>The window x at which a half view shows exactly what the full view shows in that half.</summary>
        public static float InPlaceFraming(int rank) => rank <= 0 ? 0f : 0.5f;

        /// <summary>
        /// A half view's window, 0.5 wide, sits at one of three steps: 0, 0.25 or 0.5.
        /// It stays put while its player is more than <paramref name="edgeMargin"/> of a
        /// width inside the window; nearer an edge it moves to the step that centres the
        /// player best. After a step the player is at most an eighth of the screen from
        /// the window's centre, well inside the margin, so it cannot step straight back.
        /// </summary>
        public static float StepFraming(float current, float playerX, float width, float edgeMargin)
        {
            float maxStart = 1f - width;
            if (maxStart <= 0.0001f) return 0f;
            if (float.IsNaN(playerX) || float.IsInfinity(playerX)) return current;
            current = Mathf.Clamp(current, 0f, maxStart);
            float edge = edgeMargin * width;
            if (playerX >= current + edge && playerX <= current + width - edge) return current;
            float best = current, bestDistance = Mathf.Abs(current + width * 0.5f - playerX);
            for (int i = 0; i <= 2; i++)
            {
                float start = maxStart * i / 2f;
                float distance = Mathf.Abs(start + width * 0.5f - playerX);
                if (distance < bestDistance - 0.00001f) { best = start; bestDistance = distance; }
            }
            return best;
        }

        /// <summary>The step that centres a player best, for a view that has no framing yet or whose picture just cut.</summary>
        public static float BestFraming(float playerX, float width)
        {
            float maxStart = 1f - width;
            if (maxStart <= 0.0001f) return 0f;
            if (float.IsNaN(playerX) || float.IsInfinity(playerX)) return maxStart / 2f;
            float best = 0f, bestDistance = float.MaxValue;
            for (int i = 0; i <= 2; i++)
            {
                float start = maxStart * i / 2f;
                float distance = Mathf.Abs(start + width * 0.5f - playerX);
                if (distance < bestDistance - 0.00001f) { best = start; bestDistance = distance; }
            }
            return best;
        }

        private static float Ease(float t) => t * t * (3f - 2f * t);

        /// <summary>Once per game tick: decide merges, splits and reflows.</summary>
        public void Update(IList<Player> players, float dt, Settings settings)
        {
            lastEvent = "";
            scratch.Clear();
            keys.Clear();
            for (int i = 0; i < players.Count; i++)
            {
                if (scratch.Contains(players[i].camera)) continue;
                scratch.Add(players[i].camera);
                keys[players[i].camera] = players[i].screenKey;
            }
            scratch.Sort();
            if (scratch.Count == 0) return;
            bool together = Together();
            togetherFor = together ? togetherFor + dt : 0f;
            apartFor = together ? 0f : apartFor + dt;
            sinceSplit += dt;

            if (!initialized)
            {
                initialized = true;
                alive.AddRange(scratch);
                Build(together || alive.Count == 1);
                lastEvent = "start";
                return;
            }
            if (!SameAlive())
            {
                Reflow(settings, together);
                lastEvent = "reflow";
                return;
            }
            if (alive.Count == 1) return;
            if (full)
            {
                if (KeepFullOnMajority()) lastEvent = "switch";
                if (!together && apartFor >= settings.splitGrace)
                {
                    StartSplit(settings);
                    lastEvent = "split";
                }
            }
            else if (together && togetherFor >= settings.mergeDelay && sinceSplit >= settings.remergeCooldown)
            {
                StartMerge(settings);
                lastEvent = "merge";
            }
        }

        /// <summary>Once per rendered frame: advance every transition.</summary>
        public void Animate(float dt)
        {
            foreach (View view in views)
            {
                if (view.geoT < 1f)
                {
                    view.geoT = Mathf.Min(1f, view.geoT + dt / Mathf.Max(0.0001f, view.geoDuration));
                    float e = Ease(view.geoT);
                    view.cell = Box.Lerp(view.cellFrom, view.cellTo, e);
                    view.zoom = view.zoomFrom + (view.zoomTo - view.zoomFrom) * e;
                }
                if (view.winT < 1f)
                {
                    view.winT = Mathf.Min(1f, view.winT + dt / Mathf.Max(0.0001f, view.winDuration));
                    float e = Ease(view.winT);
                    view.window = view.windowFrom + (view.windowTo - view.windowFrom) * e;
                }
            }
            if (mergeAnchor != null && mergeAnchor.geoT >= 1f)
            {
                foreach (View view in views)
                    if (view.leaving)
                    {
                        view.leaving = false;
                        view.visible = false;
                    }
                mergeAnchor.order = 1;
                mergeAnchor = null;
            }
        }

        /// <summary>
        /// Once per rendered frame for each half view: where its window should be. A
        /// snap (the picture under it cut, or the view is hidden) applies at once; any
        /// other change is a short eased step, never during a zoom or a slide.
        /// </summary>
        public void SetFraming(int camera, float windowX, bool snap, Settings settings)
        {
            View view = ViewOf(camera);
            if (view == null) return;
            // Kept for every view, so a reflow into halves starts from the player's
            // real position rather than a stale one.
            view.framing = windowX;
            if (!view.native) return;
            if (view.leaving || view.camera == fullCamera && full) return;
            if (snap || !view.visible)
            {
                view.window = view.windowFrom = view.windowTo = new Vector2(windowX, 0f);
                view.winT = 1f;
                return;
            }
            if (view.geoT < 1f || Mathf.Abs(view.windowTo.x - windowX) < 0.0001f) return;
            view.windowFrom = view.window;
            view.windowTo = new Vector2(windowX, 0f);
            view.winT = 0f;
            view.winDuration = settings.stepSeconds;
        }

        private bool Together()
        {
            if (scratch.Count <= 1) return true;
            long first = keys[scratch[0]];
            for (int i = 1; i < scratch.Count; i++) if (keys[scratch[i]] != first) return false;
            return true;
        }

        private bool SameAlive()
        {
            if (scratch.Count != alive.Count) return false;
            for (int i = 0; i < scratch.Count; i++) if (scratch[i] != alive[i]) return false;
            return true;
        }

        /// <summary>The screen most players are on; ties keep the full view's screen, then the lowest camera's.</summary>
        private long MajorityKey()
        {
            long best = keys[alive[0]];
            int bestCount = -1;
            bool fullKnown = fullCamera >= 0 && keys.ContainsKey(fullCamera);
            foreach (int camera in alive)
            {
                long key = keys[camera];
                int count = 0;
                foreach (int other in alive) if (keys[other] == key) count++;
                bool preferred = fullKnown && key == keys[fullCamera];
                if (count > bestCount || (count == bestCount && preferred))
                {
                    best = key;
                    bestCount = count;
                }
            }
            return best;
        }

        /// <summary>
        /// While one view is shown and a player has just left the shared screen, keep
        /// showing the screen most players are on. If the player who left owned the
        /// full view, hand the full view to a camera still on that screen: the same
        /// picture, so nothing visible changes. Returns true if it switched.
        /// </summary>
        private bool KeepFullOnMajority()
        {
            long majority = MajorityKey();
            if (fullCamera >= 0 && keys.ContainsKey(fullCamera) && keys[fullCamera] == majority) return false;
            foreach (int camera in alive)
                if (keys[camera] == majority)
                {
                    SetFullCamera(camera);
                    return true;
                }
            return false;
        }

        private void SetFullCamera(int camera)
        {
            View previous = ViewOf(fullCamera);
            View next = ViewOf(camera);
            fullCamera = camera;
            if (next == null) return;
            if (previous != null && previous != next)
            {
                // The new owner takes over exactly where the old one was, mid-zoom
                // included; only then does the old one go back to its slot, unseen.
                next.cell = previous.cell; next.zoom = previous.zoom; next.window = previous.window;
                next.cellFrom = previous.cellFrom; next.cellTo = previous.cellTo;
                next.zoomFrom = previous.zoomFrom; next.zoomTo = previous.zoomTo;
                next.geoT = previous.geoT; next.geoDuration = previous.geoDuration;
                next.windowFrom = previous.windowFrom; next.windowTo = previous.windowTo;
                next.winT = previous.winT; next.winDuration = previous.winDuration;
                next.native = previous.native;
                if (mergeAnchor == previous) mergeAnchor = next;
                SetRest(previous, true);
                previous.visible = false;
                previous.leaving = false;
                previous.order = 1;
            }
            else if (previous == null) SetFull(next, true, 0f);
            next.visible = true;
            next.leaving = false;
            next.order = 2;
        }

        private void Build(bool startFull)
        {
            views.Clear();
            spare = null;
            mergeAnchor = null;
            shape = ShapeFor(alive.Count);
            for (int rank = 0; rank < alive.Count; rank++)
            {
                var view = new View { camera = alive[rank], rank = rank };
                views.Add(view);
                SetRest(view, true);
            }
            full = startFull;
            if (full)
            {
                fullCamera = alive[0];
                foreach (View view in views) view.visible = false;
                View owner = ViewOf(fullCamera);
                SetFull(owner, true, 0f);
                owner.visible = true;
                owner.order = 2;
            }
            else
            {
                fullCamera = -1;
                EnsureSpare(true);
            }
        }

        /// <summary>The resting mapping of a view for the current shape.</summary>
        private void SetRest(View view, bool immediate, float seconds = 0f)
        {
            Box cell = Slot(shape, view.rank);
            float zoom = 1f;
            Vector2 window = Vector2.zero;
            view.native = shape == Shape.Halves;
            if (shape == Shape.Grid) zoom = 0.5f;
            else if (shape == Shape.Halves)
                window = new Vector2(view.framing >= 0f ? view.framing : InPlaceFraming(view.rank), 0f);
            MoveTo(view, cell, zoom, window, immediate, seconds);
        }

        private void SetFull(View view, bool immediate, float seconds)
        {
            view.native = false;
            MoveTo(view, Box.Full, 1f, Vector2.zero, immediate, seconds);
        }

        private static void MoveTo(View view, Box cell, float zoom, Vector2 window, bool immediate, float seconds)
        {
            if (immediate || seconds <= 0f)
            {
                view.cell = view.cellFrom = view.cellTo = cell;
                view.zoom = view.zoomFrom = view.zoomTo = zoom;
                view.window = view.windowFrom = view.windowTo = window;
                view.geoT = view.winT = 1f;
                return;
            }
            view.cellFrom = view.cell; view.cellTo = cell;
            view.zoomFrom = view.zoom; view.zoomTo = zoom;
            view.geoT = 0f; view.geoDuration = seconds;
            view.windowFrom = view.window; view.windowTo = window;
            view.winT = 0f; view.winDuration = seconds;
        }

        /// <summary>The fourth quarter exists exactly while three players are split into the grid.</summary>
        private void EnsureSpare(bool visible)
        {
            bool wanted = !full && shape == Shape.Grid && alive.Count == 3;
            if (!wanted)
            {
                if (spare != null) { views.Remove(spare); spare = null; }
                return;
            }
            if (spare == null)
            {
                spare = new View { camera = -1, rank = 3, order = 0 };
                views.Insert(0, spare);
            }
            MoveTo(spare, Slot(Shape.Grid, 3), 0.5f, Vector2.zero, true, 0f);
            spare.visible = visible;
            spare.leaving = false;
        }

        private void StartSplit(Settings settings)
        {
            sinceSplit = 0f;
            full = false;
            View anchor = ViewOf(fullCamera);
            foreach (View view in views)
            {
                if (view == spare || view == anchor) continue;
                view.leaving = false;
                view.visible = true;
                view.order = 1;
                SetRest(view, true);
            }
            if (anchor != null)
            {
                anchor.visible = true;
                anchor.leaving = false;
                anchor.order = 2;
                SetRest(anchor, false, settings.zoomSeconds);
            }
            mergeAnchor = null;
            fullCamera = -1;
            EnsureSpare(true);
        }

        /// <summary>
        /// A half whose window is already in place grows to full with no motion inside
        /// it; otherwise the one needing the smaller shift. Quarters: the lowest camera.
        /// </summary>
        private View ChooseAnchor()
        {
            View best = null;
            float bestShift = float.MaxValue;
            foreach (View view in views)
            {
                if (view == spare || !view.visible) continue;
                float shift = shape == Shape.Halves ? Mathf.Abs(view.window.x - InPlaceFraming(view.rank)) : view.rank;
                if (shift < bestShift - 0.0001f) { best = view; bestShift = shift; }
            }
            return best ?? ViewOf(alive[0]);
        }

        private void StartMerge(Settings settings)
        {
            View anchor = ChooseAnchor();
            full = true;
            fullCamera = anchor.camera;
            foreach (View view in views)
            {
                if (view == anchor || !view.visible) continue;
                view.leaving = true;
                view.order = view == spare ? 0 : 1;
            }
            anchor.visible = true;
            anchor.leaving = false;
            anchor.order = 2;
            SetFull(anchor, false, settings.zoomSeconds);
            mergeAnchor = anchor;
        }

        private void Reflow(Settings settings, bool together)
        {
            var survivors = new List<View>(views.Count);
            foreach (View view in views)
                if (view != spare && scratch.Contains(view.camera)) survivors.Add(view);
            alive.Clear();
            alive.AddRange(scratch);
            shape = ShapeFor(alive.Count);
            views.Clear();
            if (spare != null)
            {
                if (!full) views.Add(spare);
                else spare = null;
            }
            views.AddRange(survivors);
            for (int rank = 0; rank < alive.Count; rank++)
            {
                View view = ViewOf(alive[rank]);
                if (view == null)
                {
                    // A revived player's view grows out of its slot's centre.
                    view = new View { camera = alive[rank], rank = rank };
                    Box slot = Slot(shape, rank);
                    Vector2 c = slot.Center;
                    float zoom = shape == Shape.Grid ? 0.5f : 1f;
                    MoveTo(view, new Box(c.x, c.y, 0f, 0f), 0.0001f, Vector2.zero, true, 0f);
                    view.zoom = view.zoomFrom = view.zoomTo = zoom * 0.0001f;
                    views.Add(view);
                }
                view.rank = rank;
            }
            if (mergeAnchor != null && !views.Contains(mergeAnchor)) mergeAnchor = null;
            if (alive.Count == 1)
            {
                // The survivor fills the screen.
                View survivor = ViewOf(alive[0]);
                bool wasHidden = !survivor.visible;
                foreach (View view in views) if (view != survivor) { view.visible = false; view.leaving = false; }
                if (spare != null) { views.Remove(spare); spare = null; }
                full = true;
                fullCamera = survivor.camera;
                survivor.visible = true;
                survivor.leaving = false;
                survivor.order = 2;
                SetFull(survivor, wasHidden, settings.reflowSeconds);
                mergeAnchor = null;
                return;
            }
            if (full)
            {
                // Still one view: the owner stays full (handed on if the owner died),
                // everyone else moves to their new slot unseen.
                if (ViewOf(fullCamera) == null)
                {
                    fullCamera = -1;
                    long majority = MajorityKey();
                    foreach (int camera in alive) if (keys[camera] == majority) { fullCamera = camera; break; }
                    View owner = ViewOf(fullCamera);
                    SetFull(owner, true, 0f);
                    owner.visible = true;
                    owner.order = 2;
                }
                foreach (View view in views)
                    if (view.camera != fullCamera)
                    {
                        view.leaving = false;
                        view.visible = false;
                        SetRest(view, true);
                    }
                if (mergeAnchor != null && mergeAnchor.camera != fullCamera) mergeAnchor = null;
                return;
            }
            foreach (View view in views)
            {
                if (view == spare) continue;
                view.leaving = false;
                view.visible = true;
                view.order = 1;
                SetRest(view, false, settings.reflowSeconds);
            }
            EnsureSpare(true);
        }
    }
}
