using System;
using System.Collections.Generic;
using UnityEngine;

namespace SplitScreenCoop
{
    /// <summary>Game-independent screen geometry. Coordinates in the output are normalized bottom-left to top-right.</summary>
    public sealed class SplitLayoutSolver
    {
        public sealed class Settings
        {
            public float mergeDistance = 850f;
            public float blendWidth = 300f;
            public float minZoom = 0.5f;
            public float zoomExponent = 0.5f;
            public float smoothingTime = 0.18f;
            public float dividerWidth = 2f;
            public float screenAspect = 1.75f;
            public bool permanentSplit;
        }

        public struct PlayerInput
        {
            public int playerIndex;
            public Vector2 worldPos;
            public long sameScreenKey;
            public Vector2 mergedScreenPos;
            public bool validWorldPos;
        }

        public sealed class ViewportState
        {
            public int cameraNumber;
            public bool ghost;
            public bool rendering;
            public int sharesImageWith = -1;
            public Vector2[] polygon;
            public float areaFraction;
            public float groupAreaFraction;
            public Vector2 regionAnchor;
            public Vector2 groupSourceAnchor;
            public Vector2 groupTargetAnchor;
            public float zoom;
            public float splitAmount;
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
        }

        private sealed class Memory
        {
            public Vector2 site;
            public Vector2 siteVelocity;
            public Vector2 worldPos;
            public bool hasWorldPos;
            public float weight;
            public float weightVelocity;
            public float visibleFor;
            public PlayerInput lastInput;
            public float lastArea;
            public float deathStartArea;
            public float missingFor;
            public bool wasPresent;
        }

        private readonly Dictionary<int, Memory> memory = new Dictionary<int, Memory>();
        private readonly Dictionary<long, Vector2> lastPairNormals = new Dictionary<long, Vector2>();
        private readonly Dictionary<long, bool> pairJoined = new Dictionary<long, bool>();
        private const float Epsilon = 0.00001f;
        private const float DeathTransitionSeconds = 0.7f;

        public void Reset()
        {
            memory.Clear();
            lastPairNormals.Clear();
            pairJoined.Clear();
        }

        public Layout Solve(IList<PlayerInput> players, float dt, Settings settings)
        {
            if (players == null) throw new ArgumentNullException(nameof(players));
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (players.Count > 4) throw new ArgumentOutOfRangeException(nameof(players), "At most four cameras are supported.");
            dt = Mathf.Clamp(dt, 0.001f, 0.1f);
            int aliveCount = players.Count;
            if (aliveCount == 0)
            {
                foreach (Memory state in memory.Values) state.wasPresent = false;
                return new Layout { viewports = new ViewportState[0], dividers = new DividerSegment[0],
                    pairSplitAmounts = new float[0, 0], effectiveInputs = new PlayerInput[0] };
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
            if (count == 0) return new Layout { viewports = new ViewportState[0], dividers = new DividerSegment[0],
                pairSplitAmounts = new float[0, 0], effectiveInputs = new PlayerInput[0] };
            bool[] ghosts = new bool[count];
            for (int i = 0; i < count; i++) ghosts[i] = !aliveNumbers[players[i].playerIndex];
            float aspect = Mathf.Max(0.1f, settings.screenAspect);

            Vector2[] world = new Vector2[count];
            Vector2[] sites = new Vector2[count];
            float[] weights = new float[count];
            bool[] seen = new bool[count];
            for (int i = 0; i < count; i++)
            {
                PlayerInput input = players[i];
                if (input.playerIndex < 0 || input.playerIndex > 3 || seen[input.playerIndex])
                    throw new ArgumentException("Camera numbers must be unique and in 0..3.", nameof(players));
                seen[input.playerIndex] = true;
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
            }

            Vector2 center = Vector2.zero;
            for (int i = 0; i < count; i++) center += world[i];
            center /= count;
            float reach = 1f;
            for (int i = 0; i < count; i++) reach = Mathf.Max(reach, (world[i] - center).magnitude);
            float worldScale = Mathf.Max(2f * Mathf.Max(1f, settings.mergeDistance + settings.blendWidth), 2f * reach);
            for (int i = 0; i < count; i++)
            {
                Memory state = memory[players[i].playerIndex];
                Vector2 target = new Vector2(0.5f + (world[i].x - center.x) / worldScale,
                    0.5f + (world[i].y - center.y) * aspect / worldScale);
                target.x = Mathf.Clamp(target.x, 0.05f, 0.95f);
                target.y = Mathf.Clamp(target.y, 0.05f, 0.95f);
                if (state.visibleFor <= 0f) state.site = target;
                else state.site = Damp(state.site, target, ref state.siteVelocity, settings.smoothingTime, dt);
                state.visibleFor += dt;
                sites[i] = state.site;
                weights[i] = state.weight;
            }

            float[,] amounts = new float[count, count];
            bool[,] joined = new bool[count, count];
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
            for (int i = 0; i < count; i++)
            for (int j = i + 1; j < count; j++)
            {
                bool sameScreen = players[i].sameScreenKey == players[j].sameScreenKey;
                Vector2 delta = world[j] - world[i];
                float distance = new Vector2(delta.x, delta.y * aspect).magnitude;
                float split = ghosts[i] || ghosts[j] || settings.permanentSplit || !sameScreen ? 1f :
                    Smoothstep(settings.mergeDistance, settings.mergeDistance + Mathf.Max(1f, settings.blendWidth), distance);
                amounts[i, j] = amounts[j, i] = split;
                long key = PairKey(players[i].playerIndex, players[j].playerIndex);
                bool wasJoined;
                pairJoined.TryGetValue(key, out wasJoined);
                bool nowJoined = !ghosts[i] && !ghosts[j] && !settings.permanentSplit && sameScreen &&
                    (wasJoined ? split < 0.06f : split < 0.02f);
                pairJoined[key] = nowJoined;
                joined[i, j] = joined[j, i] = nowJoined;
                if (delta.sqrMagnitude > 1f && split > 0.02f)
                    lastPairNormals[key] = new Vector2(delta.x, delta.y * aspect).normalized;
            }

            // Coincident sites need a persistent, tiny separation so every power cell remains defined.
            for (int i = 0; i < count; i++)
            for (int j = i + 1; j < count; j++)
            {
                if ((sites[j] - sites[i]).sqrMagnitude >= Epsilon * Epsilon) continue;
                Vector2 normal;
                if (!lastPairNormals.TryGetValue(PairKey(players[i].playerIndex, players[j].playerIndex), out normal))
                    normal = new Vector2(1f, 0f);
                sites[i] -= normal * 0.0005f;
                sites[j] += normal * 0.0005f;
            }

            Vector2[][] cells;
            if (count == 2)
            {
                Vector2 normal = sites[1] - sites[0];
                if (normal.sqrMagnitude < Epsilon)
                {
                    if (!lastPairNormals.TryGetValue(PairKey(players[0].playerIndex, players[1].playerIndex), out normal))
                        normal = new Vector2(1f, 0f);
                }
                normal.Normalize();
                float minimum = Mathf.Min(0f, Mathf.Min(normal.x, Mathf.Min(normal.y, normal.x + normal.y)));
                float maximum = Mathf.Max(0f, Mathf.Max(normal.x, Mathf.Max(normal.y, normal.x + normal.y)));
                float cut = normal.x * 0.5f + normal.y * 0.5f;
                for (int probe = 0; probe < 24; probe++)
                {
                    cut = (minimum + maximum) * 0.5f;
                    if (Area(Clip(ScreenPolygon(), normal, cut)) < targetAreas[0]) minimum = cut;
                    else maximum = cut;
                }
                cells = new[] { Clip(ScreenPolygon(), normal, cut), Clip(ScreenPolygon(), -normal, -cut) };
            }
            else
            {
                float[] solved = (float[])weights.Clone();
                // Each cell's area grows monotonically with its power weight.
                // Coordinate bisection is stable even for almost coincident sites,
                // where a fixed gradient step can erase a cell in one iteration.
                cells = PowerCells(sites, solved);
                float maximumError = 0f;
                for (int i = 0; i < count; i++)
                    maximumError = Mathf.Max(maximumError, Mathf.Abs(Area(cells[i]) - targetAreas[i]));
                int rounds = maximumError < 0.003f ? 0 : maximumError < 0.03f ? 2 : 5;
                for (int step = 0; step < rounds; step++)
                {
                    for (int i = 0; i < count; i++)
                    {
                        float lower = solved[i] - 2f, upper = solved[i] + 2f;
                        for (int probe = 0; probe < 26; probe++)
                        {
                            float middle = (lower + upper) * 0.5f;
                            solved[i] = middle;
                            if (Area(PowerCell(sites, solved, i)) < targetAreas[i]) lower = middle;
                            else upper = middle;
                        }
                        solved[i] = (lower + upper) * 0.5f;
                    }
                    float mean = 0f;
                    for (int i = 0; i < count; i++) mean += solved[i];
                    mean /= count;
                    for (int i = 0; i < count; i++) solved[i] -= mean;
                }
                for (int i = 0; i < count; i++)
                {
                    Memory state = memory[players[i].playerIndex];
                    state.weight = Damp(state.weight, solved[i], ref state.weightVelocity, settings.smoothingTime, dt);
                    weights[i] = state.weight;
                }
                cells = PowerCells(sites, weights);
            }

            int[] leaders = new int[count];
            for (int i = 0; i < count; i++) leaders[i] = i;
            for (int i = 0; i < count; i++)
            for (int j = i + 1; j < count; j++)
                if (joined[i, j]) Union(leaders, i, j);
            ViewportState[] viewports = new ViewportState[count];
            for (int i = 0; i < count; i++)
            {
                int leaderIndex = Find(leaders, i);
                for (int j = 0; j < count; j++)
                    if (Find(leaders, j) == leaderIndex && players[j].playerIndex < players[leaderIndex].playerIndex)
                        leaderIndex = j;
                float split = 0f;
                for (int j = 0; j < count; j++) if (j != i) split = Mathf.Max(split, amounts[i, j]);
                float imageBlend = 0f;
                bool hasSameScreenPeer = false;
                for (int j = 0; j < count; j++)
                    if (j != i && players[j].sameScreenKey == players[i].sameScreenKey)
                    {
                        hasSameScreenPeer = true;
                        imageBlend = Mathf.Max(imageBlend, amounts[i, j]);
                    }
                if (!hasSameScreenPeer && count > 1) imageBlend = 1f;
                float area = Area(cells[i]);
                memory[players[i].playerIndex].lastArea = area;
                float groupArea = 0f;
                Vector2 weightedCentroid = Vector2.zero;
                Vector2 sourceSum = Vector2.zero;
                int groupMembers = 0;
                for (int j = 0; j < count; j++)
                    if (Find(leaders, j) == Find(leaders, i))
                    {
                        float memberArea = Area(cells[j]);
                        groupArea += memberArea;
                        weightedCentroid += Centroid(cells[j]) * memberArea;
                        sourceSum += Finite(players[j].mergedScreenPos) ? players[j].mergedScreenPos : new Vector2(0.5f, 0.5f);
                        groupMembers++;
                    }
                Vector2 centroid = Centroid(cells[i]);
                Vector2 sourceAnchor = sourceSum / Mathf.Max(1, groupMembers);
                Vector2 targetAnchor = weightedCentroid / Mathf.Max(Epsilon, groupArea);
                Vector2 transformAnchor = Vector2.Lerp(sourceAnchor, targetAnchor, split);
                float zoomTarget = Mathf.Clamp(Mathf.Pow(Mathf.Max(Epsilon, groupArea), Mathf.Max(0f, settings.zoomExponent)),
                    Mathf.Clamp(settings.minZoom, 0.1f, 1f), 1f);
                Vector2 min = new Vector2(1f, 1f), max = Vector2.zero;
                for (int j = 0; j < count; j++)
                    if (Find(leaders, j) == Find(leaders, i))
                        for (int vertex = 0; vertex < cells[j].Length; vertex++)
                        {
                            Vector2 point = cells[j][vertex];
                            min.x = Mathf.Min(min.x, point.x); min.y = Mathf.Min(min.y, point.y);
                            max.x = Mathf.Max(max.x, point.x); max.y = Mathf.Max(max.y, point.y);
                        }
                // A full-height (or full-width) region cannot zoom out using only
                // one full render-texture screen; the sample would leave the source.
                zoomTarget = Mathf.Max(zoomTarget, Mathf.Max(max.x - min.x, max.y - min.y));
                float zoom = Mathf.Lerp(1f, zoomTarget, split);
                Vector2 playerSource = Finite(players[i].mergedScreenPos) ? players[i].mergedScreenPos : new Vector2(0.5f, 0.5f);
                Vector2 anchor = transformAnchor + (playerSource - sourceAnchor) * zoom;
                Memory state = memory[players[i].playerIndex];
                bool mergedSource = leaderIndex != i;
                viewports[i] = new ViewportState
                {
                    cameraNumber = players[i].playerIndex,
                    ghost = ghosts[i],
                    // A dead player's camera no longer has a reliable live image.
                    // Retain its cell briefly for area reflow, never its stale RT.
                    rendering = !ghosts[i] && (!mergedSource || imageBlend > 0.01f || state.visibleFor < 0.25f),
                    sharesImageWith = ghosts[i] ? -1 : mergedSource ? players[leaderIndex].playerIndex : -1,
                    polygon = cells[i],
                    areaFraction = area,
                    groupAreaFraction = groupArea,
                    centroid = centroid,
                    site = sites[i],
                    regionAnchor = anchor,
                    groupSourceAnchor = sourceAnchor,
                    groupTargetAnchor = targetAnchor,
                    zoom = zoom,
                    splitAmount = split,
                    imageBlend = ghosts[i] ? 1f : imageBlend
                };
            }

            List<DividerSegment> dividers = new List<DividerSegment>(6);
            for (int i = 0; i < count; i++)
            for (int j = i + 1; j < count; j++)
            {
                if (amounts[i, j] <= 0f) continue;
                AddSharedEdges(cells[i], cells[j], players[i].playerIndex, players[j].playerIndex,
                    amounts[i, j], settings.dividerWidth, dividers);
            }
            return new Layout { viewports = viewports, dividers = dividers.ToArray(),
                pairSplitAmounts = amounts, effectiveInputs = effective.ToArray() };
        }

        private static Vector2 Damp(Vector2 from, Vector2 to, ref Vector2 velocity, float time, float dt)
        {
            float vx = velocity.x, vy = velocity.y;
            Vector2 result = new Vector2(
                Mathf.SmoothDamp(from.x, to.x, ref vx, Mathf.Max(0.01f, time), Mathf.Infinity, dt),
                Mathf.SmoothDamp(from.y, to.y, ref vy, Mathf.Max(0.01f, time), Mathf.Infinity, dt));
            velocity = new Vector2(vx, vy);
            return result;
        }

        // Native follow is the t=0 endpoint. Once fully split, the player's
        // source-screen anchor is geometry-only, so a stale camera position
        // cannot become the next frame's follow target.
        public static Vector2 FollowSourcePosition(Vector2 nativeSource, Vector2 targetAnchor,
            Vector2 boundsCenter, float zoom, float splitAmount)
        {
            zoom = Mathf.Max(0.1f, zoom);
            Vector2 centered = new Vector2(0.5f + (targetAnchor.x - boundsCenter.x) / zoom,
                0.5f + (targetAnchor.y - boundsCenter.y) / zoom);
            Vector2 result = Vector2.Lerp(nativeSource, centered, Mathf.Clamp01(splitAmount));
            return new Vector2(Mathf.Clamp01(result.x), Mathf.Clamp01(result.y));
        }

        public static bool SharedCameraCanShow(Vector2 screenPosition)
        {
            return Finite(screenPosition) && screenPosition.x >= 0.05f && screenPosition.x <= 0.95f &&
                screenPosition.y >= 0.05f && screenPosition.y <= 0.95f;
        }

        private static float Damp(float from, float to, ref float velocity, float time, float dt)
        {
            return Mathf.SmoothDamp(from, to, ref velocity, Mathf.Max(0.01f, time), Mathf.Infinity, dt);
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

        private Vector2[][] PowerCells(Vector2[] sites, float[] weights)
        {
            Vector2[][] cells = new Vector2[sites.Length][];
            for (int i = 0; i < sites.Length; i++)
                cells[i] = PowerCell(sites, weights, i);
            return cells;
        }

        private static Vector2[] PowerCell(Vector2[] sites, float[] weights, int i)
        {
            Vector2[] polygon = ScreenPolygon();
            for (int j = 0; j < sites.Length; j++)
            {
                if (i == j) continue;
                Vector2 normal = 2f * (sites[j] - sites[i]);
                float cut = sites[j].sqrMagnitude - sites[i].sqrMagnitude + weights[i] - weights[j];
                polygon = Clip(polygon, normal, cut);
                if (polygon.Length == 0) break;
            }
            return polygon;
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
                else if (Mathf.Abs(previousSide) <= Epsilon && Mathf.Abs(side) > Epsilon && side > 0f)
                    result.Add(previous);
                if (side <= Epsilon) result.Add(current);
                previous = current;
                previousSide = side;
            }
            return result.ToArray();
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
