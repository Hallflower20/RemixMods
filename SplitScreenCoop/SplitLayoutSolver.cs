using System;
using System.Collections.Generic;
using UnityEngine;

namespace SplitScreenCoop
{
    /// <summary>
    /// Game-independent screen geometry of the Static split style: one fixed region per
    /// living player. Coordinates in the output are normalized bottom-left to top-right.
    ///
    /// At rest every region is an axis-aligned rectangle. That is not a stylistic
    /// choice: each view samples one prebaked room screen, so a region can only pan by
    /// however much of the source its bounding box leaves uncovered. A rectangle of
    /// width w keeps 1-w of horizontal pan and always has a legal pan that shows its
    /// own player.
    ///
    /// Where the regions sit depends only on which players are alive, never on where
    /// they stand: the lower camera number is left (or top), three players are a half
    /// over two quarters, four a grid. A death shrinks the dying player's region shut
    /// and hands the screen to the survivors; an arrival or a revival hands it back;
    /// either change slides the regions from their old rectangles to their new ones.
    /// Players walking around inside their regions only pan the image.
    ///
    /// Until 2026-09-22 this solver also merged players on one screen into one view,
    /// by distance, for the Dynamic style. That style was removed at the user's request
    /// and the merging with it; HANDOFF "Merging stripped from the solver" has what went.
    /// </summary>
    public sealed class SplitLayoutSolver
    {
        public sealed class Settings
        {
            public float minZoom = 0.5f;
            public float zoomExponent = 0f;
            public float smoothingTime = 0.18f;
            public float dividerWidth = 2f;
            public float screenAspect = 1.75f;
            /// <summary>Seconds a slide transition takes.</summary>
            public float transitionSeconds = 0.45f;
        }

        public struct PlayerInput
        {
            public int playerIndex;
            /// <summary>
            /// The player's position on the screen of the camera its region draws (its own,
            /// or the camera whose screen it shares), normalized. A lone player's view shows
            /// that picture unpanned; with several regions each is centred on its player.
            /// </summary>
            public Vector2 screenPos;
        }

        public sealed class ViewportState
        {
            public int cameraNumber;
            public bool ghost;
            public bool rendering;
            /// <summary>The camera whose picture this view draws; -1 for its own. The solver's regions always draw their own; Adaptive shares.</summary>
            public int sharesImageWith = -1;
            /// <summary>The region as it is drawn this frame; a slide moves it towards <see cref="targetPolygon"/>.</summary>
            public Vector2[] polygon;
            /// <summary>The resting rectangle of this region.</summary>
            public Vector2[] targetPolygon;
            public float areaFraction;
            public Vector2 regionAnchor;
            /// <summary>
            /// The window that must stay inside the source image: the region's own bounding
            /// box as drawn this frame.
            /// </summary>
            public Vector2 windowMin;
            public Vector2 windowMax;
            /// <summary>
            /// Where the player may be placed on screen: the window shrunk by a margin, so a
            /// player is always inside their own region while the screen is split.
            /// </summary>
            public Vector2 anchorMin;
            public Vector2 anchorMax;
            public float zoom;
            /// <summary>1 while the screen is split (the view centres its player), 0 for a lone full-screen view (the picture unpanned).</summary>
            public float splitAmount;
            public Vector2 centroid;
            public Vector2 site;
        }

        public struct DividerSegment
        {
            public int firstCamera;
            public int secondCamera;
            public Vector2 start;
            public Vector2 end;
            public float width;
        }

        public sealed class Layout
        {
            public ViewportState[] viewports;
            public DividerSegment[] dividers;
            public PlayerInput[] effectiveInputs;
            /// <summary>True while regions are sliding towards their target rectangles.</summary>
            public bool sliding;
            /// <summary>True on the tick a slide started, i.e. the layout tree changed shape.</summary>
            public bool restructured;
        }

        private sealed class Memory
        {
            public PlayerInput lastInput;
            public float lastArea;
            public float deathStartArea;
            public float missingFor;
            public bool wasPresent;
        }

        /// <summary>Per layout-node damped state. A node is the set of cameras it partitions.</summary>
        private sealed class NodeMemory
        {
            /// <summary>The cameras the node last partitioned, in slot order; a change restructures.</summary>
            public long layoutKey = -1;
            public float lastUsed;
            public float fraction = -1f;
            public float fractionVelocity;
        }

        private struct Box
        {
            public float x, y, w, h;
            public Box(float x, float y, float w, float h) { this.x = x; this.y = y; this.w = w; this.h = h; }
            public Vector2 Center { get { return new Vector2(x + w * 0.5f, y + h * 0.5f); } }
            public float Area { get { return w * h; } }
            public Vector2[] Polygon()
            {
                return new[] { new Vector2(x, y), new Vector2(x + w, y), new Vector2(x + w, y + h), new Vector2(x, y + h) };
            }
            public static Box Lerp(Box a, Box b, float t)
            {
                return new Box(Mathf.Lerp(a.x, b.x, t), Mathf.Lerp(a.y, b.y, t), Mathf.Lerp(a.w, b.w, t), Mathf.Lerp(a.h, b.h, t));
            }
            public static Box Of(Vector2[] polygon)
            {
                Vector2 min = new Vector2(1f, 1f), max = Vector2.zero;
                Bounds(polygon, ref min, ref max);
                return new Box(min.x, min.y, Mathf.Max(0f, max.x - min.x), Mathf.Max(0f, max.y - min.y));
            }
        }

        /// <summary>One region of the layout tree: one player.</summary>
        private sealed class Item
        {
            /// <summary>Index of the player in this tick's input.</summary>
            public int player;
            /// <summary>Slot order and node identity: the camera number.</summary>
            public int camera;
            public float weight;
            public Box box;
            public Vector2[] Polygon() { return box.Polygon(); }
        }

        private sealed class ItemOrder : IComparer<Item>
        {
            public static readonly ItemOrder Instance = new ItemOrder();
            public int Compare(Item a, Item b) { return a.camera.CompareTo(b.camera); }
        }

        private enum TransitionMode { None, Slide }

        // Solve runs every game tick. What never leaves it is kept here (at most four
        // players). Everything in the Layout it returns is new on every call: callers
        // and the slide memory keep earlier layouts' arrays.
        private readonly bool[] scratchAlive = new bool[4];
        private readonly List<PlayerInput> scratchEffective = new List<PlayerInput>(4);
        private readonly bool[] scratchGhosts = new bool[4];
        private readonly float[] scratchTargetAreas = new float[4];
        private readonly Vector2[][] scratchCells = new Vector2[4][];
        private readonly Vector2[][] scratchShown = new Vector2[4][];
        private readonly List<Item> scratchItems = new List<Item>(4);
        private readonly List<Item> partitionRest = new List<Item>(2);
        private readonly List<Item> partitionTop = new List<Item>(2);
        private readonly List<Item> partitionBottom = new List<Item>(2);
        private readonly List<DividerSegment> scratchDividers = new List<DividerSegment>(6);
        private readonly Item[] itemPool = { new Item(), new Item(), new Item(), new Item() };

        private readonly Dictionary<int, Memory> memory = new Dictionary<int, Memory>();
        private readonly Dictionary<int, NodeMemory> nodes = new Dictionary<int, NodeMemory>();
        private readonly Dictionary<int, Vector2[]> lastShown = new Dictionary<int, Vector2[]>();
        private readonly Dictionary<int, Box> lastTargets = new Dictionary<int, Box>();
        private readonly Dictionary<int, Box> slideFrom = new Dictionary<int, Box>();
        private TransitionMode transition = TransitionMode.None;
        private float transitionT = 1f;
        private float clock;
        private const float Epsilon = 0.00001f;
        private const float DeathTransitionSeconds = 0.7f;
        /// <summary>A layout node nobody has used for this long forgets its damped state.</summary>
        private const float NodeMemorySeconds = 2f;
        /// <summary>
        /// How far inside its region a player is kept while the screen is split, in
        /// normalized screen units (x is a fraction of the width, y of the height).
        /// </summary>
        private static readonly Vector2 VisibleMargin = new Vector2(0.05f, 0.08f);
        /// <summary>Set by the layout tree whenever a node changes shape this tick.</summary>
        private bool structureChanged;

        public void Reset()
        {
            memory.Clear();
            nodes.Clear();
            lastShown.Clear();
            lastTargets.Clear();
            slideFrom.Clear();
            transition = TransitionMode.None;
            transitionT = 1f;
            clock = 0f;
        }

        public Layout Solve(IList<PlayerInput> players, float dt, Settings settings)
        {
            if (players == null) throw new ArgumentNullException(nameof(players));
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (players.Count > 4) throw new ArgumentOutOfRangeException(nameof(players), "At most four cameras are supported.");
            dt = Mathf.Clamp(dt, 0.001f, 0.1f);
            clock += dt;
            structureChanged = false;
            // Forget nodes the tree has not visited recently. A sub-box's damped cut
            // otherwise survives from an old structure (a spawn-in ghost fade left one
            // pair a sliver) and the regions revive at that stale size.
            List<int> forgotten = null;
            foreach (var entry in nodes)
                if (clock - entry.Value.lastUsed > NodeMemorySeconds)
                    (forgotten ?? (forgotten = new List<int>())).Add(entry.Key);
            if (forgotten != null) foreach (int key in forgotten) nodes.Remove(key);
            int aliveCount = players.Count;
            if (aliveCount == 0)
            {
                foreach (Memory state in memory.Values) state.wasPresent = false;
                lastShown.Clear();
                lastTargets.Clear();
                return Empty();
            }
            bool[] aliveNumbers = scratchAlive;
            Array.Clear(aliveNumbers, 0, aliveNumbers.Length);
            List<PlayerInput> effective = scratchEffective;
            effective.Clear();
            for (int i = 0; i < aliveCount; i++)
            {
                PlayerInput input = players[i];
                if (input.playerIndex < 0 || input.playerIndex > 3 || aliveNumbers[input.playerIndex])
                    throw new ArgumentException("Camera numbers must be unique and in 0..3.", nameof(players));
                aliveNumbers[input.playerIndex] = true;
                effective.Add(input);
                Memory existing;
                if (memory.TryGetValue(input.playerIndex, out existing))
                {
                    existing.lastInput = input;
                    existing.missingFor = 0f;
                    existing.wasPresent = true;
                }
            }
            // A player who has just died stays as a ghost while their region shrinks shut.
            foreach (var entry in memory)
            {
                if (aliveNumbers[entry.Key] || !entry.Value.wasPresent ||
                    entry.Value.missingFor >= DeathTransitionSeconds || effective.Count >= 4) continue;
                if (entry.Value.missingFor <= 0f)
                    entry.Value.deathStartArea = entry.Value.lastArea;
                entry.Value.missingFor += dt;
                if (entry.Value.missingFor < DeathTransitionSeconds)
                    effective.Add(entry.Value.lastInput);
                else entry.Value.wasPresent = false;
            }
            players = effective;
            int count = players.Count;
            if (count == 0) return Empty();
            bool[] ghosts = scratchGhosts;
            for (int i = 0; i < count; i++) ghosts[i] = !aliveNumbers[players[i].playerIndex];
            float aspect = Mathf.Max(0.1f, settings.screenAspect);
            for (int i = 0; i < count; i++)
            {
                if (ghosts[i]) continue;
                Memory state;
                if (!memory.TryGetValue(players[i].playerIndex, out state))
                {
                    state = new Memory();
                    memory.Add(players[i].playerIndex, state);
                }
                state.lastInput = players[i];
                state.wasPresent = true;
            }

            float[] targetAreas = scratchTargetAreas;
            float ghostTotal = 0f;
            for (int i = 0; i < count; i++)
                if (ghosts[i])
                {
                    Memory state = memory[players[i].playerIndex];
                    float fade = Mathf.Clamp01(1f - state.missingFor / DeathTransitionSeconds);
                    // Keep the area at the moment of death as the fade's origin.
                    // Reusing the shrinking current area compounds the fade every
                    // frame and makes the region collapse almost immediately.
                    targetAreas[i] = state.deathStartArea * fade * fade;
                    ghostTotal += targetAreas[i];
                }
            for (int i = 0; i < count; i++)
                if (!ghosts[i]) targetAreas[i] = (1f - ghostTotal) / Mathf.Max(1, aliveCount);

            // One item of the layout tree per player; a ghost fades out through its weight.
            List<Item> items = scratchItems;
            items.Clear();
            for (int i = 0; i < count; i++)
            {
                Item item = itemPool[i];
                item.player = i;
                item.camera = players[i].playerIndex;
                item.weight = targetAreas[i] * Mathf.Max(1, aliveCount);
                item.box = default(Box);
                items.Add(item);
            }
            Partition(new Box(0f, 0f, 1f, 1f), items, aspect, settings, dt);
            Vector2[][] cells = scratchCells;
            for (int k = 0; k < items.Count; k++) cells[items[k].player] = items[k].Polygon();

            // ---- Transitions ---------------------------------------------------
            // Two regions never need one: an arrival or a death with two players left
            // resizes the two halves in place. Three or more slide from their previous
            // rectangles to their new ones when the tree restructures.
            Vector2[][] shown = scratchShown;
            for (int i = 0; i < count; i++) shown[i] = cells[i];
            // A slide starts only when the tree changed shape: a player came or went.
            // Regions that merely move because a dying player's weight is still settling
            // keep moving smoothly; treating that motion as a restructure restarted the
            // slide every tick and left the layout crawling.
            bool snapped = count >= 3 && structureChanged;
            if (snapped) BeginSlide();
            bool restructured = snapped;
            bool sliding = false;
            if (transition == TransitionMode.Slide)
            {
                transitionT = Mathf.Min(1f, transitionT + dt / Mathf.Max(0.05f, settings.transitionSeconds));
                float eased = Smoothstep(0f, 1f, transitionT);
                if (transitionT < 1f && count >= 3)
                    for (int i = 0; i < count; i++)
                    {
                        Box from;
                        if (ghosts[i] || !slideFrom.TryGetValue(players[i].playerIndex, out from)) continue;
                        shown[i] = Box.Lerp(from, Box.Of(cells[i]), eased).Polygon();
                        sliding = true;
                    }
                if (transitionT >= 1f || count < 3)
                {
                    transition = TransitionMode.None;
                    slideFrom.Clear();
                }
            }
            for (int i = 0; i < count; i++)
            {
                if (ghosts[i]) { lastTargets.Remove(players[i].playerIndex); lastShown.Remove(players[i].playerIndex); continue; }
                lastTargets[players[i].playerIndex] = Box.Of(cells[i]);
                lastShown[players[i].playerIndex] = shown[i];
            }
            List<int> stale = null;
            foreach (int key in lastShown.Keys)
            {
                bool present = false;
                for (int i = 0; i < count; i++) if (players[i].playerIndex == key) { present = true; break; }
                if (!present) (stale ?? (stale = new List<int>())).Add(key);
            }
            if (stale != null) foreach (int key in stale) { lastShown.Remove(key); lastTargets.Remove(key); }

            // ---- Pans --------------------------------------------------------
            // A lone view shows its camera's picture unpanned. While the screen is split
            // each region centres its player, as far as the one prebaked screen allows
            // (the window must stay inside it), zoomed out by the zoom exponent.
            float split = count > 1 ? 1f : 0f;
            ViewportState[] viewports = new ViewportState[count];
            for (int i = 0; i < count; i++)
            {
                float area = Area(shown[i]);
                float cellArea = Area(cells[i]);
                memory[players[i].playerIndex].lastArea = cellArea;
                Vector2 source = Finite(players[i].screenPos) ? players[i].screenPos : new Vector2(0.5f, 0.5f);
                Vector2 min = new Vector2(1f, 1f), max = Vector2.zero;
                Bounds(shown[i], ref min, ref max);
                Vector2 center = (min + max) * 0.5f;
                float zoomTarget = Mathf.Clamp(Mathf.Pow(Mathf.Max(Epsilon, cellArea), Mathf.Max(0f, settings.zoomExponent)),
                    Mathf.Clamp(settings.minZoom, 0.1f, 1f), 1f);
                // The sampled window is the region's box divided by zoom, and it has to fit
                // inside the one prebaked screen the camera rendered. A sliding region can
                // widen that box for a moment; the zoom follows.
                zoomTarget = Mathf.Max(zoomTarget, Mathf.Max(max.x - min.x, max.y - min.y));
                float zoom = Mathf.Lerp(1f, zoomTarget, split);
                Vector2 centroid = Centroid(shown[i]);
                Vector2 anchorMin, anchorMax;
                PanBounds(min, max, split, out anchorMin, out anchorMax);
                Vector2 anchor = ClampVector(Vector2.Lerp(source, center, split), anchorMin, anchorMax);
                viewports[i] = new ViewportState
                {
                    cameraNumber = players[i].playerIndex,
                    ghost = ghosts[i],
                    rendering = !ghosts[i],
                    polygon = shown[i],
                    targetPolygon = cells[i],
                    areaFraction = area,
                    centroid = centroid,
                    site = centroid,
                    regionAnchor = anchor,
                    windowMin = min,
                    windowMax = max,
                    anchorMin = anchorMin,
                    anchorMax = anchorMax,
                    zoom = zoom,
                    splitAmount = split
                };
            }

            List<DividerSegment> dividers = scratchDividers;
            dividers.Clear();
            for (int i = 0; i < count; i++)
            for (int j = i + 1; j < count; j++)
                AddSharedEdges(viewports[i].polygon, viewports[j].polygon, players[i].playerIndex, players[j].playerIndex,
                    settings.dividerWidth, dividers);
            return new Layout { viewports = viewports, dividers = dividers.ToArray(),
                effectiveInputs = effective.ToArray(), sliding = sliding, restructured = restructured };
        }

        /// <summary>
        /// A target rectangle moved discontinuously in a three- or four-region layout:
        /// slide every region from its previous rectangle to its new one.
        /// </summary>
        private void BeginSlide()
        {
            transition = TransitionMode.Slide;
            transitionT = 0f;
            slideFrom.Clear();
            foreach (var entry in lastShown) slideFrom[entry.Key] = Box.Of(entry.Value);
        }

        private static Layout Empty()
        {
            return new Layout { viewports = new ViewportState[0], dividers = new DividerSegment[0],
                effectiveInputs = new PlayerInput[0] };
        }

        // ---- Layout tree -------------------------------------------------------

        /// <summary>
        /// Fixed slots. Items are ordered by camera number and placed: two side by side
        /// (or stacked when the box is taller than wide), the lower number left or top;
        /// three as the lowest number's half on top of two quarters; four as a 2x2 grid.
        /// World positions play no part, so the only way the tree changes shape is a
        /// player arriving or leaving. Cut positions still follow the live weights
        /// (damped), so a dying player's region slides shut.
        /// </summary>
        private void Partition(Box box, List<Item> items, float aspect, Settings settings, float dt)
        {
            if (items.Count == 1)
            {
                items[0].box = box;
                return;
            }
            // A kept comparer: Sort(Comparison) wraps the lambda in a new comparer per call.
            items.Sort(ItemOrder.Instance);
            int mask = 0;
            long key = 0;
            for (int i = 0; i < items.Count; i++)
            {
                mask |= 1 << items[i].camera;
                key = key * 16 + (1 << items[i].camera);
            }
            NodeMemory node;
            if (!nodes.TryGetValue(mask, out node))
            {
                node = new NodeMemory();
                nodes.Add(mask, node);
            }
            node.lastUsed = clock;
            bool changed = node.layoutKey != key;
            if (changed) structureChanged = true;
            node.layoutKey = key;

            if (items.Count == 2)
            {
                // Side by side unless the box is taller than it is wide on screen.
                bool sideBySide = box.w * aspect >= box.h;
                Item low = sideBySide ? items[0] : items[1];
                Item high = sideBySide ? items[1] : items[0];
                Box lowBox, highBox;
                CutBox(box, sideBySide ? 0 : 1, low.weight, high.weight, node, settings, dt, changed, out lowBox, out highBox);
                low.box = lowBox;
                high.box = highBox;
                return;
            }
            if (items.Count == 3)
            {
                // The scratch pairs are safe to reuse: a call is only ever given two,
                // three or four items and only three and four recurse, always with two.
                List<Item> rest = partitionRest;
                rest.Clear();
                rest.Add(items[1]);
                rest.Add(items[2]);
                // The top half is weighed against the larger of the two quarters, not
                // their sum: three players give one half and two quarters.
                float restWeight = Mathf.Max(Mathf.Max(0f, items[1].weight), items[2].weight);
                Box restBox, firstBox;
                CutBox(box, 1, restWeight, items[0].weight, node, settings, dt, changed, out restBox, out firstBox);
                items[0].box = firstBox;
                Partition(restBox, rest, aspect, settings, dt);
                return;
            }
            // Four items: two rows of two, so the result is a grid instead of one
            // column holding three stacked slivers.
            {
                List<Item> top = partitionTop;
                List<Item> bottom = partitionBottom;
                top.Clear();
                top.Add(items[0]);
                top.Add(items[1]);
                bottom.Clear();
                bottom.Add(items[2]);
                bottom.Add(items[3]);
                Box bottomBox, topBox;
                CutBox(box, 1, bottom[0].weight + bottom[1].weight, top[0].weight + top[1].weight, node, settings, dt, changed,
                    out bottomBox, out topBox);
                Partition(topBox, top, aspect, settings, dt);
                Partition(bottomBox, bottom, aspect, settings, dt);
            }
        }

        /// <summary>
        /// Cut <paramref name="box"/> along <paramref name="axis"/> (0: the low box is
        /// left, 1: the low box is at the bottom). While the node keeps its layout the
        /// cut follows the live weights, damped, so a dying player's region shrinks
        /// smoothly. When the layout changed the cut snaps to its target: the resting
        /// rectangles are then final at once and the slide in <see cref="Solve"/> is
        /// the only animation, instead of a damped cut on a new axis dragging the
        /// resting layout through shapes nobody asked for.
        /// </summary>
        private void CutBox(Box box, int axis, float lowWeight, float highWeight, NodeMemory node,
            Settings settings, float dt, bool changed, out Box lowBox, out Box highBox)
        {
            float target = lowWeight / Mathf.Max(Epsilon, lowWeight + highWeight);
            if (node.fraction < 0f || changed)
            {
                node.fraction = target;
                node.fractionVelocity = 0f;
            }
            else
                node.fraction = Mathf.SmoothDamp(node.fraction, target, ref node.fractionVelocity,
                    Mathf.Max(0.01f, settings.smoothingTime), Mathf.Infinity, dt);
            float fraction = Mathf.Clamp01(node.fraction);
            if (axis == 0)
            {
                lowBox = new Box(box.x, box.y, box.w * fraction, box.h);
                highBox = new Box(box.x + box.w * fraction, box.y, box.w * (1f - fraction), box.h);
            }
            else
            {
                lowBox = new Box(box.x, box.y, box.w, box.h * fraction);
                highBox = new Box(box.x, box.y + box.h * fraction, box.w, box.h * (1f - fraction));
            }
        }

        // ---- Helpers ------------------------------------------------------------

        /// <summary>
        /// Whether a camera's prebaked screen shows <paramref name="screenPosition"/> with
        /// room to spare: the rule for a region to draw another camera's picture of the
        /// same screen (picture sharing in UpdateDynamicLayout).
        /// </summary>
        public static bool SharedCameraCanShow(Vector2 screenPosition)
        {
            return Finite(screenPosition) && screenPosition.x >= 0.05f && screenPosition.x <= 0.95f &&
                screenPosition.y >= 0.05f && screenPosition.y <= 0.95f;
        }

        /// <summary>
        /// The range a region's player anchor may take: the region's box shrunk by
        /// <see cref="VisibleMargin"/> scaled by the split, so a player is always inside
        /// their own region while the screen is split, and a lone view is not constrained.
        /// </summary>
        public static void PanBounds(Vector2 min, Vector2 max, float split, out Vector2 anchorMin, out Vector2 anchorMax)
        {
            Vector2 margin = VisibleMargin * Mathf.Clamp01(split);
            Vector2 half = (max - min) * 0.5f;
            margin = new Vector2(Mathf.Min(margin.x, half.x), Mathf.Min(margin.y, half.y));
            anchorMin = min + margin;
            anchorMax = max - margin;
        }

        public static Vector2 ClampVector(Vector2 value, Vector2 min, Vector2 max)
        {
            return new Vector2(min.x <= max.x ? Mathf.Clamp(value.x, min.x, max.x) : (min.x + max.x) * 0.5f,
                min.y <= max.y ? Mathf.Clamp(value.y, min.y, max.y) : (min.y + max.y) * 0.5f);
        }

        /// <summary>
        /// The uv translation that shows source point <paramref name="playerSource"/>
        /// at display point <paramref name="anchor"/>, clamped so the rectangle
        /// [<paramref name="min"/>, <paramref name="max"/>] never samples outside the
        /// source texture. For an axis-aligned region this clamp is exact, and a region
        /// of width w always keeps 1-w of pan, so the player is always displayable.
        /// </summary>
        public static Vector2 ClampedUvShift(Vector2 playerSource, Vector2 anchor, Vector2 min, Vector2 max, float zoom)
        {
            zoom = Mathf.Max(0.1f, zoom);
            Vector2 shift = playerSource - anchor / zoom;
            float minShiftX = -min.x / zoom, maxShiftX = 1f - max.x / zoom;
            float minShiftY = -min.y / zoom, maxShiftY = 1f - max.y / zoom;
            shift.x = minShiftX <= maxShiftX ? Mathf.Clamp(shift.x, minShiftX, maxShiftX) : (minShiftX + maxShiftX) * 0.5f;
            shift.y = minShiftY <= maxShiftY ? Mathf.Clamp(shift.y, minShiftY, maxShiftY) : (minShiftY + maxShiftY) * 0.5f;
            return shift;
        }

        private static float Smoothstep(float start, float end, float value)
        {
            float t = Mathf.Clamp01((value - start) / Mathf.Max(Epsilon, end - start));
            return t * t * (3f - 2f * t);
        }

        private static bool Finite(Vector2 value)
        {
            return !float.IsNaN(value.x) && !float.IsNaN(value.y) && !float.IsInfinity(value.x) && !float.IsInfinity(value.y);
        }

        private static void Bounds(Vector2[] polygon, ref Vector2 min, ref Vector2 max)
        {
            for (int i = 0; i < polygon.Length; i++)
            {
                min.x = Mathf.Min(min.x, polygon[i].x); min.y = Mathf.Min(min.y, polygon[i].y);
                max.x = Mathf.Max(max.x, polygon[i].x); max.y = Mathf.Max(max.y, polygon[i].y);
            }
        }

        private static float Area(Vector2[] polygon)
        {
            float twice = 0f;
            for (int i = 0; i < polygon.Length; i++)
            {
                Vector2 a = polygon[i], b = polygon[(i + 1) % polygon.Length];
                twice += a.x * b.y - b.x * a.y;
            }
            return Mathf.Abs(twice) * 0.5f;
        }

        private static Vector2 Centroid(Vector2[] polygon)
        {
            if (polygon.Length == 0) return new Vector2(0.5f, 0.5f);
            float twice = 0f;
            Vector2 sum = Vector2.zero;
            for (int i = 0; i < polygon.Length; i++)
            {
                Vector2 a = polygon[i], b = polygon[(i + 1) % polygon.Length];
                float cross = a.x * b.y - b.x * a.y;
                twice += cross;
                sum += (a + b) * cross;
            }
            if (Mathf.Abs(twice) < Epsilon) return polygon[0];
            return sum / (3f * twice);
        }

        private static void AddSharedEdges(Vector2[] a, Vector2[] b, int first, int second,
            float width, List<DividerSegment> output)
        {
            for (int i = 0; i < a.Length; i++)
            {
                Vector2 start = a[i], end = a[(i + 1) % a.Length];
                if ((end - start).sqrMagnitude < Epsilon) continue;
                for (int j = 0; j < b.Length; j++)
                {
                    Vector2 otherStart = b[j], otherEnd = b[(j + 1) % b.Length];
                    Vector2 axis = end - start;
                    float length = axis.magnitude;
                    axis /= length;
                    if (Mathf.Abs(Cross(axis, otherStart - start)) > 0.0001f ||
                        Mathf.Abs(Cross(axis, otherEnd - start)) > 0.0001f) continue;
                    float lo = Mathf.Max(0f, Mathf.Min(Vector2.Dot(otherStart - start, axis), Vector2.Dot(otherEnd - start, axis)));
                    float hi = Mathf.Min(length, Mathf.Max(Vector2.Dot(otherStart - start, axis), Vector2.Dot(otherEnd - start, axis)));
                    if (hi - lo < 0.0001f) continue;
                    output.Add(new DividerSegment { firstCamera = first, secondCamera = second,
                        start = start + axis * lo, end = start + axis * hi, width = width });
                }
            }
        }

        private static float Cross(Vector2 a, Vector2 b) { return a.x * b.y - a.y * b.x; }
    }
}
