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
    /// disappears.
    ///
    /// Where the cells sit depends only on which players are grouped, never on
    /// where they stand: the lower camera number is left (or top), a merged pair
    /// beside a third player takes the top half, four cells form a grid. So the
    /// layout only changes when players merge, part, die or arrive, and each such
    /// change slides the cells from their old rectangles to their new ones. Players
    /// walking around inside their cells only pan the image.
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
            /// <summary>Seconds a slide transition takes.</summary>
            public float transitionSeconds = 0.45f;
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

        /// <summary>Per layout-node damped state. A node is the set of cameras it partitions.</summary>
        private sealed class NodeMemory
        {
            /// <summary>The item masks the node last partitioned, in slot order; a change restructures.</summary>
            public long layoutKey = -1;
            public float lastUsed;
            public float fraction = -1f;
            public float fractionVelocity;
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
            /// <summary>Number of members; decides which item takes the big cell of three.</summary>
            public float structuralWeight;
            public Box box;
            public long screenKey;
            /// <summary>Slot order: the lowest camera number in the item.</summary>
            public int Order
            {
                get
                {
                    for (int bit = 0; bit < 4; bit++) if ((mask & (1 << bit)) != 0) return bit;
                    return 4;
                }
            }
            public Vector2[] Polygon() { return box.Polygon(); }
        }

        private sealed class ItemOrder : IComparer<Item>
        {
            public static readonly ItemOrder Instance = new ItemOrder();
            public int Compare(Item a, Item b) { return a.Order.CompareTo(b.Order); }
        }

        private enum TransitionMode { None, Slide }

        // Solve runs every game tick. What never leaves it is kept here (at most four
        // players). Everything in the Layout it returns is new on every call: callers
        // and the slide memory keep earlier layouts' arrays.
        private readonly bool[] scratchAlive = new bool[4];
        private readonly List<PlayerInput> scratchEffective = new List<PlayerInput>(4);
        private readonly bool[] scratchGhosts = new bool[4];
        private readonly Vector2[] scratchWorld = new Vector2[4];
        private readonly float[] scratchTargetAreas = new float[4];
        private readonly float[,] scratchLineAmounts = new float[4, 4];
        private readonly bool[,] scratchJoined = new bool[4, 4];
        private readonly int[] scratchLeaders = new int[4];
        private readonly Item[] scratchItemOfPlayer = new Item[4];
        private readonly Vector2[][] scratchCells = new Vector2[4][];
        private readonly Vector2[][] scratchShown = new Vector2[4][];
        private readonly List<Item> scratchItems = new List<Item>(4);
        private readonly List<Item> scratchSlices = new List<Item>(4);
        private readonly List<Item> partitionRest = new List<Item>(2);
        private readonly List<Item> partitionTop = new List<Item>(2);
        private readonly List<Item> partitionBottom = new List<Item>(2);
        private readonly List<DividerSegment> scratchDividers = new List<DividerSegment>(6);
        private readonly List<Item> itemPool = new List<Item>(8);
        private int itemsRented;

        /// <summary>A cleared item from the pool; the pool is handed out afresh by every Solve.</summary>
        private Item RentItem()
        {
            Item item;
            if (itemsRented < itemPool.Count) item = itemPool[itemsRented];
            else
            {
                item = new Item();
                itemPool.Add(item);
            }
            itemsRented++;
            item.members.Clear();
            item.mask = 0;
            item.weight = 0f;
            item.structuralWeight = 0f;
            item.box = default(Box);
            item.screenKey = 0;
            return item;
        }

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
            itemsRented = 0;
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

            Vector2[] world = scratchWorld;
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

            float[] targetAreas = scratchTargetAreas;
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

            // Only amounts leaves (pairSplitAmounts); the other two are read for i != j,
            // every one of which is written below.
            float[,] amounts = new float[count, count];
            float[,] lineAmounts = scratchLineAmounts;
            bool[,] joined = scratchJoined;
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

            int[] leaders = scratchLeaders;
            for (int i = 0; i < count; i++) leaders[i] = i;
            for (int i = 0; i < count; i++)
            for (int j = i + 1; j < count; j++)
                if (joined[i, j]) Union(leaders, i, j);

            // One layout item per image group. Ghosts are never joined, so they are
            // always their own item and fade out through their weight.
            List<Item> items = scratchItems;
            items.Clear();
            Item[] itemOfPlayer = scratchItemOfPlayer;
            for (int i = 0; i < count; i++)
            {
                int leader = Find(leaders, i);
                Item item = null;
                for (int k = 0; k < items.Count; k++)
                    if (Find(leaders, items[k].members[0]) == leader) { item = items[k]; break; }
                if (item == null)
                {
                    item = RentItem();
                    items.Add(item);
                }
                item.members.Add(i);
                item.mask |= 1 << players[i].playerIndex;
                item.weight += targetAreas[i] * Mathf.Max(1, aliveCount);
                item.structuralWeight += 1f;
                item.screenKey = players[i].sameScreenKey;
                itemOfPlayer[i] = item;
            }

            Partition(new Box(0f, 0f, 1f, 1f), items, aspect, settings, dt);

            // Members of one group tile the group's rectangle. They all draw the same
            // image, so only the HUD and the divider bookkeeping care about the slices.
            Vector2[][] cells = scratchCells;
            foreach (Item item in items)
            {
                if (item.members.Count == 1)
                {
                    cells[item.members[0]] = item.Polygon();
                    continue;
                }
                List<Item> slices = scratchSlices;
                slices.Clear();
                for (int m = 0; m < item.members.Count; m++)
                {
                    int p = item.members[m];
                    Item slice = RentItem();
                    slice.members.Add(p);
                    slice.mask = 1 << players[p].playerIndex;
                    slice.weight = targetAreas[p] * Mathf.Max(1, aliveCount);
                    slice.structuralWeight = 1f;
                    slice.screenKey = players[p].sameScreenKey;
                    slices.Add(slice);
                }
                Partition(item.box, slices, aspect, settings, dt);
                for (int m = 0; m < slices.Count; m++) cells[slices[m].members[0]] = slices[m].Polygon();
            }

            // ---- Transitions ---------------------------------------------------
            // Two cells never need one: a join or part of two players leaves the
            // two rectangles where they are (only the pans change). Three or more
            // cells slide from their previous rectangles to their new ones when the
            // tree restructures.
            Vector2[][] shown = scratchShown;
            for (int i = 0; i < count; i++) shown[i] = cells[i];
            // A slide starts only when the tree changed shape: an item joined or
            // parted, or a player came or went. Cells that merely move because a
            // damped cut or a dying player's weight is still settling keep moving
            // smoothly; treating that motion as a restructure restarted the slide
            // every tick and left the layout crawling.
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
                // A sliding cell can widen that box for a moment; the zoom follows.
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

            List<DividerSegment> dividers = scratchDividers;
            dividers.Clear();
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

        private static Layout Empty()
        {
            return new Layout { viewports = new ViewportState[0], dividers = new DividerSegment[0],
                pairSplitAmounts = new float[0, 0], effectiveInputs = new PlayerInput[0] };
        }

        // ---- Layout tree -------------------------------------------------------

        /// <summary>
        /// Fixed slots. Items are ordered by their lowest camera number and placed:
        /// two side by side (or stacked when the box is taller than wide), first item
        /// left or top; three as one half on top of two quarters, the half going to
        /// the item with the most members (a merged pair), otherwise the first; four
        /// as a 2x2 grid. World positions play no part, so the only way the tree
        /// changes shape is an item joining, parting, arriving or leaving, which is
        /// what the players read as "the screen split" or "the screens merged".
        /// Cut positions still follow the live weights (damped), so a dying
        /// player's cell slides shut and a pair beside a third player owns two thirds.
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
                mask |= items[i].mask;
                key = key * 16 + items[i].mask;
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
                int first = 0;
                for (int i = 1; i < items.Count; i++)
                    if (items[i].structuralWeight > items[first].structuralWeight) first = i;
                // The scratch pairs are safe to reuse: a call is only ever given two,
                // three or four items and only three and four recurse, always with two.
                List<Item> rest = partitionRest;
                rest.Clear();
                float restWeight = 0f;
                for (int i = 0; i < items.Count; i++)
                {
                    if (i == first) continue;
                    rest.Add(items[i]);
                    restWeight = Mathf.Max(restWeight, items[i].weight);
                }
                // The top item is weighed against the largest of the others, not
                // their sum: three players give one half and two quarters, a pair
                // sharing an image above two singles gives the pair two thirds.
                Box restBox, firstBox;
                CutBox(box, 1, restWeight, items[first].weight, node, settings, dt, changed, out restBox, out firstBox);
                items[first].box = firstBox;
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
        /// cut follows the live weights, damped, so a dying player's cell shrinks
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
