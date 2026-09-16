using System;
using SplitScreenCoop;
using UnityEngine;

internal static class Program
{
    private static int checks;
    private static readonly SplitLayoutSolver.Settings Settings = new SplitLayoutSolver.Settings();

    private static SplitLayoutSolver.PlayerInput P(int camera, float x, float y, long screen = 1)
    {
        return new SplitLayoutSolver.PlayerInput { playerIndex = camera, worldPos = new Vector2(x, y),
            sameScreenKey = screen, mergedScreenPos = new Vector2(0.5f, 0.5f), validWorldPos = true };
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

    private static void Direction(float x, float y, bool expectColumns)
    {
        var layout = Settle(new SplitLayoutSolver(), P(0, -x, -y, 1), P(1, x, y, 2));
        Tiling(layout, "direction");
        Vector2 difference = layout.viewports[1].centroid - layout.viewports[0].centroid;
        Check(Vector2.Dot(difference, new Vector2(x, y * Settings.screenAspect)) > 0f,
            "Two-player region did not follow world direction " + x + ", " + y);
        Vector2 min, max;
        Bounds(layout.viewports[0].polygon, out min, out max);
        bool columns = max.y - min.y > 0.99f;
        Check(columns == expectColumns, "Two-player split orientation wrong for " + x + ", " + y);
        Check(Math.Abs(layout.viewports[0].areaFraction - 0.5f) < 0.001f, "Two-player area not half");
        Check(layout.dividers.Length == 1 && layout.dividers[0].alpha == 1f, "Two-player divider missing");
        PanBudget(layout, "direction");
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

    private static void ThreePlayerIsolation()
    {
        // The player far from the other two owns the big half.
        var layout = Settle(new SplitLayoutSolver(), P(0, -1500f, 0f, 1), P(1, 400f, 300f, 2), P(2, 500f, -300f, 3));
        Tiling(layout, "isolation");
        Check(Math.Abs(layout.viewports[0].areaFraction - 0.5f) < 0.02f, "Isolated player did not get the half cell");
        Check(layout.viewports[0].centroid.x < 0.5f, "Isolated left player was placed on the right");
        Check(Math.Abs(layout.viewports[1].areaFraction - 0.25f) < 0.02f &&
            Math.Abs(layout.viewports[2].areaFraction - 0.25f) < 0.02f, "Remaining players did not get quarters");
        Check(layout.viewports[1].centroid.y > layout.viewports[2].centroid.y,
            "Stacked players are not ordered by height");
        Check(layout.dividers.Length >= 2, "Three-player dividers missing");
    }

    private static void FourPlayerGrid()
    {
        var layout = Settle(new SplitLayoutSolver(), P(0, -800f, 500f, 1), P(1, 800f, 500f, 2),
            P(2, -800f, -500f, 3), P(3, 800f, -500f, 4));
        Tiling(layout, "grid");
        PanBudget(layout, "grid");
        foreach (var view in layout.viewports)
        {
            Vector2 min, max;
            Bounds(view.polygon, out min, out max);
            Check(Math.Abs(max.x - min.x - 0.5f) < 0.01f && Math.Abs(max.y - min.y - 0.5f) < 0.01f,
                "Four players did not form a 2x2 grid");
        }
        Check(layout.viewports[0].centroid.x < 0.5f && layout.viewports[0].centroid.y > 0.5f, "Top-left player misplaced");
        Check(layout.viewports[3].centroid.x > 0.5f && layout.viewports[3].centroid.y < 0.5f, "Bottom-right player misplaced");
    }

    private static void Hysteresis()
    {
        // Jittering around the diagonal must not flip the split orientation each frame.
        var solver = new SplitLayoutSolver();
        var first = Settle(solver, P(0, -600f, -300f, 1), P(1, 600f, 300f, 2));
        int flips = 0;
        bool lastVertical = first.viewports[0].polygon[2].y - first.viewports[0].polygon[0].y < 0.99f;
        for (int frame = 0; frame < 240; frame++)
        {
            float wobble = (frame % 2 == 0 ? 1f : -1f) * 120f;
            var layout = solver.Solve(new[] { P(0, -600f + wobble, -300f - wobble, 1), P(1, 600f - wobble, 300f + wobble, 2) },
                1f / 60f, Settings);
            bool vertical = layout.viewports[0].polygon[2].y - layout.viewports[0].polygon[0].y < 0.99f;
            if (vertical != lastVertical) flips++;
            lastVertical = vertical;
        }
        Check(flips == 0, "Split orientation flipped " + flips + " times on jitter");

        // Crossing sides by less than the dead zone keeps the players where they are.
        var sides = new SplitLayoutSolver();
        Settle(sides, P(0, -500f, 0f, 1), P(1, 500f, 0f, 2));
        var crossed = sides.Solve(new[] { P(0, 40f, 0f, 1), P(1, -40f, 0f, 2) }, 1f / 60f, Settings);
        Check(crossed.viewports[0].centroid.x < crossed.viewports[1].centroid.x,
            "Players swapped sides inside the dead zone");
        var farCrossed = Settle(sides, P(0, 500f, 0f, 1), P(1, -500f, 0f, 2));
        Check(farCrossed.viewports[0].centroid.x > farCrossed.viewports[1].centroid.x,
            "Players never swapped sides after clearly crossing");
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
        Check(previous.viewports[0].imageBlend < 0.1f, "Images did not blend back on return");
    }

    private static void GroupFormationSlides()
    {
        // Two players beside a third join into one image: the outer cut should
        // slide from a half to two thirds rather than pop.
        var solver = new SplitLayoutSolver();
        var apart = Settle(solver, P(0, -600f, 0f, 1), P(1, 600f, 0f, 1), P(2, 3000f, 0f, 2));
        float before = apart.viewports[2].groupAreaFraction;
        float largest = 0f;
        var previous = apart;
        for (int frame = 0; frame < 120; frame++)
        {
            float half = Math.Max(50f, 600f - frame * 12f);
            var next = solver.Solve(new[] { P(0, -half, 0f, 1), P(1, half, 0f, 1), P(2, 3000f, 0f, 2) }, 1f / 60f, Settings);
            largest = Math.Max(largest, Math.Abs(next.viewports[2].groupAreaFraction - previous.viewports[2].groupAreaFraction));
            previous = next;
        }
        Check(previous.viewports[1].sharesImageWith == 0, "Pair did not merge while approaching");
        Check(previous.viewports[2].groupAreaFraction < before - 0.05f, "Third player's cell did not shrink for the pair");
        Check(largest < 0.03f, "Cell popped while a pair formed: " + largest);
    }

    private static void RotateTransition()
    {
        // Two players swap sides: the divider must sweep through intermediate
        // angles, never jump, keep the screen covered and end on rectangles.
        var solver = new SplitLayoutSolver();
        var before = Settle(solver, P(0, -600f, 0f, 1), P(1, 600f, 0f, 2));
        Check(before.viewports[0].centroid.x < 0.5f, "Setup: player 0 should start on the left");
        bool sawDiagonal = false;
        float largestCentroidStep = 0f;
        var previous = before;
        int frames = 0;
        for (int frame = 0; frame < 120; frame++)
        {
            var next = solver.Solve(new[] { P(0, 600f, 0f, 1), P(1, -600f, 0f, 2) }, 1f / 60f, Settings);
            frames++;
            float total = next.viewports[0].areaFraction + next.viewports[1].areaFraction;
            Check(Math.Abs(total - 1f) < 0.01f, "Rotating cells do not cover the screen: " + total);
            Check(Math.Abs(next.viewports[0].areaFraction - 0.5f) < 0.03f, "Rotating cell area drifted: " + next.viewports[0].areaFraction);
            Vector2 min, max;
            Bounds(next.viewports[0].polygon, out min, out max);
            Check(next.viewports[0].zoom >= Math.Max(max.x - min.x, max.y - min.y) - 1e-4f,
                "Rotating cell would sample outside its source screen");
            if (!IsRectangle(next.viewports[0].polygon)) sawDiagonal = true;
            largestCentroidStep = Math.Max(largestCentroidStep,
                (next.viewports[0].centroid - previous.viewports[0].centroid).magnitude);
            previous = next;
            if (next.viewports[0].centroid.x > 0.5f && IsRectangle(next.viewports[0].polygon) && frame > 5) break;
        }
        Check(sawDiagonal, "Side swap did not rotate through intermediate angles");
        Check(previous.viewports[0].centroid.x > 0.5f && IsRectangle(previous.viewports[0].polygon),
            "Side swap did not finish on the new rectangles");
        Check(largestCentroidStep < 0.08f, "Cell jumped during the rotate transition: " + largestCentroidStep);
        Check(frames < 60, "Rotate transition took too long: " + frames + " frames");
    }

    private static void SlideTransition()
    {
        // Three players restructure: every cell slides from its old rectangle to
        // its new one; the target rectangles always tile the screen; nothing fades.
        var solver = new SplitLayoutSolver();
        var before = Settle(solver, P(0, -1500f, 0f, 1), P(1, 400f, 300f, 2), P(2, 500f, -300f, 3));
        Check(!before.sliding, "Settled layout still sliding");
        bool sawSlide = false;
        float largestStep = 0f;
        var previous = before;
        for (int frame = 0; frame < 120; frame++)
        {
            var next = solver.Solve(new[] { P(0, -500f, 300f, 1), P(1, -400f, -300f, 2), P(2, 1500f, 0f, 3) }, 1f / 60f, Settings);
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
        Check(sawSlide, "Restructure did not start a slide");
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
            RotateTransition();
            SlideTransition();
            ScreenArrivalGlides();
            Direction(800f, 0f, true);
            Direction(-800f, 0f, true);
            Direction(0f, 800f, false);
            Direction(0f, -800f, false);
            Direction(600f, 450f, true);
            Weighting("2", new[] { 0.5f, 0.5f }, P(0, -800f, 0f, 1), P(1, 800f, 0f, 2));
            Weighting("2+1", new[] { 2f / 3f, 2f / 3f, 1f / 3f }, P(0, -70f, 0f), P(1, 70f, 0f), P(2, 900f, 0f, 2));
            Weighting("1+1+1", new[] { 0.5f, 0.25f, 0.25f }, P(0, -900f, 0f, 1), P(1, 100f, 300f, 2), P(2, 200f, -300f, 3));
            Weighting("2+2", new[] { 0.5f, 0.5f, 0.5f, 0.5f }, P(0, -1000f, 0f), P(1, -990f, 0f), P(2, 990f, 0f, 2), P(3, 1000f, 0f, 2));
            Weighting("1+1+1+1", new[] { 0.25f, 0.25f, 0.25f, 0.25f }, P(0, -800f, 400f, 1), P(1, 800f, 400f, 2), P(2, -800f, -400f, 3), P(3, 800f, -400f, 4));
            Weighting("3+1", new[] { 0.75f, 0.75f, 0.75f, 0.25f }, P(0, -20f, 0f), P(1, 0f, 0f), P(2, 20f, 0f), P(3, 900f, 0f, 2));
            Weighting("coincident", new[] { 2f / 3f, 2f / 3f, 1f / 3f }, P(0, 0f, 0f), P(1, 0f, 0f), P(2, 900f, 0f, 2));
            ThreePlayerIsolation();
            FourPlayerGrid();
            Hysteresis();
            Continuity();
            MergeSplitSweep();
            GroupFormationSlides();
            DeathReflow();
            DeathCurve();
            SharedSourceUv();
            ConservativeSameScreenSplit();
            SharedCameraWindow();
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
