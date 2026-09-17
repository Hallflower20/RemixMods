using System;
using System.Collections.Generic;
using UnityEngine;

namespace SplitScreenCoop
{
    /// <summary>
    /// Game-independent screen geometry. Coordinates in the output are normalized
    /// bottom-left to top-right.
    ///
    /// At rest every cell is an axis-aligned rectangle. That is not a stylistic
    /// choice: each view samples one prebaked room screen, so a cell can only pan by
    /// however much of the source its bounding box leaves uncovered. A rectangle of
    /// width w keeps 1-w of horizontal pan and always has a legal pan that shows its
    /// own player. A diagonal cell touching two opposite screen edges has no pan at
    /// all, and the followed slugcat can end up on the far side of its own divider.
    ///
    /// Nothing ever fades between images. Two players on one prebaked screen merge
    /// the way a split screen in a LEGO game does: their cells keep drawing the same
    /// image, and the pan of each cell moves from "centre my player" to "identity"
    /// as the split amount falls, so the two halves line up and the divider
    /// disappears. Two cells are separated by one straight line through the screen
    /// centre whose angle follows the players continuously (damped); three or more
    /// cells are rectangles that slide when the layout tree restructures.
    /// </summary>
    public sealed class SplitLayoutSolver
    {
        public sealed class Settings
        {
            public float mergeDistance = 600f;
            public float blendWidth = 250f;
            public float minZoom = 0.5f;
            public float zoomExponent = 0f;
            public float smoothingTime = 0.18f;
            public float dividerWidth = 2f;
            public float screenAspect = 1.75f;
            public bool permanentSplit;
            /// <summary>World-unit dead zone before two cells swap sides.</summary>
            public float directionDeadZone = 160f;
            /// <summary>Minimum seconds between structural changes of one layout node.</summary>
            public float layoutHoldSeconds = 2f;
            /// <summary>Seconds a slide transition takes.</summary>
            public float transitionSeconds = 0.45f;
            /// <summary>Response time of a two-cell divider's angle. A side swap is a half turn.</summary>
            public float dividerTurnSeconds = 0.3f;
            /// <summary>
            /// Response time of the per-pair split amount. When a player's camera cuts
            /// onto the other's screen the pair is often already inside the merge
            /// band, so the target drops to merged in one tick; this is how long the
            /// two views take to glide together. Half a second reads as a camera move
            /// rather than a snap.
            /// </summary>
            public float splitSmoothingTime = 0.5f;
            /// <summary>Response time of the divider's distance-based opacity.</summary>
            public float lineSmoothingTime = 0.25f;
        }

        public struct PlayerInput
        {
            public int playerIndex;
            public Vector2 worldPos;
            public long sameScreenKey;
            /// <summary>Identifies the room the player is in; the divider fades with distance only inside one room.</summary>
            public long roomKey;
            public Vector2 mergedScreenPos;
            public bool validWorldPos;
        }

        public sealed class ViewportState
        {
            public int cameraNumber;
            public bool ghost;
            public bool rendering;
            public int sharesImageWith = -1;
            /// <summary>The cell as it is drawn this frame; a slide moves it towards <see cref="targetPolygon"/>.</summary>
            public Vector2[] polygon;
            /// <summary>The resting rectangle of this cell.</summary>
            public Vector2[] targetPolygon;
            public float areaFraction;
            public float groupAreaFraction;
            public Vector2 regionAnchor;
            public Vector2 groupSourceAnchor;
            public Vector2 groupTargetAnchor;
            /// <summary>Bottom-left corner of the whole group's bounding box.</summary>
            public Vector2 groupMin;
            /// <summary>Top-right corner of the whole group's bounding box.</summary>
            public Vector2 groupMax;
            /// <summary>
            /// The window that must stay inside the source image: the group's box while
            /// the cell pans with its group, blending to the cell's own box as the cell
            /// pans on its own (by <see cref="innerSplit"/>).
            /// </summary>
            public Vector2 windowMin;
            public Vector2 windowMax;
            /// <summary>
            /// Where the player may be placed on screen: the window shrunk by a margin
            /// that grows with the split, so a player is always inside their own cell
            /// once its image has parted from a neighbour's.
            /// </summary>
            public Vector2 anchorMin;
            public Vector2 anchorMax;
            public float zoom;
            /// <summary>Largest split amount against anybody: max of the two below.</summary>
            public float splitAmount;
            /// <summary>The joined group's split against players outside it; pans the group as one image.</summary>
            public float groupSplit;
            /// <summary>This cell's split against its own group-mates; blends its pan from the group transform to its own centre.</summary>
            public float innerSplit;
            public float imageBlend;
            public Vector2 centroid;
            public Vector2 site;
        }

        public struct DividerSegment
        {
            public int firstCamera;
            public int secondCamera;
            public Vector2 start;
            public Vector2 end;
            public float alpha;
            public float width;
        }

        public sealed class Layout
        {
            public ViewportState[] viewports;
            public DividerSegment[] dividers;
            public float[,] pairSplitAmounts;
            public PlayerInput[] effectiveInputs;
            /// <summary>True while cells are sliding towards their target rectangles.</summary>
            public bool sliding;
            /// <summary>True on the tick a slide started, i.e. the layout tree changed shape.</summary>
            public bool restructured;
        }

        private sealed class Memory
        {
            public Vector2 worldPos;
            public bool hasWorldPos;
            public float visibleFor;
            public PlayerInput lastInput;
            public float lastArea;
            public float deathStartArea;
            public float missingFor;
            public bool wasPresent;
        }

        /// <summary>Per layout-node hysteresis. A node is the set of cameras it partitions.</summary>
        private sealed class NodeMemory
        {
            public int itemCount;
            public int axis = -1;
            public bool reversed;
            public int splitOffMask;
            public float changedAt = -1000f;
            public float lastUsed;
            /// <summary>Four items: which two form the low pair, kept while they are in different rooms.</summary>
            public int lowMask;
            public float fraction = -1f;
            public float fractionVelocity;
            /// <summary>Damped angle of a two-cell divider's normal, radians.</summary>
            public float angle;
            public float angleVelocity;
            public bool hasAngle;
        }

        private sealed class PairMemory
        {
            public bool joined;
            public float split = -1f;
            public float splitVelocity;
            /// <summary>Damped divider opacity: distance-based inside one room, 1 across rooms.</summary>
            public float line = -1f;
            public float lineVelocity;
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

        private sealed class Item
        {
            public readonly List<int> members = new List<int>(4);
            public int mask;
            public float weight;
            public float structuralWeight;
            public Vector2 center;
            public Box box;
            /// <summary>Set when the cell is not a rectangle (a rotating two-cell divider).</summary>
            public Vector2[] shape;
            public long screenKey;
            public long roomKey;
            /// <summary>False when the item's members are in different rooms.</summary>
            public bool sameRoom = true;
            public Vector2[] Polygon() { return shape ?? box.Polygon(); }
        }

        /// <summary>
        /// Positions only mean something between players in one room: on the region
        /// map two rooms apart are just two rooms apart. Sides, axes and split-offs
        /// are therefore re-decided only while everybody in the node shares a room;
        /// otherwise the node keeps what it has until the players pipe together.
        /// </summary>
        private static bool AllInOneRoom(List<Item> items)
        {
            for (int i = 0; i < items.Count; i++)
                if (!items[i].sameRoom || items[i].roomKey != items[0].roomKey) return false;
            return true;
        }

        private enum TransitionMode { None, Slide }

        private readonly Dictionary<int, Memory> memory = new Dictionary<int, Memory>();
        private readonly Dictionary<long, PairMemory> pairs = new Dictionary<long, PairMemory>();
        private readonly Dictionary<int, NodeMemory> nodes = new Dictionary<int, NodeMemory>();
        private readonly Dictionary<int, Vector2[]> lastShown = new Dictionary<int, Vector2[]>();
        private readonly Dictionary<int, Box> lastTargets = new Dictionary<int, Box>();
        private readonly Dictionary<int, Box> slideFrom = new Dictionary<int, Box>();
        private TransitionMode transition = TransitionMode.None;
        private float transitionT = 1f;
        private float clock;
        private const float Epsilon = 0.00001f;
        private const float DeathTransitionSeconds = 0.7f;
        private const float MinCellExtent = 0.3f;
        private const float AspectWeight = 0.75f;
        private const float SwitchMargin = 0.35f;
        /// <summary>A layout node nobody has used for this long forgets its damped state.</summary>
        private const float NodeMemorySeconds = 2f;
        /// <summary>
        /// How far inside its cell a fully split player is kept, in normalized screen
        /// units (x is a fraction of the width, y of the height).
        /// </summary>
        private static readonly Vector2 VisibleMargin = new Vector2(0.05f, 0.08f);
        /// <summary>Set by the layout tree whenever a node changes shape this tick.</summary>
        private bool structureChanged;
        /// <summary>A pair joins below this split amount and, once joined, only parts above <see cref="UnjoinAbove"/>.</summary>
        private const float JoinBelow = 0.02f;
        private const float UnjoinAbove = 0.95f;

        public void Reset()
        {
            memory.Clear();
            pairs.Clear();
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
            // pair a sliver) and the cells revive at that stale size.
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
            bool[] aliveNumbers = new bool[4];
            List<PlayerInput> effective = new List<PlayerInput>(4);
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
            players = effective.ToArray();
            int count = players.Count;
            if (count == 0) return Empty();
            bool[] ghosts = new bool[count];
            for (int i = 0; i < count; i++) ghosts[i] = !aliveNumbers[players[i].playerIndex];
            float aspect = Mathf.Max(0.1f, settings.screenAspect);

            Vector2[] world = new Vector2[count];
            for (int i = 0; i < count; i++)
            {
                PlayerInput input = players[i];
                Memory state;
                if (!memory.TryGetValue(input.playerIndex, out state))
                {
                    state = new Memory();
                    memory.Add(input.playerIndex, state);
                }
                if (!ghosts[i])
                {
                    state.lastInput = input;
                    state.wasPresent = true;
                }
                if (input.validWorldPos && Finite(input.worldPos))
                {
                    state.worldPos = input.worldPos;
                    state.hasWorldPos = true;
                }
                world[i] = state.hasWorldPos ? state.worldPos : input.worldPos;
                state.visibleFor += dt;
            }

            float[] targetAreas = new float[count];
            float ghostTotal = 0f;
            for (int i = 0; i < count; i++)
                if (ghosts[i])
                {
                    Memory state = memory[players[i].playerIndex];
                    float fade = Mathf.Clamp01(1f - state.missingFor / DeathTransitionSeconds);
                    // Keep the area at the moment of death as the fade's origin.
                    // Reusing the shrinking current area compounds the fade every
                    // frame and makes the cell collapse almost immediately.
                    targetAreas[i] = state.deathStartArea * fade * fade;
                    ghostTotal += targetAreas[i];
                }
            for (int i = 0; i < count; i++)
                if (!ghosts[i]) targetAreas[i] = (1f - ghostTotal) / Mathf.Max(1, aliveCount);

            float[,] amounts = new float[count, count];
            float[,] lineAmounts = new float[count, count];
            bool[,] joined = new bool[count, count];
            for (int i = 0; i < count; i++)
            for (int j = i + 1; j < count; j++)
            {
                bool sameScreen = players[i].sameScreenKey == players[j].sameScreenKey;
                bool sameRoom = players[i].roomKey == players[j].roomKey;
                Vector2 delta = world[j] - world[i];
                float distance = new Vector2(delta.x, delta.y * aspect).magnitude;
                float byDistance = Smoothstep(settings.mergeDistance, settings.mergeDistance + Mathf.Max(1f, settings.blendWidth), distance);
                float target = ghosts[i] || ghosts[j] || settings.permanentSplit || !sameScreen ? 1f : byDistance;
                // The divider's own opacity follows distance whenever the players are
                // in one room, even while their cameras still sit on different
                // screens: that is the cue that the views are about to merge or have
                // just parted. The split amount above cannot do that job because it
                // also decides when two cells may share one image.
                float lineTarget = ghosts[i] || ghosts[j] || settings.permanentSplit || !sameRoom ? 1f : byDistance;
                long key = PairKey(players[i].playerIndex, players[j].playerIndex);
                PairMemory pair;
                if (!pairs.TryGetValue(key, out pair))
                {
                    pair = new PairMemory();
                    pairs.Add(key, pair);
                }
                float split;
                if (pair.split < 0f || ghosts[i] || ghosts[j]) split = target;
                else
                {
                    split = Mathf.SmoothDamp(pair.split, target, ref pair.splitVelocity,
                        Mathf.Max(0.01f, settings.splitSmoothingTime), Mathf.Infinity, dt);
                    if (Mathf.Abs(split - target) < 0.002f) { split = target; pair.splitVelocity = 0f; }
                }
                pair.split = split;
                amounts[i, j] = amounts[j, i] = split;
                float line;
                if (pair.line < 0f || ghosts[i] || ghosts[j]) line = lineTarget;
                else
                {
                    line = Mathf.SmoothDamp(pair.line, lineTarget, ref pair.lineVelocity,
                        Mathf.Max(0.01f, settings.lineSmoothingTime), Mathf.Infinity, dt);
                    if (Mathf.Abs(line - lineTarget) < 0.002f) { line = lineTarget; pair.lineVelocity = 0f; }
                }
                pair.line = line;
                lineAmounts[i, j] = lineAmounts[j, i] = line;
                // Wide hysteresis: a joined pair is one item of the layout tree, and
                // every join or unjoin restructures a three- or four-player layout.
                // Parting only well outside the merge distance stops two players
                // who hover around it from sliding the whole layout back and forth.
                bool nowJoined = !ghosts[i] && !ghosts[j] && !settings.permanentSplit && sameScreen &&
                    (pair.joined ? split < UnjoinAbove : split < JoinBelow);
                pair.joined = nowJoined;
                joined[i, j] = joined[j, i] = nowJoined;
            }

            int[] leaders = new int[count];
            for (int i = 0; i < count; i++) leaders[i] = i;
            for (int i = 0; i < count; i++)
            for (int j = i + 1; j < count; j++)
                if (joined[i, j]) Union(leaders, i, j);

            // One layout item per image group. Ghosts are never joined, so they are
            // always their own item and fade out through their weight.
            List<Item> items = new List<Item>(4);
            Item[] itemOfPlayer = new Item[count];
            for (int i = 0; i < count; i++)
            {
                int leader = Find(leaders, i);
                Item item = null;
                for (int k = 0; k < items.Count; k++)
                    if (Find(leaders, items[k].members[0]) == leader) { item = items[k]; break; }
                if (item == null)
                {
                    item = new Item();
                    items.Add(item);
                }
                if (item.members.Count == 0) item.roomKey = players[i].roomKey;
                else if (item.roomKey != players[i].roomKey) item.sameRoom = false;
                item.members.Add(i);
                item.mask |= 1 << players[i].playerIndex;
                item.weight += targetAreas[i] * Mathf.Max(1, aliveCount);
                item.structuralWeight += 1f;
                item.center += Corrected(world[i], aspect);
                item.screenKey = players[i].sameScreenKey;
                itemOfPlayer[i] = item;
            }
            foreach (Item item in items) item.center /= item.members.Count;

            Partition(new Box(0f, 0f, 1f, 1f), items, 0, aspect, settings, dt);

            // Members of one group tile the group's rectangle. They all draw the same
            // image, so only the HUD and the divider bookkeeping care about the slices.
            Vector2[][] cells = new Vector2[count][];
            foreach (Item item in items)
            {
                if (item.members.Count == 1)
                {
                    cells[item.members[0]] = item.Polygon();
                    continue;
                }
                List<Item> slices = new List<Item>(item.members.Count);
                for (int m = 0; m < item.members.Count; m++)
                {
                    int p = item.members[m];
                    Item slice = new Item();
                    slice.members.Add(p);
                    slice.mask = 1 << players[p].playerIndex;
                    slice.weight = targetAreas[p] * Mathf.Max(1, aliveCount);
                    slice.structuralWeight = 1f;
                    slice.center = Corrected(world[p], aspect);
                    slice.screenKey = players[p].sameScreenKey;
                    slice.roomKey = players[p].roomKey;
                    slices.Add(slice);
                }
                Partition(item.box, slices, 1, aspect, settings, dt);
                for (int m = 0; m < slices.Count; m++) cells[slices[m].members[0]] = slices[m].Polygon();
            }

            // ---- Transitions ---------------------------------------------------
            // Two cells never need one: their divider is a single line whose angle
            // and position are damped continuously. Three or more cells slide from
            // their previous rectangles to their new ones when the tree restructures.
            Vector2[][] shown = new Vector2[count][];
            for (int i = 0; i < count; i++) shown[i] = cells[i];
            // A slide starts only when the tree changed shape: a node chose a new
            // axis, side or split-off item, an item joined or parted, or a player
            // came or went. Cells that merely move because a damped cut or a dying
            // player's weight is still settling keep moving smoothly; treating that
            // motion as a restructure restarted the slide every tick and left the
            // layout crawling.
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

            ViewportState[] viewports = new ViewportState[count];
            for (int i = 0; i < count; i++)
            {
                Item item = itemOfPlayer[i];
                int leaderIndex = item.members[0];
                for (int m = 1; m < item.members.Count; m++)
                    if (players[item.members[m]].playerIndex < players[leaderIndex].playerIndex)
                        leaderIndex = item.members[m];
                // Pan in two stages. A joined group pans as one image, so every
                // member samples the shared screen with the same transform and cells
                // on one screen line up exactly at split 0. Each member then blends
                // from that group transform towards its own cell centre by its split
                // against its group-mates only ("inner split"). Joining and parting
                // change the layout tree, but the pan formula stays continuous
                // because a pair only parts once its inner split is nearly complete.
                float groupSplit = 0f, innerSplit = 0f;
                for (int m = 0; m < item.members.Count; m++)
                    for (int j = 0; j < count; j++)
                        if (itemOfPlayer[j] != item)
                            groupSplit = Mathf.Max(groupSplit, amounts[item.members[m], j]);
                for (int m = 0; m < item.members.Count; m++)
                    if (item.members[m] != i) innerSplit = Mathf.Max(innerSplit, amounts[i, item.members[m]]);
                float imageBlend = 0f;
                bool hasSameScreenPeer = false;
                for (int j = 0; j < count; j++)
                    if (j != i && players[j].sameScreenKey == players[i].sameScreenKey)
                    {
                        hasSameScreenPeer = true;
                        imageBlend = Mathf.Max(imageBlend, amounts[i, j]);
                    }
                if (!hasSameScreenPeer && count > 1) imageBlend = 1f;
                float area = Area(shown[i]);
                memory[players[i].playerIndex].lastArea = Area(cells[i]);
                Vector2 sourceSum = Vector2.zero;
                Vector2 groupMin = new Vector2(1f, 1f), groupMax = Vector2.zero;
                float itemArea = 0f;
                for (int m = 0; m < item.members.Count; m++)
                {
                    Vector2 merged = players[item.members[m]].mergedScreenPos;
                    sourceSum += Finite(merged) ? merged : new Vector2(0.5f, 0.5f);
                    Bounds(shown[item.members[m]], ref groupMin, ref groupMax);
                    itemArea += Area(cells[item.members[m]]);
                }
                Vector2 sourceAnchor = sourceSum / Mathf.Max(1, item.members.Count);
                Vector2 targetAnchor = (groupMin + groupMax) * 0.5f;
                Vector2 transformAnchor = Vector2.Lerp(sourceAnchor, targetAnchor, groupSplit);
                float zoomTarget = Mathf.Clamp(Mathf.Pow(Mathf.Max(Epsilon, itemArea), Mathf.Max(0f, settings.zoomExponent)),
                    Mathf.Clamp(settings.minZoom, 0.1f, 1f), 1f);
                // The sampled window is the group's bounding box divided by zoom, and
                // it has to fit inside the one prebaked screen the camera rendered.
                // A rotating divider widens that box for a moment; the zoom follows.
                zoomTarget = Mathf.Max(zoomTarget, Mathf.Max(groupMax.x - groupMin.x, groupMax.y - groupMin.y));
                float zoom = Mathf.Lerp(1f, zoomTarget, Mathf.Max(groupSplit, innerSplit));
                Vector2 playerSource = Finite(players[i].mergedScreenPos) ? players[i].mergedScreenPos : new Vector2(0.5f, 0.5f);
                Vector2 centroid = Centroid(shown[i]);
                Vector2 groupAnchor = transformAnchor + (playerSource - sourceAnchor) * zoom;
                Vector2 cellMin = new Vector2(1f, 1f), cellMax = Vector2.zero;
                Bounds(shown[i], ref cellMin, ref cellMax);
                Vector2 windowMin, windowMax, anchorMin, anchorMax;
                PanBounds(groupMin, groupMax, cellMin, cellMax, innerSplit, Mathf.Max(groupSplit, innerSplit),
                    out windowMin, out windowMax, out anchorMin, out anchorMax);
                Vector2 anchor = ClampVector(Vector2.Lerp(groupAnchor, centroid, innerSplit), anchorMin, anchorMax);
                Memory state = memory[players[i].playerIndex];
                bool mergedSource = leaderIndex != i;
                viewports[i] = new ViewportState
                {
                    cameraNumber = players[i].playerIndex,
                    ghost = ghosts[i],
                    rendering = !ghosts[i] && !mergedSource,
                    sharesImageWith = ghosts[i] ? -1 : mergedSource ? players[leaderIndex].playerIndex : -1,
                    polygon = shown[i],
                    targetPolygon = cells[i],
                    areaFraction = area,
                    groupAreaFraction = itemArea,
                    centroid = centroid,
                    site = centroid,
                    regionAnchor = anchor,
                    groupSourceAnchor = sourceAnchor,
                    groupTargetAnchor = targetAnchor,
                    groupMin = groupMin,
                    groupMax = groupMax,
                    windowMin = windowMin,
                    windowMax = windowMax,
                    anchorMin = anchorMin,
                    anchorMax = anchorMax,
                    zoom = zoom,
                    splitAmount = Mathf.Max(groupSplit, innerSplit),
                    groupSplit = groupSplit,
                    innerSplit = innerSplit,
                    imageBlend = ghosts[i] ? 1f : imageBlend
                };
            }

            List<DividerSegment> dividers = new List<DividerSegment>(6);
            for (int i = 0; i < count; i++)
            for (int j = i + 1; j < count; j++)
            {
                if (amounts[i, j] <= 0f) continue;
                AddSharedEdges(viewports[i].polygon, viewports[j].polygon, players[i].playerIndex, players[j].playerIndex,
                    lineAmounts[i, j], settings.dividerWidth, dividers);
            }
            return new Layout { viewports = viewports, dividers = dividers.ToArray(),
                pairSplitAmounts = amounts, effectiveInputs = effective.ToArray(), sliding = sliding,
                restructured = restructured };
        }

        /// <summary>
        /// A target rectangle moved discontinuously in a three- or four-cell layout:
        /// slide every cell from its previous rectangle to its new one.
        /// </summary>
        private void BeginSlide()
        {
            transition = TransitionMode.Slide;
            transitionT = 0f;
            slideFrom.Clear();
            foreach (var entry in lastShown) slideFrom[entry.Key] = Box.Of(entry.Value);
        }

        private static float WrapAngle(float angle)
        {
            // An infinite angle would spin these loops forever; treat it as no angle.
            if (float.IsNaN(angle) || float.IsInfinity(angle)) return 0f;
            while (angle > Math.PI) angle -= 2f * (float)Math.PI;
            while (angle < -Math.PI) angle += 2f * (float)Math.PI;
            return angle;
        }

        private static bool IsFullScreen(Box box)
        {
            return Mathf.Abs(box.x) < 0.0001f && Mathf.Abs(box.y) < 0.0001f &&
                Mathf.Abs(box.w - 1f) < 0.0001f && Mathf.Abs(box.h - 1f) < 0.0001f;
        }

        /// <summary>
        /// Two cells filling the whole screen: one straight divider through the screen
        /// centre. Its normal turns continuously (damped) towards the players'
        /// on-screen direction while they share a prebaked screen, so the line follows
        /// them the way a LEGO split screen does and never jumps. On different screens
        /// the target is the nearest axis, with the usual dead zone and hold time, so
        /// axis flips and side swaps are rotations as well. The cut through the centre
        /// halves the screen exactly; a dying player's weight slides it off-centre.
        /// </summary>
        private void SplitTwoRotating(Box box, Item a, Item b, NodeMemory node, float aspect,
            Settings settings, float dt, bool restructured, bool positional)
        {
            List<Item> pair = new List<Item> { a, b };
            int axis = ChooseAxis(box, pair, node, 0, aspect, settings, restructured, false, positional);
            DecideSide(a, b, axis, node, settings, restructured, positional);
            node.axis = axis;
            // Item centres are aspect-corrected (y scaled by aspect); undo that to get
            // the pixel-space direction, then express the perpendicular line's normal
            // in normalized screen coordinates.
            Vector2 pixelDelta = new Vector2(b.center.x - a.center.x, (b.center.y - a.center.y) / aspect);
            Vector2 target;
            if (a.screenKey == b.screenKey && pixelDelta.sqrMagnitude > 1f)
            {
                Vector2 m = pixelDelta.normalized;
                target = new Vector2(m.x * aspect, m.y).normalized;
            }
            else
                target = axis == 0 ? new Vector2(node.reversed ? -1f : 1f, 0f) : new Vector2(0f, node.reversed ? -1f : 1f);
            float targetAngle = (float)Math.Atan2(target.y, target.x);
            if (!node.hasAngle || restructured)
            {
                node.angle = targetAngle;
                node.angleVelocity = 0f;
                node.hasAngle = true;
            }
            else
            {
                float remaining = WrapAngle(targetAngle - node.angle);
                float step = Mathf.SmoothDamp(0f, remaining, ref node.angleVelocity,
                    Mathf.Max(0.01f, settings.dividerTurnSeconds), Mathf.Infinity, dt);
                node.angle = WrapAngle(node.angle + step);
                if (Mathf.Abs(WrapAngle(targetAngle - node.angle)) < 0.01f)
                {
                    node.angle = targetAngle;
                    node.angleVelocity = 0f;
                }
            }
            Vector2 normal = new Vector2((float)Math.Cos(node.angle), (float)Math.Sin(node.angle));
            if (Mathf.Abs(normal.x) < 0.0001f) normal.x = 0f;
            if (Mathf.Abs(normal.y) < 0.0001f) normal.y = 0f;
            normal = normal.normalized;
            float targetFraction = a.weight / Mathf.Max(Epsilon, a.weight + b.weight);
            if (node.fraction < 0f || restructured && Mathf.Abs(node.fraction - targetFraction) > 0.45f)
            {
                node.fraction = targetFraction;
                node.fractionVelocity = 0f;
            }
            else
                node.fraction = Mathf.SmoothDamp(node.fraction, targetFraction, ref node.fractionVelocity,
                    Mathf.Max(0.01f, settings.smoothingTime), Mathf.Infinity, dt);
            float cut = CutForArea(normal, Mathf.Clamp01(node.fraction));
            a.shape = Clip(ScreenPolygon(), normal, cut);
            b.shape = Clip(ScreenPolygon(), -normal, -cut);
            a.box = Box.Of(a.shape);
            b.box = Box.Of(b.shape);
        }

        /// <summary>
        /// Which side each item takes follows the world, with a dead zone and a hold
        /// time so two players dancing around each other do not swap sides.
        /// </summary>
        private void DecideSide(Item a, Item b, int axis, NodeMemory node, Settings settings, bool restructured, bool positional)
        {
            float delta = Axis(b.center, axis) - Axis(a.center, axis);
            bool wantReversed = delta < 0f;
            bool before = node.reversed;
            if (restructured || node.axis != axis)
                node.reversed = wantReversed && Mathf.Abs(delta) > Epsilon;
            else if (positional && node.reversed != wantReversed && Mathf.Abs(delta) > settings.directionDeadZone &&
                clock - node.changedAt > settings.layoutHoldSeconds)
            {
                node.reversed = wantReversed;
                node.changedAt = clock;
            }
            if (node.reversed != before) structureChanged = true;
        }

        /// <summary>The cut c such that {p : n·p ≤ c} clipped to the screen has the given area.</summary>
        private static float CutForArea(Vector2 normal, float area)
        {
            float minimum = Mathf.Min(0f, Mathf.Min(normal.x, Mathf.Min(normal.y, normal.x + normal.y)));
            float maximum = Mathf.Max(0f, Mathf.Max(normal.x, Mathf.Max(normal.y, normal.x + normal.y)));
            float cut = (minimum + maximum) * 0.5f;
            for (int probe = 0; probe < 26; probe++)
            {
                cut = (minimum + maximum) * 0.5f;
                if (Area(Clip(ScreenPolygon(), normal, cut)) < area) minimum = cut;
                else maximum = cut;
            }
            return cut;
        }

        private static Layout Empty()
        {
            return new Layout { viewports = new ViewportState[0], dividers = new DividerSegment[0],
                pairSplitAmounts = new float[0, 0], effectiveInputs = new PlayerInput[0] };
        }

        private static Vector2 Corrected(Vector2 world, float aspect)
        {
            // Compare separations in screen heights: the screen is `aspect` times
            // wider than it is tall, so a vertical gap is worth more than the same
            // number of pixels horizontally.
            return new Vector2(world.x, world.y * aspect);
        }

        // ---- Layout tree -------------------------------------------------------

        private void Partition(Box box, List<Item> items, int depth, float aspect, Settings settings, float dt)
        {
            if (items.Count == 1)
            {
                items[0].box = box;
                return;
            }
            int mask = 0;
            for (int i = 0; i < items.Count; i++) mask |= items[i].mask;
            NodeMemory node;
            if (!nodes.TryGetValue(mask, out node))
            {
                node = new NodeMemory();
                nodes.Add(mask, node);
                structureChanged = true;
            }
            node.lastUsed = clock;
            bool restructured = node.itemCount != items.Count;
            if (restructured) structureChanged = true;
            node.itemCount = items.Count;
            bool positional = AllInOneRoom(items);

            if (items.Count == 2)
            {
                if (IsFullScreen(box)) SplitTwoRotating(box, items[0], items[1], node, aspect, settings, dt, restructured, positional);
                else SplitTwo(box, items[0], items[1], node, depth, aspect, settings, dt, restructured, positional);
                return;
            }
            if (items.Count == 3)
            {
                Item first = ChooseSplitOff(box, items, node, depth, aspect, settings, restructured, positional);
                List<Item> rest = new List<Item>(2);
                for (int i = 0; i < items.Count; i++) if (items[i] != first) rest.Add(items[i]);
                float restWeight = 0f, restStructural = 0f;
                for (int i = 0; i < rest.Count; i++)
                {
                    restWeight = Mathf.Max(restWeight, rest[i].weight);
                    restStructural = Mathf.Max(restStructural, rest[i].structuralWeight);
                }
                // The odd one out is weighed against the largest of the others, not
                // their sum: three players give one half and two quarters, a pair
                // sharing an image against a single player gives the pair two thirds.
                Item restItem = new Item { weight = restWeight, structuralWeight = restStructural };
                restItem.center = (rest[0].center + rest[1].center) * 0.5f;
                restItem.mask = rest[0].mask | rest[1].mask;
                restItem.roomKey = rest[0].roomKey;
                restItem.sameRoom = AllInOneRoom(rest);
                Box firstBox, restBox;
                if (node.reversed)
                    CutBox(box, restItem, first, node, depth, aspect, settings, dt, restructured, out restBox, out firstBox);
                else
                    CutBox(box, first, restItem, node, depth, aspect, settings, dt, restructured, out firstBox, out restBox);
                first.box = firstBox;
                Partition(restBox, rest, depth + 1, aspect, settings, dt);
                return;
            }
            // Four items: always two against two, so the result is a grid instead of
            // one column holding three stacked slivers.
            {
                int axis = ChooseAxis(box, items, node, depth, aspect, settings, restructured, true, positional);
                List<Item> sorted = new List<Item>(items);
                sorted.Sort((a, b) => Axis(a.center, axis).CompareTo(Axis(b.center, axis)));
                // The pairing follows positions only while everyone shares a room;
                // otherwise the previous pairing stands.
                int lowMask = sorted[0].mask | sorted[1].mask;
                if (!positional && node.lowMask != 0 && !restructured)
                {
                    bool valid = true;
                    int seen = 0;
                    for (int i = 0; i < items.Count; i++) if ((items[i].mask & node.lowMask) != 0) seen++;
                    valid = seen == 2;
                    if (valid)
                    {
                        lowMask = node.lowMask;
                        sorted.Sort((a, b) => ((b.mask & lowMask) != 0).CompareTo((a.mask & lowMask) != 0));
                    }
                }
                if (node.lowMask != lowMask) { if (node.lowMask != 0) structureChanged = true; node.lowMask = lowMask; }
                Item low = new Item { mask = sorted[0].mask | sorted[1].mask,
                    weight = sorted[0].weight + sorted[1].weight,
                    structuralWeight = sorted[0].structuralWeight + sorted[1].structuralWeight,
                    center = (sorted[0].center + sorted[1].center) * 0.5f,
                    roomKey = sorted[0].roomKey, sameRoom = AllInOneRoom(new List<Item> { sorted[0], sorted[1] }) };
                Item high = new Item { mask = sorted[2].mask | sorted[3].mask,
                    weight = sorted[2].weight + sorted[3].weight,
                    structuralWeight = sorted[2].structuralWeight + sorted[3].structuralWeight,
                    center = (sorted[2].center + sorted[3].center) * 0.5f,
                    roomKey = sorted[2].roomKey, sameRoom = AllInOneRoom(new List<Item> { sorted[2], sorted[3] }) };
                node.axis = axis;
                node.reversed = false;
                Box lowBox, highBox;
                CutBox(box, low, high, node, depth, aspect, settings, dt, restructured, out lowBox, out highBox);
                Partition(lowBox, new List<Item> { sorted[0], sorted[1] }, depth + 1, aspect, settings, dt);
                Partition(highBox, new List<Item> { sorted[2], sorted[3] }, depth + 1, aspect, settings, dt);
            }
        }

        private void SplitTwo(Box box, Item a, Item b, NodeMemory node, int depth, float aspect,
            Settings settings, float dt, bool restructured, bool positional)
        {
            List<Item> pair = new List<Item> { a, b };
            int axis = ChooseAxis(box, pair, node, depth, aspect, settings, restructured, false, positional);
            DecideSide(a, b, axis, node, settings, restructured, positional);
            node.axis = axis;
            Item low = node.reversed ? b : a;
            Item high = node.reversed ? a : b;
            Box lowBox, highBox;
            CutBox(box, low, high, node, depth, aspect, settings, dt, restructured, out lowBox, out highBox);
            low.box = lowBox;
            high.box = highBox;
        }

        /// <summary>
        /// Cut <paramref name="box"/> along the node's axis. The cut position follows
        /// the live weights so a dying player's cell shrinks smoothly, and it is damped
        /// so a group forming or dissolving slides instead of popping.
        /// </summary>
        private void CutBox(Box box, Item low, Item high, NodeMemory node, int depth, float aspect,
            Settings settings, float dt, bool restructured, out Box lowBox, out Box highBox)
        {
            float target = low.weight / Mathf.Max(Epsilon, low.weight + high.weight);
            if (node.fraction < 0f || restructured && Mathf.Abs(node.fraction - target) > 0.45f)
            {
                node.fraction = target;
                node.fractionVelocity = 0f;
            }
            else
                node.fraction = Mathf.SmoothDamp(node.fraction, target, ref node.fractionVelocity,
                    Mathf.Max(0.01f, settings.smoothingTime), Mathf.Infinity, dt);
            float fraction = Mathf.Clamp01(node.fraction);
            if (node.axis == 0)
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

        private Item ChooseSplitOff(Box box, List<Item> items, NodeMemory node, int depth, float aspect,
            Settings settings, bool restructured, bool positional)
        {
            // Candidates: along either axis, peel off the lowest or the highest item.
            // Score by the gap to its nearest neighbour, so the most isolated player
            // takes the big cell, and by how the resulting cells are shaped.
            float bestScore = float.NegativeInfinity, currentScore = float.NegativeInfinity;
            int bestAxis = 0; Item best = null; bool bestReversed = false;
            Item current = null;
            for (int axis = 0; axis < 2; axis++)
            {
                List<Item> sorted = new List<Item>(items);
                int a = axis;
                sorted.Sort((p, q) => Axis(p.center, a).CompareTo(Axis(q.center, a)));
                for (int side = 0; side < 2; side++)
                {
                    Item candidate = side == 0 ? sorted[0] : sorted[sorted.Count - 1];
                    Item neighbour = side == 0 ? sorted[1] : sorted[sorted.Count - 2];
                    float gap = Mathf.Abs(Axis(candidate.center, axis) - Axis(neighbour.center, axis));
                    float total = 0f;
                    for (int i = 0; i < items.Count; i++)
                        total += (items[i].center - candidate.center).magnitude;
                    float structuralRest = 0f;
                    for (int i = 0; i < items.Count; i++)
                        if (items[i] != candidate) structuralRest = Mathf.Max(structuralRest, items[i].structuralWeight);
                    float fraction = candidate.structuralWeight / Mathf.Max(Epsilon, candidate.structuralWeight + structuralRest);
                    Box cell = axis == 0 ? new Box(0f, 0f, box.w * fraction, box.h) : new Box(0f, 0f, box.w, box.h * fraction);
                    Box rest = axis == 0 ? new Box(0f, 0f, box.w * (1f - fraction), box.h) : new Box(0f, 0f, box.w, box.h * (1f - fraction));
                    float score = gap / Mathf.Max(1f, total) + AspectWeight * (ShapeScore(cell, aspect) + ShapeScore(rest, aspect)) * 0.5f
                        - SizePenalty(cell) - SizePenalty(rest);
                    if (candidate.mask == node.splitOffMask && node.axis == axis && node.reversed == (side == 1))
                    {
                        current = candidate;
                        currentScore = score;
                    }
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = candidate;
                        bestAxis = axis;
                        bestReversed = side == 1;
                    }
                }
            }
            bool keep = current != null && !restructured &&
                (!positional || bestScore - currentScore < SwitchMargin || clock - node.changedAt < settings.layoutHoldSeconds);
            // `current` above is only found while the stored split-off item is still
            // at the edge of the group along its axis. If it has moved into the
            // middle, hold the decision anyway during the hold time, and always
            // while the players are in different rooms; only positional play in one
            // room re-decides. Otherwise a player crossing the map forced a slide.
            if (current == null && !restructured && node.splitOffMask != 0 &&
                (!positional || clock - node.changedAt < settings.layoutHoldSeconds))
                for (int i = 0; i < items.Count; i++)
                    if (items[i].mask == node.splitOffMask) { current = items[i]; keep = true; break; }
            if (!keep)
            {
                if (node.splitOffMask != best.mask || node.axis != bestAxis || node.reversed != bestReversed)
                {
                    node.changedAt = clock;
                    structureChanged = true;
                }
                node.splitOffMask = best.mask;
                node.axis = bestAxis;
                node.reversed = bestReversed;
                return best;
            }
            return current;
        }

        private int ChooseAxis(Box box, List<Item> items, NodeMemory node, int depth, float aspect,
            Settings settings, bool restructured, bool pairs, bool positional)
        {
            float[] score = new float[2];
            for (int axis = 0; axis < 2; axis++)
            {
                List<Item> sorted = new List<Item>(items);
                int a = axis;
                sorted.Sort((p, q) => Axis(p.center, a).CompareTo(Axis(q.center, a)));
                int cut = pairs ? 2 : 1;
                float gap = Axis(sorted[cut].center, axis) - Axis(sorted[cut - 1].center, axis);
                float spread = Mathf.Max(1f,
                    Mathf.Abs(sorted[sorted.Count - 1].center.x - sorted[0].center.x) +
                    Mathf.Abs(sorted[sorted.Count - 1].center.y - sorted[0].center.y));
                float lowWeight = 0f, highWeight = 0f;
                for (int i = 0; i < sorted.Count; i++)
                    if (i < cut) lowWeight += sorted[i].structuralWeight; else highWeight += sorted[i].structuralWeight;
                float fraction = lowWeight / Mathf.Max(Epsilon, lowWeight + highWeight);
                Box lowCell = axis == 0 ? new Box(0f, 0f, box.w * fraction, box.h) : new Box(0f, 0f, box.w, box.h * fraction);
                Box highCell = axis == 0 ? new Box(0f, 0f, box.w * (1f - fraction), box.h) : new Box(0f, 0f, box.w, box.h * (1f - fraction));
                score[axis] = gap / spread + AspectWeight * (ShapeScore(lowCell, aspect) + ShapeScore(highCell, aspect)) * 0.5f
                    - SizePenalty(lowCell) - SizePenalty(highCell);
            }
            int wanted = score[1] > score[0] ? 1 : 0;
            if (restructured || node.axis < 0)
            {
                if (node.axis != wanted) structureChanged = true;
                return wanted;
            }
            if (!positional) return node.axis;
            if (node.axis != wanted && score[wanted] - score[node.axis] > SwitchMargin &&
                clock - node.changedAt > settings.layoutHoldSeconds)
            {
                node.changedAt = clock;
                structureChanged = true;
                return wanted;
            }
            return node.axis;
        }

        private static float ShapeScore(Box cell, float aspect)
        {
            // 1 for a square cell on screen, falling towards 0 for long strips.
            float w = Mathf.Max(Epsilon, cell.w * aspect), h = Mathf.Max(Epsilon, cell.h);
            return Mathf.Min(w, h) / Mathf.Max(w, h);
        }

        private static float SizePenalty(Box cell)
        {
            // Cells narrower or shorter than this show too little of a room to play
            // in. The penalty is soft because with four players someone has to lose.
            float penalty = 0f;
            if (cell.w < MinCellExtent) penalty += 1f + (MinCellExtent - cell.w) * 4f;
            if (cell.h < MinCellExtent) penalty += 1f + (MinCellExtent - cell.h) * 4f;
            return penalty;
        }

        private static float Axis(Vector2 point, int axis)
        {
            return axis == 0 ? point.x : point.y;
        }

        // ---- Helpers ------------------------------------------------------------

        public static bool SharedCameraCanShow(Vector2 screenPosition)
        {
            return Finite(screenPosition) && screenPosition.x >= 0.05f && screenPosition.x <= 0.95f &&
                screenPosition.y >= 0.05f && screenPosition.y <= 0.95f;
        }

        /// <summary>
        /// The uv translation that shows source point <paramref name="playerSource"/>
        /// at display point <paramref name="anchor"/>, clamped so the rectangle
        /// [<paramref name="min"/>, <paramref name="max"/>] never samples outside the
        /// source texture. For an axis-aligned cell this clamp is exact, and a cell
        /// of width w always keeps 1-w of pan, so the player is always displayable.
        /// </summary>
        /// <summary>
        /// The window a cell's transform must keep inside the source image, and the
        /// range its player's anchor may take. The window blends from the group's box
        /// to the cell's own box with the inner split; the anchor range is the window
        /// shrunk by <see cref="VisibleMargin"/> scaled by the split, so a player whose
        /// image has parted from the neighbours is always inside their own cell while
        /// cells still showing one seamless image are left exactly aligned.
        /// </summary>
        public static void PanBounds(Vector2 groupMin, Vector2 groupMax, Vector2 cellMin, Vector2 cellMax,
            float innerSplit, float split, out Vector2 windowMin, out Vector2 windowMax,
            out Vector2 anchorMin, out Vector2 anchorMax)
        {
            windowMin = Vector2.Lerp(groupMin, cellMin, innerSplit);
            windowMax = Vector2.Lerp(groupMax, cellMax, innerSplit);
            Vector2 margin = VisibleMargin * Mathf.Clamp01(split);
            Vector2 half = (windowMax - windowMin) * 0.5f;
            margin = new Vector2(Mathf.Min(margin.x, half.x), Mathf.Min(margin.y, half.y));
            anchorMin = windowMin + margin;
            anchorMax = windowMax - margin;
        }

        public static Vector2 ClampVector(Vector2 value, Vector2 min, Vector2 max)
        {
            return new Vector2(min.x <= max.x ? Mathf.Clamp(value.x, min.x, max.x) : (min.x + max.x) * 0.5f,
                min.y <= max.y ? Mathf.Clamp(value.y, min.y, max.y) : (min.y + max.y) * 0.5f);
        }

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

        private static long PairKey(int a, int b)
        {
            if (a > b) { int swap = a; a = b; b = swap; }
            return ((long)a << 32) | (uint)b;
        }

        private static bool Finite(Vector2 value)
        {
            return !float.IsNaN(value.x) && !float.IsNaN(value.y) && !float.IsInfinity(value.x) && !float.IsInfinity(value.y);
        }

        private static int Find(int[] parent, int index)
        {
            while (parent[index] != index) index = parent[index];
            return index;
        }

        private static void Union(int[] parent, int first, int second)
        {
            int a = Find(parent, first), b = Find(parent, second);
            if (a != b) parent[b] = a;
        }

        private static Vector2[] ScreenPolygon()
        {
            return new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up };
        }

        private static Vector2[] Clip(Vector2[] polygon, Vector2 normal, float cut)
        {
            if (polygon.Length == 0) return polygon;
            List<Vector2> result = new List<Vector2>(polygon.Length + 2);
            Vector2 previous = polygon[polygon.Length - 1];
            float previousSide = Vector2.Dot(previous, normal) - cut;
            for (int i = 0; i < polygon.Length; i++)
            {
                Vector2 current = polygon[i];
                float side = Vector2.Dot(current, normal) - cut;
                if ((side < -Epsilon && previousSide > Epsilon) || (side > Epsilon && previousSide < -Epsilon))
                    result.Add(Vector2.Lerp(previous, current, previousSide / (previousSide - side)));
                if (side <= Epsilon) result.Add(current);
                previous = current;
                previousSide = side;
            }
            return result.ToArray();
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
            float alpha, float width, List<DividerSegment> output)
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
                        start = start + axis * lo, end = start + axis * hi, alpha = alpha, width = width });
                }
            }
        }

        private static float Cross(Vector2 a, Vector2 b) { return a.x * b.y - a.y * b.x; }
    }
}
