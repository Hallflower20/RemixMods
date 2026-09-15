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

    private static void Direction(float x, float y)
    {
        var layout = Settle(new SplitLayoutSolver(), P(0, -x, -y, 1), P(1, x, y, 2));
        Vector2 difference = layout.viewports[1].centroid - layout.viewports[0].centroid;
        Check(Vector2.Dot(difference, new Vector2(x, y * Settings.screenAspect)) > 0f,
            "Two-player region did not follow world direction " + x + ", " + y);
        Check(Math.Abs(layout.viewports[0].areaFraction - 0.5f) < 0.001f, "Two-player area not half");
        Check(layout.dividers.Length == 1 && layout.dividers[0].alpha == 1f, "Two-player divider missing");
    }

    private static void Weighting(string name, params SplitLayoutSolver.PlayerInput[] input)
    {
        var layout = Settle(new SplitLayoutSolver(), input);
        float total = 0f;
        for (int i = 0; i < input.Length; i++)
        {
            float expected = 1f / input.Length;
            float actual = layout.viewports[i].areaFraction;
            Check(Math.Abs(actual - expected) < 0.035f, name + " camera " + i + " area " + actual + " expected " + expected);
            total += actual;
        }
        Check(Math.Abs(total - 1f) < 0.005f, name + " cell union does not fill the screen");
        if (name == "2+1")
        {
            Check(Math.Abs(layout.viewports[0].groupAreaFraction - 2f / 3f) < 0.035f,
                "Grouped pair did not own two-thirds of the screen");
            Check(layout.viewports[0].zoom >= (float)Math.Sqrt(2f / 3f) - 0.04f &&
                layout.viewports[0].zoom <= 1f,
                "Grouped pair zoom ignores union area or source-screen limits");
        }
    }

    private static void Continuity()
    {
        var solver = new SplitLayoutSolver();
        var merged = Settle(solver, P(0, -50f, 0f), P(1, 50f, 0f));
        Check(merged.viewports[0].zoom == 1f && merged.viewports[1].zoom == 1f, "Merged zoom not native");
        Check(merged.dividers.Length == 0, "Merged divider visible");
        var next = solver.Solve(new[] { P(0, -141f, 0f), P(1, 141f, 0f) }, 1f / 60f, Settings);
        Check(next.viewports[0].splitAmount < 0.001f, "Split jumped at merge threshold");
        var blending = solver.Solve(new[] { P(0, -160f, 0f), P(1, 160f, 0f) }, 1f / 60f, Settings);
        Check(blending.viewports[0].splitAmount > 0f && blending.viewports[0].splitAmount < 0.2f,
            "Blend is not gradual");
        Check(Math.Abs(blending.viewports[0].zoom - next.viewports[0].zoom) < 0.1f, "Zoom popped during split");
        Check(Math.Abs(blending.viewports[0].regionAnchor.x - next.viewports[0].regionAnchor.x) < 0.1f,
            "Anchor popped during split");
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
        {
            Check(Math.Abs(first.viewports[i].areaFraction - four.viewports[i].areaFraction) < 0.15f,
                "Survivor area jumped on the first death frame");
            Check(Math.Abs(three.viewports[i].areaFraction - 1f / 3f) < 0.035f, "Death area did not reflow");
        }
        Check(first.viewports[3].areaFraction < four.viewports[3].areaFraction,
            "Dead region did not start shrinking");
    }

    private static void MergeSplitSweep()
    {
        var solver = new SplitLayoutSolver();
        var previous = Settle(solver, P(0, -100f, 0f), P(1, 100f, 0f));
        float largestAnchorChange = 0f, largestZoomChange = 0f;
        for (int frame = 0; frame < 240; frame++)
        {
            float distance = frame < 120 ? 200f + frame * 2.5f : 500f - (frame - 120) * 2.5f;
            var next = solver.Solve(new[] { P(0, -distance / 2f, 0f), P(1, distance / 2f, 0f) },
                1f / 60f, Settings);
            largestAnchorChange = Math.Max(largestAnchorChange,
                (next.viewports[0].regionAnchor - previous.viewports[0].regionAnchor).magnitude);
            largestZoomChange = Math.Max(largestZoomChange,
                Math.Abs(next.viewports[0].zoom - previous.viewports[0].zoom));
            previous = next;
        }
        Check(largestAnchorChange < 0.06f, "Anchor jumped during continuous join/leave: " + largestAnchorChange);
        Check(largestZoomChange < 0.06f, "Zoom jumped during continuous join/leave: " + largestZoomChange);
        Check(previous.viewports[0].imageBlend < 0.1f, "Images did not blend back on return");
    }

    public static int Main()
    {
        try
        {
            Direction(800f, 0f);
            Direction(-800f, 0f);
            Direction(0f, 800f);
            Direction(0f, -800f);
            Direction(600f, 450f);
            Weighting("2+1", P(0, -70f, 0f), P(1, 70f, 0f), P(2, 900f, 0f, 2));
            Weighting("2+2", P(0, -1000f, 0f), P(1, -990f, 0f), P(2, 990f, 0f, 2), P(3, 1000f, 0f, 2));
            Weighting("2+1+1", P(0, -800f, -400f), P(1, -790f, -390f), P(2, 800f, -400f, 2), P(3, 0f, 700f, 3));
            Weighting("3+1", P(0, -20f, 0f), P(1, 0f, 0f), P(2, 20f, 0f), P(3, 900f, 0f, 2));
            Weighting("coincident", P(0, 0f, 0f), P(1, 0f, 0f), P(2, 900f, 0f, 2));
            Continuity();
            MergeSplitSweep();
            DeathReflow();
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
