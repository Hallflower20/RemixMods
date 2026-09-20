using System;
using SplitScreenCoop;
using UnityEngine;

internal static class Program
{
    private static int checks;
    private static readonly SplitLayoutSolver.Settings Settings = new SplitLayoutSolver.Settings();

    private static SplitLayoutSolver.PlayerInput P(int camera, float x, float y, long screen = 1, long room = 1)
    {
        return new SplitLayoutSolver.PlayerInput { playerIndex = camera, worldPos = new Vector2(x, y),
            sameScreenKey = screen, roomKey = room, mergedScreenPos = new Vector2(0.5f, 0.5f), validWorldPos = true };
    }

    private static void DividerFadesWithDistance()
    {
        // Same room, different screens: the line fades with distance through the
        // merge band even though the cells cannot share an image yet. Different
        // rooms: solid regardless of distance.
        var far = Settle(new SplitLayoutSolver(), P(0, -1000f, 0f, 1, 7), P(1, 1000f, 0f, 2, 7));
        Check(far.dividers.Length == 1 && far.dividers[0].alpha == 1f, "Far apart in one room should draw a solid line");
        float half = (Settings.mergeDistance + Settings.blendWidth * 0.4f) / 2f;
        var mid = Settle(new SplitLayoutSolver(), P(0, -half, 0f, 1, 7), P(1, half, 0f, 2, 7));
        Check(mid.dividers.Length == 1 && mid.dividers[0].alpha > 0.05f && mid.dividers[0].alpha < 0.95f,
            "Inside the merge band the line should be partly faded: " + mid.dividers[0].alpha);
        var near = Settle(new SplitLayoutSolver(), P(0, -250f, 0f, 1, 7), P(1, 250f, 0f, 2, 7));
        Check(near.dividers.Length == 1 && near.dividers[0].alpha < 0.01f,
            "Close together in one room the line should be gone even on different screens: " + near.dividers[0].alpha);
        Check(near.viewports[0].splitAmount == 1f, "Different screens must still keep separate images");
        var rooms = Settle(new SplitLayoutSolver(), P(0, -300f, 0f, 1, 7), P(1, 300f, 0f, 2, 8));
        Check(rooms.dividers.Length == 1 && rooms.dividers[0].alpha == 1f, "Different rooms should draw a solid line");

        // The fade is gradual in time as well as in distance.
        var solver = new SplitLayoutSolver();
        var previous = Settle(solver, P(0, -1000f, 0f, 1, 7), P(1, 1000f, 0f, 2, 7));
        float largestStep = 0f;
        for (int frame = 0; frame < 60; frame++)
        {
            var next = solver.Solve(new[] { P(0, -300f, 0f, 1, 7), P(1, 300f, 0f, 2, 7) }, 1f / 60f, Settings);
            largestStep = Math.Max(largestStep, Math.Abs(next.dividers[0].alpha - previous.dividers[0].alpha));
            previous = next;
        }
        Check(largestStep < 0.12f, "Divider opacity jumped: " + largestStep);
        Check(previous.dividers[0].alpha < 0.05f, "Divider did not fade out after approaching");
    }

    private static void Check(bool condition, string message)
    {
        checks++;
        if (!condition) throw new Exception(message);
    }

    private static SplitLayoutSolver.Layout Settle(SplitLayoutSolver solver, params SplitLayoutSolver.PlayerInput[] input)
    {
        SplitLayoutSolver.Layout layout = null;
        for (int i = 0; i < 180; i++) layout = solver.Solve(input, 1f / 60f, Settings);
        return layout;
    }

    private static void Bounds(Vector2[] polygon, out Vector2 min, out Vector2 max)
    {
        min = new Vector2(1f, 1f); max = Vector2.zero;
        foreach (Vector2 p in polygon)
        {
            min.x = Math.Min(min.x, p.x); min.y = Math.Min(min.y, p.y);
            max.x = Math.Max(max.x, p.x); max.y = Math.Max(max.y, p.y);
        }
    }

    private static bool IsRectangle(Vector2[] polygon)
    {
        if (polygon.Length != 4) return false;
        Vector2 min, max;
        Bounds(polygon, out min, out max);
        foreach (Vector2 p in polygon)
            if ((Math.Abs(p.x - min.x) > 1e-5f && Math.Abs(p.x - max.x) > 1e-5f) ||
                (Math.Abs(p.y - min.y) > 1e-5f && Math.Abs(p.y - max.y) > 1e-5f)) return false;
        return true;
    }

    /// <summary>Every layout must be a set of axis-aligned rectangles tiling the screen.</summary>
    private static void Tiling(SplitLayoutSolver.Layout layout, string name)
    {
        // While cells slide, only the resting rectangles are a tiling; the drawn
        // ones may overlap or leave gaps, which the compositor covers with the
        // resting layout underneath.
        float total = 0f;
        foreach (var view in layout.viewports)
        {
            Vector2[] polygon = layout.sliding ? view.targetPolygon : view.polygon;
            Check(IsRectangle(polygon), name + ": cell " + view.cameraNumber + " is not an axis-aligned rectangle");
            Vector2 min, max;
            Bounds(polygon, out min, out max);
            Check(min.x >= -1e-4f && min.y >= -1e-4f && max.x <= 1f + 1e-4f && max.y <= 1f + 1e-4f,
                name + ": cell " + view.cameraNumber + " leaves the screen");
            total += layout.sliding ? Area(polygon) : view.areaFraction;
        }
        Check(Math.Abs(total - 1f) < 0.002f, name + ": cells do not tile the screen, total area " + total);
    }

    /// <summary>
    /// For any source position, the clamped pan must place the player inside the
    /// cell it owns. This is the property the old diagonal cells could not offer.
    /// </summary>
    private static void PanBudget(SplitLayoutSolver.Layout layout, string name)
    {
        foreach (var view in layout.viewports)
        {
            if (view.ghost) continue;
            Vector2 min, max;
            Bounds(view.polygon, out min, out max);
            Check(view.zoom >= Math.Max(max.x - min.x, max.y - min.y) - 1e-4f && view.zoom <= 1f,
                name + ": zoom " + view.zoom + " cannot fit cell " + view.cameraNumber + " inside one source screen");
            for (int sx = 0; sx <= 10; sx++)
            for (int sy = 0; sy <= 10; sy++)
            {
                Vector2 source = new Vector2(sx / 10f, sy / 10f);
                Vector2 shift = SplitLayoutSolver.ClampedUvShift(source, view.centroid, min, max, view.zoom);
                Vector2 displayed = (source - shift) * view.zoom;
                Check(displayed.x >= min.x - 1e-3f && displayed.x <= max.x + 1e-3f &&
                    displayed.y >= min.y - 1e-3f && displayed.y <= max.y + 1e-3f,
                    name + ": source " + source + " cannot be shown inside cell " + view.cameraNumber);
                Vector2 uvMin = min / view.zoom + shift, uvMax = max / view.zoom + shift;
                Check(uvMin.x >= -1e-3f && uvMin.y >= -1e-3f && uvMax.x <= 1f + 1e-3f && uvMax.y <= 1f + 1e-3f,
                    name + ": cell " + view.cameraNumber + " samples outside its source texture");
            }
        }
    }

    private static Vector2 CellMin(SplitLayoutSolver.ViewportState view)
    {
        Vector2 min, max;
        Bounds(view.polygon, out min, out max);
        return min;
    }

    private static Vector2 CellMax(SplitLayoutSolver.ViewportState view)
    {
        Vector2 min, max;
        Bounds(view.polygon, out min, out max);
        return max;
    }

    private static bool SameRect(Vector2[] a, Vector2[] b)
    {
        Vector2 aMin, aMax, bMin, bMax;
        Bounds(a, out aMin, out aMax);
        Bounds(b, out bMin, out bMax);
        return (aMin - bMin).magnitude < 1e-4f && (aMax - bMax).magnitude < 1e-4f;
    }

    private static void TwoPlayerSlotsAreFixed()
    {
        // Whatever the world direction, camera 0 is the left column and camera 1
        // the right column, each half the screen.
        var directions = new[] { new Vector2(800f, 0f), new Vector2(-800f, 0f), new Vector2(0f, 800f),
            new Vector2(0f, -800f), new Vector2(600f, 450f), new Vector2(-600f, -450f) };
        foreach (Vector2 d in directions)
        {
            var layout = Settle(new SplitLayoutSolver(), P(0, -d.x, -d.y, 1), P(1, d.x, d.y, 2));
            Tiling(layout, "two-fixed");
            PanBudget(layout, "two-fixed");
            Check(CellMax(layout.viewports[0]).x < 0.5f + 1e-4f && CellMin(layout.viewports[1]).x > 0.5f - 1e-4f,
                "Camera 0 must be the left column and camera 1 the right for direction " + d);
            Check(CellMax(layout.viewports[0]).y > 0.99f && CellMin(layout.viewports[0]).y < 0.01f,
                "Two-player cells must be full-height columns for direction " + d);
            Check(Math.Abs(layout.viewports[0].areaFraction - 0.5f) < 0.001f, "Two-player area not half");
            Check(layout.dividers.Length == 1 && layout.dividers[0].alpha == 1f, "Two-player divider missing");
        }
        // Camera order, not input order, decides the slot.
        var swapped = Settle(new SplitLayoutSolver(), P(1, -800f, 0f, 2), P(0, 800f, 0f, 1));
        Check(swapped.viewports[0].cameraNumber == 1 && CellMin(swapped.viewports[0]).x > 0.5f - 1e-4f,
            "Camera 1 given first must still take the right column");
    }

    private static void PositionsNeverRestructure()
    {
        // Players crossing sides, circling and jittering move nothing: no slide,
        // no restructure, and every cell keeps its rectangle exactly.
        var two = new SplitLayoutSolver();
        var before = Settle(two, P(0, -600f, 0f, 1), P(1, 600f, 0f, 2));
        for (int frame = 0; frame < 300; frame++)
        {
            double angle = frame / 300.0 * Math.PI * 2.0;
            float wobble = (frame % 2 == 0 ? 1f : -1f) * 120f;
            var next = two.Solve(new[] { P(0, (float)(700 * Math.Cos(angle)) + wobble, (float)(500 * Math.Sin(angle)), 1),
                P(1, (float)(-700 * Math.Cos(angle)), (float)(-500 * Math.Sin(angle)) - wobble, 2) }, 1f / 60f, Settings);
            Check(!next.restructured && !next.sliding, "Two players moving restructured the layout at frame " + frame);
            for (int i = 0; i < 2; i++)
                Check(SameRect(next.viewports[i].polygon, before.viewports[i].polygon),
                    "Two-player cell " + i + " moved with the players at frame " + frame);
        }

        var three = new SplitLayoutSolver();
        var settled = Settle(three, P(0, -1500f, 0f, 1), P(1, 400f, 300f, 2), P(2, 500f, -300f, 3));
        for (int frame = 0; frame < 300; frame++)
        {
            double angle = frame / 300.0 * Math.PI * 2.0;
            var next = three.Solve(new[] {
                P(0, (float)(1500 * Math.Cos(angle)), (float)(900 * Math.Sin(angle)), 1),
                P(1, (float)(-1500 * Math.Cos(angle)), (float)(-900 * Math.Sin(angle)), 2),
                P(2, (float)(900 * Math.Sin(angle)), (float)(1500 * Math.Cos(angle)), 3) }, 1f / 60f, Settings);
            Check(!next.restructured && !next.sliding, "Three players moving restructured the layout at frame " + frame);
            for (int i = 0; i < 3; i++)
                Check(SameRect(next.viewports[i].polygon, settled.viewports[i].polygon),
                    "Three-player cell " + i + " moved with the players at frame " + frame);
        }

        // Rooms changing (map positions swinging) hold as well.
        var rooms = new SplitLayoutSolver();
        var a = Settle(rooms, P(0, -1500f, 0f, 1, 1), P(1, 400f, 300f, 2, 2), P(2, 500f, -300f, 3, 3));
        var b = Settle(rooms, P(0, 1500f, 0f, 1, 9), P(1, -400f, -300f, 2, 9), P(2, -500f, 300f, 3, 9));
        for (int i = 0; i < 3; i++)
            Check(SameRect(a.viewports[i].polygon, b.viewports[i].polygon),
                "Players changing rooms were rearranged by map position: cam " + a.viewports[i].cameraNumber);
    }

    private static void ThreePlayerSlots()
    {
        // Three singles: camera 0 owns the top half, cameras 1 and 2 the bottom
        // quarters, left to right - wherever they stand.
        var layout = Settle(new SplitLayoutSolver(), P(0, 1500f, -900f, 1), P(1, -400f, 300f, 2), P(2, 500f, 900f, 3));
        Tiling(layout, "three");
        PanBudget(layout, "three");
        Check(Math.Abs(layout.viewports[0].areaFraction - 0.5f) < 0.02f, "Camera 0 did not get the half cell");
        Check(CellMin(layout.viewports[0]).y > 0.5f - 1e-3f, "Camera 0's half is not the top half");
        Check(Math.Abs(layout.viewports[1].areaFraction - 0.25f) < 0.02f &&
            Math.Abs(layout.viewports[2].areaFraction - 0.25f) < 0.02f, "Remaining players did not get quarters");
        Check(CellMax(layout.viewports[1]).x < 0.5f + 1e-3f && CellMin(layout.viewports[2]).x > 0.5f - 1e-3f,
            "Bottom quarters are not ordered by camera number");
        Check(layout.dividers.Length >= 2, "Three-player dividers missing");

        // A merged pair beside a single takes the top, whoever is in it.
        var pair = Settle(new SplitLayoutSolver(), P(0, 900f, 0f, 5), P(1, -70f, 0f, 1), P(2, 70f, 0f, 1), P(3, 2000f, 0f, 6));
        Tiling(pair, "pair on top");
        Check(pair.viewports[2].sharesImageWith == 1, "Cameras 1 and 2 did not merge");
        Check(CellMin(pair.viewports[1]).y > 0.3f && CellMin(pair.viewports[2]).y > 0.3f &&
            Math.Abs(pair.viewports[1].groupAreaFraction - 2f / 3f) < 0.02f,
            "Merged pair did not take the top two thirds: " + pair.viewports[1].groupAreaFraction);
        Check(CellMax(pair.viewports[0]).x < 0.5f + 1e-3f && CellMin(pair.viewports[3]).x > 0.5f - 1e-3f &&
            CellMax(pair.viewports[0]).y < 0.4f, "Singles beside a merged pair are not the bottom quarters by number");
    }

    private static void FourPlayerGrid()
    {
        var layout = Settle(new SplitLayoutSolver(), P(0, 800f, -500f, 1), P(1, -800f, -500f, 2),
            P(2, 800f, 500f, 3), P(3, -800f, 500f, 4));
        Tiling(layout, "grid");
        PanBudget(layout, "grid");
        foreach (var view in layout.viewports)
        {
            Vector2 min, max;
            Bounds(view.polygon, out min, out max);
            Check(Math.Abs(max.x - min.x - 0.5f) < 0.01f && Math.Abs(max.y - min.y - 0.5f) < 0.01f,
                "Four players did not form a 2x2 grid");
        }
        Check(layout.viewports[0].centroid.x < 0.5f && layout.viewports[0].centroid.y > 0.5f, "Camera 0 is not top-left");
        Check(layout.viewports[1].centroid.x > 0.5f && layout.viewports[1].centroid.y > 0.5f, "Camera 1 is not top-right");
        Check(layout.viewports[2].centroid.x < 0.5f && layout.viewports[2].centroid.y < 0.5f, "Camera 2 is not bottom-left");
        Check(layout.viewports[3].centroid.x > 0.5f && layout.viewports[3].centroid.y < 0.5f, "Camera 3 is not bottom-right");
    }

    private static void PairSlotsFollowNumbers()
    {
        // Two pairs: the pair holding camera 0 is the left half; each pair tiles
        // its half side by side... no, stacked: a half is taller than wide.
        var layout = Settle(new SplitLayoutSolver(), P(0, -1000f, 0f), P(1, -990f, 0f), P(2, 990f, 0f, 2), P(3, 1000f, 0f, 2));
        Tiling(layout, "2+2");
        Check(layout.viewports[1].sharesImageWith == 0 && layout.viewports[3].sharesImageWith == 2, "Pairs did not merge");
        Check(CellMax(layout.viewports[0]).x < 0.5f + 1e-3f && CellMax(layout.viewports[1]).x < 0.5f + 1e-3f,
            "The pair with camera 0 is not the left half");
        Check(CellMin(layout.viewports[2]).x > 0.5f - 1e-3f && CellMin(layout.viewports[3]).x > 0.5f - 1e-3f,
            "The pair with camera 2 is not the right half");
        Check(CellMin(layout.viewports[0]).y > CellMin(layout.viewports[1]).y, "Within a column the lower number is not on top");

        // A single beside a pair: whoever has the lower number is left; the pair owns two thirds.
        var single = Settle(new SplitLayoutSolver(), P(0, 900f, 0f, 2), P(1, -70f, 0f), P(2, 70f, 0f));
        Check(CellMax(single.viewports[0]).x < 0.34f + 1e-3f, "Camera 0 alone is not the left third");
        Check(Math.Abs(single.viewports[1].groupAreaFraction - 2f / 3f) < 0.02f, "Pair on the right does not own two thirds");
    }

    private static void Weighting(string name, float[] expected, params SplitLayoutSolver.PlayerInput[] input)
    {
        var layout = Settle(new SplitLayoutSolver(), input);
        Tiling(layout, name);
        PanBudget(layout, name);
        var actual = new float[input.Length];
        for (int i = 0; i < input.Length; i++) actual[i] = layout.viewports[i].groupAreaFraction;
        Array.Sort(actual);
        Array.Sort(expected);
        for (int i = 0; i < input.Length; i++)
            Check(Math.Abs(actual[i] - expected[i]) < 0.02f,
                name + " group areas " + string.Join(",", Array.ConvertAll(actual, v => v.ToString("0.00"))) +
                " expected " + string.Join(",", Array.ConvertAll(expected, v => v.ToString("0.00"))));
        foreach (var view in layout.viewports)
        {
            Vector2 min, max;
            Bounds(view.polygon, out min, out max);
            if (view.sharesImageWith < 0)
                Check(max.x - min.x >= 0.25f - 1e-3f && max.y - min.y >= 0.25f - 1e-3f,
                    name + ": cell " + view.cameraNumber + " is a sliver " + (max.x - min.x) + "x" + (max.y - min.y));
        }
    }

    private static void Continuity()
    {
        var solver = new SplitLayoutSolver();
        var merged = Settle(solver, P(0, -50f, 0f), P(1, 50f, 0f));
        Check(merged.viewports[0].zoom == 1f && merged.viewports[1].zoom == 1f, "Merged zoom not native");
        Check(merged.dividers.Length == 0, "Merged divider visible");
        Check(merged.viewports[1].sharesImageWith == 0, "Merged pair does not share one image");
        float threshold = Settings.mergeDistance;
        var next = solver.Solve(new[] { P(0, -(threshold + 2f) / 2f, 0f),
            P(1, (threshold + 2f) / 2f, 0f) }, 1f / 60f, Settings);
        Check(next.viewports[0].splitAmount < 0.001f, "Split jumped at merge threshold");
        // Hold the pair just past the merge band: the damped split must rise
        // gradually from 0, never jump, and settle inside the blend range.
        var blending = next;
        float largestSplitStep = 0f;
        for (int frame = 0; frame < 60; frame++)
        {
            var step = solver.Solve(new[] { P(0, -(threshold + 90f) / 2f, 0f),
                P(1, (threshold + 90f) / 2f, 0f) }, 1f / 60f, Settings);
            largestSplitStep = Math.Max(largestSplitStep, Math.Abs(step.viewports[0].splitAmount - blending.viewports[0].splitAmount));
            Check(Math.Abs(step.viewports[0].zoom - blending.viewports[0].zoom) < 0.1f, "Zoom popped during split");
            Check(Math.Abs(step.viewports[0].regionAnchor.x - blending.viewports[0].regionAnchor.x) < 0.1f,
                "Anchor popped during split");
            Check((step.viewports[0].centroid - blending.viewports[0].centroid).magnitude < 0.01f,
                "Cells moved when the pair began to split");
            blending = step;
        }
        Check(blending.viewports[0].splitAmount > 0.1f && blending.viewports[0].splitAmount < 0.3f,
            "Blend did not settle inside the blend band: " + blending.viewports[0].splitAmount);
        Check(largestSplitStep < 0.08f, "Blend is not gradual: " + largestSplitStep);
    }

    private static SplitLayoutSolver.Layout SettleWith(SplitLayoutSolver solver, SplitLayoutSolver.Settings settings,
        params SplitLayoutSolver.PlayerInput[] input)
    {
        SplitLayoutSolver.Layout layout = null;
        for (int i = 0; i < 180; i++) layout = solver.Solve(input, 1f / 60f, settings);
        return layout;
    }

    private static SplitLayoutSolver.ViewportState View(SplitLayoutSolver.Layout layout, int camera)
    {
        foreach (var view in layout.viewports) if (view.cameraNumber == camera) return view;
        throw new Exception("No region for camera " + camera);
    }

    private static bool IsRect(SplitLayoutSolver.ViewportState view, float x0, float y0, float x1, float y1)
    {
        Vector2 min = CellMin(view), max = CellMax(view);
        return Math.Abs(min.x - x0) < 0.01f && Math.Abs(min.y - y0) < 0.01f &&
            Math.Abs(max.x - x1) < 0.01f && Math.Abs(max.y - y1) < 0.01f;
    }

    // The Static split style is the solver's permanentSplit fed with the players
    // who are alive. Its contract: regions depend on the number of living players
    // and on nothing else - not on where they stand, not on sharing a screen - and a
    // death or a revival hands the screen out again.
    private static void StaticStyle()
    {
        var settings = new SplitLayoutSolver.Settings { permanentSplit = true };
        for (int count = 1; count <= 4; count++)
        {
            var solver = new SplitLayoutSolver();
            SplitLayoutSolver.Layout settled = null;
            for (int tick = 0; tick < 240; tick++)
            {
                // Everybody on ONE screen of one room, wandering within a few tiles
                // of each other: Dynamic merges this into a single view.
                var input = new SplitLayoutSolver.PlayerInput[count];
                for (int p = 0; p < count; p++)
                    input[p] = P(p, 30f * p + 200f * (float)Math.Sin(tick * 0.05f + p),
                        40f * (float)Math.Cos(tick * 0.03f + p), 1, 1);
                var layout = solver.Solve(input, 1f / 40f, settings);
                Check(layout.viewports.Length == count, "Static " + count + ": lost a region at tick " + tick);
                if (tick < 60) continue;
                if (tick == 60) settled = layout;
                Tiling(layout, "static " + count + " tick " + tick);
                Check(!layout.sliding && (tick == 60 || !layout.restructured),
                    "Static " + count + ": layout moved at tick " + tick);
                foreach (var view in layout.viewports)
                {
                    Check(!view.ghost && view.rendering && view.sharesImageWith == -1,
                        "Static " + count + ": camera " + view.cameraNumber + " does not own its region");
                    if (count > 1) Check(view.splitAmount == 1f, "Static " + count + ": regions started merging");
                    Check(SameRect(view.polygon, View(settled, view.cameraNumber).polygon),
                        "Static " + count + ": camera " + view.cameraNumber + "'s region moved at tick " + tick);
                }
                foreach (var divider in layout.dividers)
                    Check(divider.alpha == 1f, "Static " + count + ": divider faded");
            }
            if (count == 1) Check(IsRect(View(settled, 0), 0f, 0f, 1f, 1f), "Static 1 is not full screen");
            if (count == 2)
                Check(IsRect(View(settled, 0), 0f, 0f, 0.5f, 1f) && IsRect(View(settled, 1), 0.5f, 0f, 1f, 1f),
                    "Static 2 is not camera 0 left, camera 1 right");
            if (count == 3)
                Check(IsRect(View(settled, 0), 0f, 0.5f, 1f, 1f) && IsRect(View(settled, 1), 0f, 0f, 0.5f, 0.5f) &&
                    IsRect(View(settled, 2), 0.5f, 0f, 1f, 0.5f), "Static 3 is not a half over two quarters");
            if (count == 4)
                Check(IsRect(View(settled, 0), 0f, 0.5f, 0.5f, 1f) && IsRect(View(settled, 1), 0.5f, 0.5f, 1f, 1f) &&
                    IsRect(View(settled, 2), 0f, 0f, 0.5f, 0.5f) && IsRect(View(settled, 3), 0.5f, 0f, 1f, 0.5f),
                    "Static 4 is not a grid in camera order");
        }

        // A death reassigns the screen to the survivors, a revival gives it back.
        var game = new SplitLayoutSolver();
        var three = SettleWith(game, settings, P(0, 0f, 0f, 1, 1), P(1, 10f, 0f, 1, 1), P(2, 20f, 0f, 1, 1));
        var two = SettleWith(game, settings, P(0, 0f, 0f, 1, 1), P(2, 20f, 0f, 1, 1));
        Check(two.viewports.Length == 2, "Static: the dead player's region was not removed");
        Tiling(two, "static after a death");
        Check(IsRect(View(two, 0), 0f, 0f, 0.5f, 1f) && IsRect(View(two, 2), 0.5f, 0f, 1f, 1f),
            "Static: two survivors did not get the halves in camera order");
        var one = SettleWith(game, settings, P(2, 20f, 0f, 1, 1));
        Check(one.viewports.Length == 1 && IsRect(View(one, 2), 0f, 0f, 1f, 1f),
            "Static: the last survivor did not get the whole screen");
        var back = SettleWith(game, settings, P(0, 0f, 0f, 1, 1), P(1, 10f, 0f, 1, 1), P(2, 20f, 0f, 1, 1));
        Tiling(back, "static after revivals");
        for (int camera = 0; camera < 3; camera++)
            Check(SameRect(View(back, camera).polygon, View(three, camera).polygon),
                "Static: camera " + camera + " did not return to its three-player region");
    }

    private static void DeathReflow()
    {
        var solver = new SplitLayoutSolver();
        var four = Settle(solver, P(0, -900f, 0f, 1), P(1, -100f, 0f, 2), P(2, 100f, 0f, 3), P(3, 900f, 0f, 4));
        Check(four.viewports.Length == 4, "Four-player input lost a region");
        var first = solver.Solve(new[] { P(0, -900f, 0f, 1), P(1, -100f, 0f, 2), P(2, 100f, 0f, 3) }, 1f / 60f, Settings);
        var three = Settle(solver, P(0, -900f, 0f, 1), P(1, -100f, 0f, 2), P(2, 100f, 0f, 3));
        Check(first.viewports.Length == 4 && first.viewports[3].ghost && three.viewports.Length == 3,
            "Death transition did not shrink then remove the dead camera");
        for (int i = 0; i < 3; i++)
            Check(Math.Abs(first.viewports[i].areaFraction - four.viewports[i].areaFraction) < 0.15f,
                "Survivor area jumped on the first death frame");
        Check(first.viewports[3].areaFraction < four.viewports[3].areaFraction,
            "Dead region did not start shrinking");
        Tiling(three, "after death");
        float total = 0f;
        foreach (var view in three.viewports) total += view.areaFraction;
        Check(Math.Abs(total - 1f) < 0.01f, "Death area did not reflow");
    }

    private static void DeathCurve()
    {
        var solver = new SplitLayoutSolver();
        var input = new[] { P(0, -900f, 0f, 1), P(1, -100f, 0f, 2),
            P(2, 100f, 0f, 3), P(3, 900f, 0f, 4) };
        var previous = Settle(solver, input);
        float startingArea = previous.viewports[3].areaFraction;
        float largestChange = 0f;
        float quarterTimeArea = 0f, halfTimeArea = 0f;
        for (int frame = 0; frame < 41; frame++)
        {
            var next = solver.Solve(new[] { input[0], input[1], input[2] }, 1f / 60f, Settings);
            Check(next.viewports.Length == 4 && next.viewports[3].ghost,
                "Dead view disappeared before its smooth transition finished");
            Tiling(next, "death frame " + frame);
            largestChange = Math.Max(largestChange,
                Math.Abs(next.viewports[3].areaFraction - previous.viewports[3].areaFraction));
            if (frame == 10) quarterTimeArea = next.viewports[3].areaFraction;
            if (frame == 20) halfTimeArea = next.viewports[3].areaFraction;
            previous = next;
        }
        Check(largestChange < 0.06f, "Dead view area popped during reflow: " + largestChange);
        Check(quarterTimeArea > startingArea * 0.35f,
            "Dead view collapsed too early in its transition: " + quarterTimeArea);
        Check(halfTimeArea < quarterTimeArea && halfTimeArea > 0.005f,
            "Dead view did not steadily shrink through the transition");
        Check(solver.Solve(new[] { input[0], input[1], input[2] }, 1f / 60f, Settings)
            .viewports.Length == 3, "Dead view remained after its transition");
    }

    private static void SharedSourceUv()
    {
        var solver = new SplitLayoutSolver();
        var left = P(0, -145f, 0f);
        var right = P(1, 145f, 0f);
        left.mergedScreenPos = new Vector2(0.35f, 0.52f);
        right.mergedScreenPos = new Vector2(0.65f, 0.49f);
        var layout = Settle(solver, left, right);
        var a = layout.viewports[0];
        var b = layout.viewports[1];
        Check(b.sharesImageWith == 0 && a.zoom == b.zoom && a.splitAmount == b.splitAmount,
            "Nearby players do not share one source image, zoom and split");
        Vector2 shiftA = left.mergedScreenPos - a.regionAnchor / a.zoom;
        Vector2 shiftB = right.mergedScreenPos - b.regionAnchor / b.zoom;
        Check((shiftA - shiftB).magnitude < 0.0001f,
            "Merged camera cells sample different UV transforms");
        Check((a.groupMin - Vector2.zero).magnitude < 1e-5f && (a.groupMax - Vector2.one).magnitude < 1e-5f,
            "Merged pair does not own the whole screen");

        // A pair sharing an image next to a third player still uses one transform.
        var three = new SplitLayoutSolver();
        var trio = Settle(three, left, right, P(2, 1400f, 0f, 2));
        var ta = trio.viewports[0];
        var tb = trio.viewports[1];
        Check(tb.sharesImageWith == 0 && ta.zoom == tb.zoom && ta.splitAmount == tb.splitAmount,
            "Grouped pair beside a third player diverged in zoom or split");
        Vector2 tShiftA = left.mergedScreenPos - ta.regionAnchor / ta.zoom;
        Vector2 tShiftB = right.mergedScreenPos - tb.regionAnchor / tb.zoom;
        Check((tShiftA - tShiftB).magnitude < 0.0001f, "Grouped pair beside a third player sample different UVs");
        Check(Math.Abs(ta.groupAreaFraction - 2f / 3f) < 0.02f, "Grouped pair did not own two thirds");
    }

    private static void ConservativeSameScreenSplit()
    {
        var near = Settle(new SplitLayoutSolver(), P(0, -300f, 0f), P(1, 300f, 0f));
        var far = Settle(new SplitLayoutSolver(), P(0, -650f, 0f), P(1, 650f, 0f));
        var separateScreens = Settle(new SplitLayoutSolver(), P(0, -20f, 0f, 1), P(1, 20f, 0f, 2));
        Check(near.viewports[0].splitAmount == 0f && near.dividers.Length == 0,
            "Players still split too early on one camera screen");
        Check(far.viewports[0].splitAmount == 1f,
            "Very distant players no longer receive independent views");
        Check(separateScreens.viewports[0].splitAmount == 1f,
            "Players on different camera screens must split immediately");
    }

    private static void SharedCameraWindow()
    {
        Check(SplitLayoutSolver.SharedCameraCanShow(new Vector2(0.05f, 0.95f)),
            "The safe edge of a shared camera view was rejected");
        Check(!SplitLayoutSolver.SharedCameraCanShow(new Vector2(0.02f, 0.5f)) &&
            !SplitLayoutSolver.SharedCameraCanShow(new Vector2(0.5f, 1.02f)),
            "A player outside the shared camera view was merged");
    }

    private static void MergeSplitSweep()
    {
        var solver = new SplitLayoutSolver();
        var previous = Settle(solver, P(0, -100f, 0f), P(1, 100f, 0f));
        float largestAnchorChange = 0f, largestZoomChange = 0f, largestCellChange = 0f;
        for (int frame = 0; frame < 240; frame++)
        {
            float distance = frame < 120 ? Settings.mergeDistance - 80f + frame * 4.5f :
                Settings.mergeDistance + 460f - (frame - 120) * 4.5f;
            var next = solver.Solve(new[] { P(0, -distance / 2f, 0f), P(1, distance / 2f, 0f) },
                1f / 60f, Settings);
            largestAnchorChange = Math.Max(largestAnchorChange,
                (next.viewports[0].regionAnchor - previous.viewports[0].regionAnchor).magnitude);
            largestZoomChange = Math.Max(largestZoomChange,
                Math.Abs(next.viewports[0].zoom - previous.viewports[0].zoom));
            largestCellChange = Math.Max(largestCellChange,
                (next.viewports[0].centroid - previous.viewports[0].centroid).magnitude);
            previous = next;
        }
        Check(largestAnchorChange < 0.06f, "Anchor jumped during continuous join/leave: " + largestAnchorChange);
        Check(largestZoomChange < 0.06f, "Zoom jumped during continuous join/leave: " + largestZoomChange);
        Check(largestCellChange < 0.001f, "Cells moved during a same-screen join/leave: " + largestCellChange);
        // The merge glides over about half a second, so give it that after the sweep.
        float rest = (Settings.mergeDistance - 80f) / 2f;
        for (int frame = 0; frame < 60; frame++)
            previous = solver.Solve(new[] { P(0, -rest, 0f), P(1, rest, 0f) }, 1f / 60f, Settings);
        Check(previous.viewports[0].imageBlend < 0.1f, "Images did not blend back on return");
    }

    private static void MergeIsTheOnlyRestructure()
    {
        // Three players: two of them approaching and merging is the one thing that
        // rearranges the cells - once, as a slide - and moving apart again is the
        // other. Everything in between holds.
        var solver = new SplitLayoutSolver();
        var apart = Settle(solver, P(0, -600f, 0f, 1), P(1, 600f, 0f, 1), P(2, 3000f, 0f, 2));
        Check(Math.Abs(apart.viewports[2].groupAreaFraction - 0.25f) < 0.02f, "Setup: three singles should give camera 2 a quarter");
        int restructures = 0;
        float largest = 0f;
        var previous = apart;
        for (int frame = 0; frame < 150; frame++)
        {
            float half = Math.Max(50f, 600f - frame * 12f);
            var next = solver.Solve(new[] { P(0, -half, 0f, 1), P(1, half, 0f, 1), P(2, 3000f, 0f, 2) }, 1f / 60f, Settings);
            if (next.restructured) restructures++;
            // What is drawn must move smoothly; the resting rectangle may change at once.
            largest = Math.Max(largest, Math.Abs(Area(next.viewports[2].polygon) - Area(previous.viewports[2].polygon)));
            previous = next;
        }
        Check(previous.viewports[1].sharesImageWith == 0, "Pair did not merge while approaching");
        Check(restructures == 1, "Merging should restructure exactly once, got " + restructures);
        Check(Math.Abs(previous.viewports[2].groupAreaFraction - 1f / 3f) < 0.02f,
            "Single beside a merged pair should own a third: " + previous.viewports[2].groupAreaFraction);
        Check(CellMin(previous.viewports[2]).x > 0.6f, "Camera 2 alone should be the right third");
        Check(largest < 0.03f, "Cell popped while a pair formed: " + largest);

        // Merged players wandering around each other on one screen change nothing.
        for (int frame = 0; frame < 120; frame++)
        {
            double angle = frame / 120.0 * Math.PI * 2.0;
            var next = solver.Solve(new[] { P(0, (float)(150 * Math.Cos(angle)), (float)(120 * Math.Sin(angle)), 1),
                P(1, (float)(-150 * Math.Cos(angle)), (float)(-120 * Math.Sin(angle)), 1), P(2, 3000f, 0f, 2) }, 1f / 60f, Settings);
            Check(!next.restructured, "Merged pair moving restructured the layout at frame " + frame);
        }

        // Parting restructures once more and returns to the three-slot layout.
        restructures = 0;
        SplitLayoutSolver.Layout parted = null;
        for (int frame = 0; frame < 240; frame++)
        {
            parted = solver.Solve(new[] { P(0, -900f, 0f, 1), P(1, 900f, 0f, 1), P(2, 3000f, 0f, 2) }, 1f / 60f, Settings);
            if (parted.restructured) restructures++;
        }
        Check(restructures == 1, "Parting should restructure exactly once, got " + restructures);
        Check(parted.viewports[1].sharesImageWith < 0, "Pair did not part");
        Check(Math.Abs(parted.viewports[0].areaFraction - 0.5f) < 0.02f && CellMin(parted.viewports[0]).y > 0.5f - 1e-3f,
            "After parting camera 0 should be back on the top half");
        Tiling(parted, "after parting");
    }

    private static void SlideOnMerge()
    {
        // Three players restructure through a merge: every cell slides from its old
        // rectangle to its new one; the target rectangles always tile the screen.
        var solver = new SplitLayoutSolver();
        var before = Settle(solver, P(0, -1500f, 0f, 1), P(1, 400f, 300f, 2), P(2, 500f, -300f, 3));
        Check(!before.sliding, "Settled layout still sliding");
        bool sawSlide = false;
        float largestStep = 0f;
        var previous = before;
        for (int frame = 0; frame < 120; frame++)
        {
            var next = solver.Solve(new[] { P(0, -1500f, 0f, 1), P(1, -50f, 0f, 3), P(2, 50f, 0f, 3) }, 1f / 60f, Settings);
            float targetTotal = 0f;
            foreach (var view in next.viewports)
            {
                Check(IsRectangle(view.targetPolygon) && IsRectangle(view.polygon), "Sliding cell is not rectangular");
                targetTotal += Area(view.targetPolygon);
            }
            Check(Math.Abs(targetTotal - 1f) < 0.01f, "Target rectangles do not tile the screen during a slide");
            if (next.sliding) sawSlide = true;
            else if (sawSlide)
            {
                foreach (var view in next.viewports)
                    Check((Bounds1(view.polygon) - Bounds1(view.targetPolygon)).magnitude < 1e-4f,
                        "Slide did not end on the target rectangle");
                break;
            }
            largestStep = Math.Max(largestStep, (next.viewports[0].centroid - previous.viewports[0].centroid).magnitude);
            previous = next;
        }
        Check(sawSlide, "Merge did not start a slide");
        Check(largestStep < 0.06f, "Cell jumped during the slide: " + largestStep);
    }

    private static Vector2 Bounds1(Vector2[] polygon)
    {
        Vector2 min, max;
        Bounds(polygon, out min, out max);
        return min + max;
    }

    private static void ScreenArrivalGlides()
    {
        // Two players on different screens; one arrives on the other's screen 300
        // world units away. The split must fall off gradually, not snap to merged.
        var solver = new SplitLayoutSolver();
        var apart = Settle(solver, P(0, -1200f, 0f, 1), P(1, 1200f, 0f, 2));
        Check(apart.viewports[0].splitAmount == 1f, "Setup: separate screens should be fully split");
        var first = solver.Solve(new[] { P(0, -150f, 0f, 1), P(1, 150f, 0f, 1) }, 1f / 60f, Settings);
        Check(first.viewports[0].splitAmount > 0.5f && first.viewports[0].splitAmount < 1f,
            "Arriving on the same screen snapped the split to " + first.viewports[0].splitAmount);
        var settled = Settle(solver, P(0, -150f, 0f, 1), P(1, 150f, 0f, 1));
        Check(settled.viewports[0].splitAmount == 0f && settled.viewports[1].sharesImageWith == 0,
            "Same-screen players did not end up merged");
        Check((Bounds1(settled.viewports[0].polygon) - Bounds1(apart.viewports[0].polygon)).magnitude < 1e-4f,
            "Merging moved the cells; only the pan should change");
    }

    private static float Area(Vector2[] polygon)
    {
        float twice = 0f;
        for (int i = 0; i < polygon.Length; i++)
        {
            Vector2 a = polygon[i], b = polygon[(i + 1) % polygon.Length];
            twice += a.x * b.y - b.x * a.y;
        }
        return Math.Abs(twice) * 0.5f;
    }

    public static int Main()
    {
        try
        {
            TwoPlayerSlotsAreFixed();
            PositionsNeverRestructure();
            ThreePlayerSlots();
            FourPlayerGrid();
            PairSlotsFollowNumbers();
            MergeIsTheOnlyRestructure();
            SlideOnMerge();
            DividerFadesWithDistance();
            ScreenArrivalGlides();
            Weighting("2", new[] { 0.5f, 0.5f }, P(0, -800f, 0f, 1), P(1, 800f, 0f, 2));
            Weighting("2+1", new[] { 2f / 3f, 2f / 3f, 1f / 3f }, P(0, -70f, 0f), P(1, 70f, 0f), P(2, 900f, 0f, 2));
            Weighting("1+1+1", new[] { 0.5f, 0.25f, 0.25f }, P(0, -900f, 0f, 1), P(1, 100f, 300f, 2), P(2, 200f, -300f, 3));
            Weighting("2+2", new[] { 0.5f, 0.5f, 0.5f, 0.5f }, P(0, -1000f, 0f), P(1, -990f, 0f), P(2, 990f, 0f, 2), P(3, 1000f, 0f, 2));
            Weighting("1+1+1+1", new[] { 0.25f, 0.25f, 0.25f, 0.25f }, P(0, -800f, 400f, 1), P(1, 800f, 400f, 2), P(2, -800f, -400f, 3), P(3, 800f, -400f, 4));
            Weighting("3+1", new[] { 0.75f, 0.75f, 0.75f, 0.25f }, P(0, -20f, 0f), P(1, 0f, 0f), P(2, 20f, 0f), P(3, 900f, 0f, 2));
            Weighting("coincident", new[] { 2f / 3f, 2f / 3f, 1f / 3f }, P(0, 0f, 0f), P(1, 0f, 0f), P(2, 900f, 0f, 2));
            Continuity();
            MergeSplitSweep();
            DeathReflow();
            DeathCurve();
            SharedSourceUv();
            ConservativeSameScreenSplit();
            SharedCameraWindow();
            StaticStyle();
            Console.WriteLine("PASS: " + checks + " layout checks");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL: " + error.Message);
            return 1;
        }
    }
}
