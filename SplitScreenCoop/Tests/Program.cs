using System;
using SplitScreenCoop;
using UnityEngine;

internal static partial class Program
{
    private static int checks;
    private static readonly SplitLayoutSolver.Settings Settings = new SplitLayoutSolver.Settings();

    /// <summary>A player on camera <paramref name="camera"/> at a point of its screen (normalized, the centre by default).</summary>
    private static SplitLayoutSolver.PlayerInput P(int camera, float screenX = 0.5f, float screenY = 0.5f)
    {
        return new SplitLayoutSolver.PlayerInput { playerIndex = camera, screenPos = new Vector2(screenX, screenY) };
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
        // While regions slide, only the resting rectangles are a tiling; the drawn
        // ones may overlap or leave gaps, which the compositor covers with the
        // resting layout underneath.
        float total = 0f;
        foreach (var view in layout.viewports)
        {
            Vector2[] polygon = layout.sliding ? view.targetPolygon : view.polygon;
            Check(IsRectangle(polygon), name + ": region " + view.cameraNumber + " is not an axis-aligned rectangle");
            Vector2 min, max;
            Bounds(polygon, out min, out max);
            Check(min.x >= -1e-4f && min.y >= -1e-4f && max.x <= 1f + 1e-4f && max.y <= 1f + 1e-4f,
                name + ": region " + view.cameraNumber + " leaves the screen");
            total += layout.sliding ? Area(polygon) : view.areaFraction;
        }
        Check(Math.Abs(total - 1f) < 0.002f, name + ": regions do not tile the screen, total area " + total);
    }

    /// <summary>
    /// For any source position, the clamped pan must place the player inside the
    /// region it owns, and the region must never sample outside its one source screen.
    /// </summary>
    private static void PanBudget(SplitLayoutSolver.Layout layout, string name)
    {
        foreach (var view in layout.viewports)
        {
            if (view.ghost) continue;
            Vector2 min, max;
            Bounds(view.polygon, out min, out max);
            Check(view.zoom >= Math.Max(max.x - min.x, max.y - min.y) - 1e-4f && view.zoom <= 1f,
                name + ": zoom " + view.zoom + " cannot fit region " + view.cameraNumber + " inside one source screen");
            for (int sx = 0; sx <= 10; sx++)
            for (int sy = 0; sy <= 10; sy++)
            {
                Vector2 source = new Vector2(sx / 10f, sy / 10f);
                Vector2 shift = SplitLayoutSolver.ClampedUvShift(source, view.centroid, min, max, view.zoom);
                Vector2 displayed = (source - shift) * view.zoom;
                Check(displayed.x >= min.x - 1e-3f && displayed.x <= max.x + 1e-3f &&
                    displayed.y >= min.y - 1e-3f && displayed.y <= max.y + 1e-3f,
                    name + ": source " + source + " cannot be shown inside region " + view.cameraNumber);
                Vector2 uvMin = min / view.zoom + shift, uvMax = max / view.zoom + shift;
                Check(uvMin.x >= -1e-3f && uvMin.y >= -1e-3f && uvMax.x <= 1f + 1e-3f && uvMax.y <= 1f + 1e-3f,
                    name + ": region " + view.cameraNumber + " samples outside its source texture");
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
        // Wherever the players stand on their screens, camera 0 is the left column and
        // camera 1 the right column, each half the screen.
        var points = new[] { new Vector2(0.1f, 0.5f), new Vector2(0.9f, 0.5f), new Vector2(0.5f, 0.05f),
            new Vector2(0.5f, 0.95f), new Vector2(0.2f, 0.8f), new Vector2(0.8f, 0.2f) };
        foreach (Vector2 p in points)
        {
            var layout = Settle(new SplitLayoutSolver(), P(0, p.x, p.y), P(1, 1f - p.x, 1f - p.y));
            Tiling(layout, "two-fixed");
            PanBudget(layout, "two-fixed");
            Check(CellMax(layout.viewports[0]).x < 0.5f + 1e-4f && CellMin(layout.viewports[1]).x > 0.5f - 1e-4f,
                "Camera 0 must be the left column and camera 1 the right, players at " + p);
            Check(CellMax(layout.viewports[0]).y > 0.99f && CellMin(layout.viewports[0]).y < 0.01f,
                "Two-player regions must be full-height columns, players at " + p);
            Check(Math.Abs(layout.viewports[0].areaFraction - 0.5f) < 0.001f, "Two-player area not half");
            Check(layout.dividers.Length == 1, "Two-player divider missing");
        }
        // Camera order, not input order, decides the slot.
        var swapped = Settle(new SplitLayoutSolver(), P(1), P(0));
        Check(swapped.viewports[0].cameraNumber == 1 && CellMin(swapped.viewports[0]).x > 0.5f - 1e-4f,
            "Camera 1 given first must still take the right column");
    }

    private static void PositionsNeverRestructure()
    {
        // Players wandering and jittering over their screens move nothing: no slide,
        // no restructure, and every region keeps its rectangle exactly.
        var two = new SplitLayoutSolver();
        var before = Settle(two, P(0, 0.2f, 0.5f), P(1, 0.8f, 0.5f));
        for (int frame = 0; frame < 300; frame++)
        {
            double angle = frame / 300.0 * Math.PI * 2.0;
            float wobble = (frame % 2 == 0 ? 1f : -1f) * 0.05f;
            var next = two.Solve(new[] { P(0, 0.5f + 0.4f * (float)Math.Cos(angle) + wobble, 0.5f + 0.4f * (float)Math.Sin(angle)),
                P(1, 0.5f - 0.4f * (float)Math.Cos(angle), 0.5f - 0.4f * (float)Math.Sin(angle) - wobble) }, 1f / 60f, Settings);
            Check(!next.restructured && !next.sliding, "Two players moving restructured the layout at frame " + frame);
            for (int i = 0; i < 2; i++)
                Check(SameRect(next.viewports[i].polygon, before.viewports[i].polygon),
                    "Two-player region " + i + " moved with the players at frame " + frame);
        }

        var three = new SplitLayoutSolver();
        var settled = Settle(three, P(0, 0.1f, 0.5f), P(1, 0.6f, 0.7f), P(2, 0.7f, 0.3f));
        for (int frame = 0; frame < 300; frame++)
        {
            double angle = frame / 300.0 * Math.PI * 2.0;
            var next = three.Solve(new[] {
                P(0, 0.5f + 0.45f * (float)Math.Cos(angle), 0.5f + 0.45f * (float)Math.Sin(angle)),
                P(1, 0.5f - 0.45f * (float)Math.Cos(angle), 0.5f - 0.45f * (float)Math.Sin(angle)),
                P(2, 0.5f + 0.45f * (float)Math.Sin(angle), 0.5f + 0.45f * (float)Math.Cos(angle)) }, 1f / 60f, Settings);
            Check(!next.restructured && !next.sliding, "Three players moving restructured the layout at frame " + frame);
            for (int i = 0; i < 3; i++)
                Check(SameRect(next.viewports[i].polygon, settled.viewports[i].polygon),
                    "Three-player region " + i + " moved with the players at frame " + frame);
        }
    }

    private static void ThreePlayerSlots()
    {
        // Three players: camera 0 owns the top half, cameras 1 and 2 the bottom
        // quarters, left to right - wherever they stand.
        var layout = Settle(new SplitLayoutSolver(), P(0, 0.9f, 0.1f), P(1, 0.2f, 0.7f), P(2, 0.6f, 0.9f));
        Tiling(layout, "three");
        PanBudget(layout, "three");
        Check(Math.Abs(layout.viewports[0].areaFraction - 0.5f) < 0.02f, "Camera 0 did not get the half");
        Check(CellMin(layout.viewports[0]).y > 0.5f - 1e-3f, "Camera 0's half is not the top half");
        Check(Math.Abs(layout.viewports[1].areaFraction - 0.25f) < 0.02f &&
            Math.Abs(layout.viewports[2].areaFraction - 0.25f) < 0.02f, "Remaining players did not get quarters");
        Check(CellMax(layout.viewports[1]).x < 0.5f + 1e-3f && CellMin(layout.viewports[2]).x > 0.5f - 1e-3f,
            "Bottom quarters are not ordered by camera number");
        Check(layout.dividers.Length >= 2, "Three-player dividers missing");
    }

    private static void FourPlayerGrid()
    {
        var layout = Settle(new SplitLayoutSolver(), P(0, 0.9f, 0.2f), P(1, 0.1f, 0.2f), P(2, 0.9f, 0.8f), P(3, 0.1f, 0.8f));
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

    private static void Weighting(string name, float[] expected, params SplitLayoutSolver.PlayerInput[] input)
    {
        var layout = Settle(new SplitLayoutSolver(), input);
        Tiling(layout, name);
        PanBudget(layout, name);
        var actual = new float[input.Length];
        for (int i = 0; i < input.Length; i++) actual[i] = layout.viewports[i].areaFraction;
        Array.Sort(actual);
        Array.Sort(expected);
        for (int i = 0; i < input.Length; i++)
            Check(Math.Abs(actual[i] - expected[i]) < 0.02f,
                name + " areas " + string.Join(",", Array.ConvertAll(actual, v => v.ToString("0.00"))) +
                " expected " + string.Join(",", Array.ConvertAll(expected, v => v.ToString("0.00"))));
        foreach (var view in layout.viewports)
        {
            Vector2 min, max;
            Bounds(view.polygon, out min, out max);
            Check(max.x - min.x >= 0.25f - 1e-3f && max.y - min.y >= 0.25f - 1e-3f,
                name + ": region " + view.cameraNumber + " is a sliver " + (max.x - min.x) + "x" + (max.y - min.y));
        }
    }

    /// <summary>
    /// The pan rule. A lone view shows its camera's picture unpanned, wherever the player
    /// stands; while the screen is split each region centres its player, as far as the
    /// region's margin allows, whatever the player's position on their screen.
    /// </summary>
    private static void LoneViewIsUnpanned()
    {
        var points = new[] { new Vector2(0.5f, 0.5f), new Vector2(0.1f, 0.9f), new Vector2(0.95f, 0.05f) };
        foreach (Vector2 p in points)
        {
            var one = Settle(new SplitLayoutSolver(), P(0, p.x, p.y));
            var view = one.viewports[0];
            Check(view.splitAmount == 0f && view.zoom == 1f, "A lone view is split or zoomed");
            Check((view.regionAnchor - p).magnitude < 1e-6f, "A lone view is anchored away from its player: " + view.regionAnchor);
            Vector2 shift = SplitLayoutSolver.ClampedUvShift(p, view.regionAnchor, view.windowMin, view.windowMax, view.zoom);
            Check(shift.magnitude < 1e-6f, "A lone view's picture is panned by " + shift);

            var two = Settle(new SplitLayoutSolver(), P(0, p.x, p.y), P(1, 1f - p.x, p.y));
            foreach (var region in two.viewports)
            {
                Check(region.splitAmount == 1f, "A split region is not split");
                Vector2 center = (region.windowMin + region.windowMax) * 0.5f;
                Check((region.regionAnchor - SplitLayoutSolver.ClampVector(center, region.anchorMin, region.anchorMax)).magnitude < 1e-6f,
                    "A split region does not centre its player: anchor " + region.regionAnchor + " for players at " + p);
            }
        }
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

    // The Static split style is the solver fed with the players who are alive. Its
    // contract: regions depend on the number of living players and on nothing else -
    // not on where they stand, not on sharing a screen - and a death or a revival hands
    // the screen out again.
    private static void StaticStyle()
    {
        var settings = new SplitLayoutSolver.Settings();
        for (int count = 1; count <= 4; count++)
        {
            var solver = new SplitLayoutSolver();
            SplitLayoutSolver.Layout settled = null;
            for (int tick = 0; tick < 240; tick++)
            {
                // Everybody close together on one screen, wandering.
                var input = new SplitLayoutSolver.PlayerInput[count];
                for (int p = 0; p < count; p++)
                    input[p] = P(p, 0.5f + 0.02f * p + 0.1f * (float)Math.Sin(tick * 0.05f + p),
                        0.5f + 0.03f * (float)Math.Cos(tick * 0.03f + p));
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
                    if (count > 1) Check(view.splitAmount == 1f, "Static " + count + ": a region is not split");
                    Check(SameRect(view.polygon, View(settled, view.cameraNumber).polygon),
                        "Static " + count + ": camera " + view.cameraNumber + "'s region moved at tick " + tick);
                }
                Check(layout.dividers.Length >= count - 1, "Static " + count + ": dividers missing");
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
        var three = SettleWith(game, settings, P(0), P(1), P(2));
        var two = SettleWith(game, settings, P(0), P(2));
        Check(two.viewports.Length == 2, "Static: the dead player's region was not removed");
        Tiling(two, "static after a death");
        Check(IsRect(View(two, 0), 0f, 0f, 0.5f, 1f) && IsRect(View(two, 2), 0.5f, 0f, 1f, 1f),
            "Static: two survivors did not get the halves in camera order");
        var one = SettleWith(game, settings, P(2));
        Check(one.viewports.Length == 1 && IsRect(View(one, 2), 0f, 0f, 1f, 1f),
            "Static: the last survivor did not get the whole screen");
        var back = SettleWith(game, settings, P(0), P(1), P(2));
        Tiling(back, "static after revivals");
        for (int camera = 0; camera < 3; camera++)
            Check(SameRect(View(back, camera).polygon, View(three, camera).polygon),
                "Static: camera " + camera + " did not return to its three-player region");
    }

    private static void DeathReflow()
    {
        var solver = new SplitLayoutSolver();
        var four = Settle(solver, P(0), P(1), P(2), P(3));
        Check(four.viewports.Length == 4, "Four-player input lost a region");
        var first = solver.Solve(new[] { P(0), P(1), P(2) }, 1f / 60f, Settings);
        var three = Settle(solver, P(0), P(1), P(2));
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
        var input = new[] { P(0), P(1), P(2), P(3) };
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

    private static void SharedCameraWindow()
    {
        Check(SplitLayoutSolver.SharedCameraCanShow(new Vector2(0.05f, 0.95f)),
            "The safe edge of a shared camera view was rejected");
        Check(!SplitLayoutSolver.SharedCameraCanShow(new Vector2(0.02f, 0.5f)) &&
            !SplitLayoutSolver.SharedCameraCanShow(new Vector2(0.5f, 1.02f)),
            "A player outside the shared camera view was allowed to share it");
    }

    private static void SlideOnArrival()
    {
        // A fourth player arrives beside three: the tree restructures once, every region
        // slides from its old rectangle to its new one, and the target rectangles always
        // tile the screen.
        var solver = new SplitLayoutSolver();
        var before = Settle(solver, P(0), P(1), P(2));
        Check(!before.sliding, "Settled layout still sliding");
        bool sawSlide = false;
        int restructures = 0;
        float largestStep = 0f;
        var previous = before;
        for (int frame = 0; frame < 120; frame++)
        {
            var next = solver.Solve(new[] { P(0), P(1), P(2), P(3) }, 1f / 60f, Settings);
            if (next.restructured) restructures++;
            float targetTotal = 0f;
            foreach (var view in next.viewports)
            {
                Check(IsRectangle(view.targetPolygon) && IsRectangle(view.polygon), "Sliding region is not rectangular");
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
        Check(sawSlide, "An arrival did not start a slide");
        Check(restructures == 1, "An arrival should restructure exactly once, got " + restructures);
        Check(largestStep < 0.06f, "A region jumped during the slide: " + largestStep);
        Tiling(previous, "after an arrival");
    }

    private static Vector2 Bounds1(Vector2[] polygon)
    {
        Vector2 min, max;
        Bounds(polygon, out min, out max);
        return min + max;
    }

    /// <summary>
    /// Solve keeps its working storage between calls (it runs every tick); nothing it
    /// returns may share any of it. Callers keep earlier layouts (the renderer until the
    /// next tick, the slide memory, these tests), so a layout must read the same after
    /// later calls, whatever they solve: one to four players, arrivals, a death.
    /// </summary>
    private static void KeptLayoutsDoNotChange()
    {
        var solver = new SplitLayoutSolver();
        var kept = new System.Collections.Generic.List<SplitLayoutSolver.Layout>();
        var copies = new System.Collections.Generic.List<string>();
        var rounds = new[]
        {
            new[] { P(0, 0.2f, 0.4f), P(1, 0.7f, 0.6f) },
            new[] { P(0, 0.3f, 0.5f), P(1, 0.6f, 0.5f), P(2, 0.9f, 0.1f) },
            new[] { P(0, 0.1f, 0.9f), P(1, 0.9f, 0.9f), P(2, 0.1f, 0.1f), P(3, 0.9f, 0.1f) },
            new[] { P(0, 0.1f, 0.9f), P(2, 0.1f, 0.1f), P(3, 0.9f, 0.1f) }, // player 2 died
            new[] { P(3, 0.5f, 0.5f) },
            new[] { P(0), P(1), P(2), P(3) },
        };
        foreach (var round in rounds)
            for (int tick = 0; tick < 40; tick++)
            {
                var layout = solver.Solve(round, 1f / 40f, Settings);
                if (tick % 13 != 0) continue;
                kept.Add(layout);
                copies.Add(Describe(layout));
            }
        for (int i = 0; i < kept.Count; i++)
            Check(Describe(kept[i]) == copies[i], "Layout " + i + " changed after later Solve calls");
    }

    private static string Describe(SplitLayoutSolver.Layout layout)
    {
        var text = new System.Text.StringBuilder();
        foreach (var view in layout.viewports)
        {
            text.Append(view.cameraNumber).Append(view.ghost).Append(view.sharesImageWith).Append(':');
            foreach (var point in view.polygon) text.Append(point.x.ToString("R")).Append(',').Append(point.y.ToString("R")).Append(' ');
            foreach (var point in view.targetPolygon) text.Append(point.x.ToString("R")).Append(',').Append(point.y.ToString("R")).Append(' ');
            text.Append(view.zoom.ToString("R")).Append(' ').Append(view.splitAmount.ToString("R")).Append(' ')
                .Append(view.regionAnchor.x.ToString("R")).Append(' ').Append(view.anchorMax.y.ToString("R")).Append('|');
        }
        foreach (var divider in layout.dividers)
            text.Append(divider.firstCamera).Append(divider.secondCamera).Append(divider.start.x.ToString("R")).Append(' ')
                .Append(divider.end.y.ToString("R")).Append(' ').Append(divider.width.ToString("R")).Append('|');
        foreach (var input in layout.effectiveInputs) text.Append(input.playerIndex).Append(input.screenPos.x.ToString("R")).Append(';');
        return text.ToString();
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
            SlideOnArrival();
            Weighting("2", new[] { 0.5f, 0.5f }, P(0, 0.1f, 0.5f), P(1, 0.9f, 0.5f));
            Weighting("1+1+1", new[] { 0.5f, 0.25f, 0.25f }, P(0, 0.1f, 0.5f), P(1, 0.6f, 0.8f), P(2, 0.7f, 0.2f));
            Weighting("1+1+1+1", new[] { 0.25f, 0.25f, 0.25f, 0.25f }, P(0, 0.1f, 0.9f), P(1, 0.9f, 0.9f), P(2, 0.1f, 0.1f), P(3, 0.9f, 0.1f));
            LoneViewIsUnpanned();
            DeathReflow();
            DeathCurve();
            SharedCameraWindow();
            StaticStyle();
            KeptLayoutsDoNotChange();
            AdaptiveStartsFromWhereEveryoneIs();
            AdaptiveMergeWaitsForTheGroup();
            AdaptiveSplitGraceAndGroupCrossing();
            AdaptiveRemergeCooldown();
            AdaptiveGridSlots();
            AdaptivePartialGroupsNeverMoveAnything();
            AdaptiveGridZoomIsAPureScale();
            AdaptiveHalvesInPlaceMergeIsStill();
            AdaptiveNothingJumps();
            AdaptiveReflow();
            AdaptiveNobodyAliveKeepsTheLayout();
            AdaptiveFullViewStaysWithTheGroup();
            AdaptiveFraming();
            FrameWindowPercentiles();
            FrameWindowCap();
            HitchRule();
            AllocationRate();
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
