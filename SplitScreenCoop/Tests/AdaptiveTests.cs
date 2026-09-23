using System;
using System.Collections.Generic;
using SplitScreenCoop;
using UnityEngine;

internal static partial class Program
{
    private static readonly AdaptiveLayout.Settings Adaptive = new AdaptiveLayout.Settings();
    private const float Tick = 1f / 40f;
    private const float Frame = 1f / 60f;

    private static AdaptiveLayout.Player A(int camera, long key)
    {
        return new AdaptiveLayout.Player { camera = camera, screenKey = key };
    }

    /// <summary>Game time: layout decisions at the 40 Hz tick, animation at 60 fps, like the game.</summary>
    private static void Run(AdaptiveLayout layout, float seconds, AdaptiveLayout.Player[] players,
        Action<AdaptiveLayout> everyFrame = null)
    {
        float elapsed = 0f, frames = 0f;
        while (elapsed < seconds - 0.00001f)
        {
            layout.Update(players, Tick, Adaptive);
            frames += Tick;
            while (frames >= Frame - 0.00001f)
            {
                layout.Animate(Frame);
                everyFrame?.Invoke(layout);
                frames -= Frame;
            }
            elapsed += Tick;
        }
    }

    private static int VisibleCount(AdaptiveLayout layout)
    {
        int count = 0;
        foreach (var view in layout.views) if (view.visible) count++;
        return count;
    }

    private static void AdaptiveStartsFromWhereEveryoneIs()
    {
        var together = new AdaptiveLayout();
        Run(together, Tick, new[] { A(0, 5), A(1, 5) });
        Check(together.full && together.FullAtRest && together.fullCamera == 0, "Two players on one screen start in one view");
        Check(VisibleCount(together) == 1 && together.ViewOf(0).cell.Approximately(AdaptiveLayout.Box.Full),
            "Only the full view is drawn");

        var apart = new AdaptiveLayout();
        Run(apart, Tick, new[] { A(1, 5), A(0, 6) });
        Check(!apart.full && apart.shape == AdaptiveLayout.Shape.Halves, "Two players on two screens start split");
        Check(apart.ViewOf(0).cell.Approximately(AdaptiveLayout.Slot(AdaptiveLayout.Shape.Halves, 0)) &&
              apart.ViewOf(1).cell.Approximately(AdaptiveLayout.Slot(AdaptiveLayout.Shape.Halves, 1)),
            "The lower camera takes the left half whatever the input order");
        Check(apart.ViewOf(0).zoom == 1f && apart.ViewOf(0).native, "Halves are native scale");
        Check(Mathf.Abs(apart.ViewOf(0).window.x - 0f) < 0.0001f && Mathf.Abs(apart.ViewOf(1).window.x - 0.5f) < 0.0001f,
            "Without framing a half shows its own half of the screen");
    }

    private static void AdaptiveMergeWaitsForTheGroup()
    {
        var layout = new AdaptiveLayout();
        Run(layout, 0.5f, new[] { A(0, 1), A(1, 2) });
        Run(layout, Adaptive.mergeDelay - 0.1f, new[] { A(0, 1), A(1, 1) });
        Check(!layout.full, "No merge before the group has been together for the merge delay");
        Run(layout, 0.2f, new[] { A(0, 1), A(1, 1) });
        Check(layout.full, "Merged once together for the merge delay");
        Run(layout, Adaptive.zoomSeconds + 0.05f, new[] { A(0, 1), A(1, 1) });
        Check(layout.FullAtRest && VisibleCount(layout) == 1, "After the zoom only the full view is drawn");
    }

    private static void AdaptiveSplitGraceAndGroupCrossing()
    {
        // A player off the shared screen for less than the grace: nothing happens.
        var layout = new AdaptiveLayout();
        Run(layout, 0.5f, new[] { A(0, 1), A(1, 1) });
        bool splitSeen = false;
        Run(layout, Adaptive.splitGrace - 0.1f, new[] { A(0, 1), A(1, 2) }, l => splitSeen |= !l.full);
        Run(layout, 1f, new[] { A(0, 1), A(1, 1) }, l => splitSeen |= !l.full);
        Check(!splitSeen, "A short visit to the next screen does not split the view");

        // The whole group crossing a screen boundary within the grace: one view throughout.
        Run(layout, 0.3f, new[] { A(0, 3), A(1, 1) }, l => splitSeen |= !l.full);
        Run(layout, 1f, new[] { A(0, 3), A(1, 3) }, l => splitSeen |= !l.full);
        Check(!splitSeen, "A group crossing a boundary together stays in one view");
        Check(layout.fullCamera == 0, "The full view followed the first player across");

        // Staying apart: split once the grace has run out.
        Run(layout, Adaptive.splitGrace + 0.1f, new[] { A(0, 3), A(1, 4) });
        Check(!layout.full, "Apart for longer than the grace splits");
    }

    private static void AdaptiveRemergeCooldown()
    {
        var layout = new AdaptiveLayout();
        Run(layout, 0.5f, new[] { A(0, 1), A(1, 1) });
        Run(layout, Adaptive.splitGrace + 0.05f, new[] { A(0, 1), A(1, 2) });
        Check(!layout.full, "Split");
        Run(layout, Adaptive.remergeCooldown - 0.7f, new[] { A(0, 1), A(1, 1) });
        Check(!layout.full, "Together again straight after a split: no merge during the cooldown");
        Run(layout, 0.8f, new[] { A(0, 1), A(1, 1) });
        Check(layout.full, "Merges once the cooldown and the merge delay have both passed");
    }

    private static void AdaptiveGridSlots()
    {
        var four = new AdaptiveLayout();
        Run(four, Tick, new[] { A(3, 4), A(2, 3), A(1, 2), A(0, 1) });
        Check(!four.full && four.shape == AdaptiveLayout.Shape.Grid && four.spare == null, "Four apart: grid, no spare");
        for (int camera = 0; camera < 4; camera++)
        {
            var view = four.ViewOf(camera);
            Check(view.cell.Approximately(AdaptiveLayout.Slot(AdaptiveLayout.Shape.Grid, camera)), "Quarter by rank for camera " + camera);
            Check(view.zoom == 0.5f && view.window.sqrMagnitude < 0.000001f, "A quarter shows the whole screen at half size");
        }
        var three = new AdaptiveLayout();
        Run(three, Tick, new[] { A(0, 1), A(2, 3), A(3, 4) });
        Check(three.spare != null && three.spare.visible &&
              three.spare.cell.Approximately(AdaptiveLayout.Slot(AdaptiveLayout.Shape.Grid, 3)), "Three apart: spare bottom right");
        Check(three.ViewOf(3).cell.Approximately(AdaptiveLayout.Slot(AdaptiveLayout.Shape.Grid, 2)), "Ranks, not camera numbers, pick quarters");
    }

    private static void AdaptivePartialGroupsNeverMoveAnything()
    {
        // The property that answers "three and four players were jarring": as long as
        // not everyone is on one screen, no view moves, whatever the groups do.
        var layout = new AdaptiveLayout();
        Run(layout, Tick, new[] { A(0, 1), A(1, 2), A(2, 3), A(3, 4) });
        var rest = new AdaptiveLayout.Box[4];
        for (int camera = 0; camera < 4; camera++) rest[camera] = layout.ViewOf(camera).cell;
        bool moved = false;
        Action<AdaptiveLayout> watch = l =>
        {
            for (int camera = 0; camera < 4; camera++)
                if (!l.ViewOf(camera).cell.Approximately(rest[camera]) || !l.ViewOf(camera).visible) moved = true;
        };
        var groupings = new[]
        {
            new[] { A(0, 1), A(1, 1), A(2, 3), A(3, 4) },
            new[] { A(0, 1), A(1, 1), A(2, 1), A(3, 4) },
            new[] { A(0, 2), A(1, 2), A(2, 5), A(3, 5) },
            new[] { A(0, 7), A(1, 1), A(2, 7), A(3, 7) },
        };
        for (int round = 0; round < 6; round++)
            foreach (var grouping in groupings) Run(layout, 2.5f, grouping, watch);
        Check(!moved && !layout.full, "Partial groups must never move, hide or merge a view");
    }

    private static void AdaptiveGridZoomIsAPureScale()
    {
        var layout = new AdaptiveLayout();
        Run(layout, 0.5f, new[] { A(0, 1), A(1, 2), A(2, 3), A(3, 4) });
        bool bad = false;
        float worst = 0f;
        Action<AdaptiveLayout> watch = l =>
        {
            var anchor = l.ViewOf(0);
            if (!anchor.visible) { bad = true; return; }
            // Whole render at every frame: window at the origin, cell/zoom == 1.
            float spanX = anchor.cell.w / anchor.zoom, spanY = anchor.cell.h / anchor.zoom;
            worst = Mathf.Max(worst, Mathf.Max(Mathf.Abs(spanX - 1f), Mathf.Abs(spanY - 1f)));
            if (anchor.window.sqrMagnitude > 0.000001f) bad = true;
            // Grows out of its own corner: the top-left edges stay put.
            if (Mathf.Abs(anchor.cell.x) > 0.0001f || Mathf.Abs(anchor.cell.yMax - 1f) > 0.0001f) bad = true;
        };
        Run(layout, Adaptive.mergeDelay + Adaptive.zoomSeconds + 0.2f, new[] { A(0, 9), A(1, 9), A(2, 9), A(3, 9) }, watch);
        Check(layout.FullAtRest && layout.fullCamera == 0, "Four together end in one view owned by the lowest camera");
        Check(!bad && worst < 0.001f, "The merge zoom shows the whole render and grows from the corner: " + worst);
        bad = false; worst = 0f;
        Run(layout, Adaptive.splitGrace + Adaptive.zoomSeconds + 0.2f, new[] { A(0, 9), A(1, 9), A(2, 9), A(3, 8) }, watch);
        Check(!layout.full && !bad && worst < 0.001f, "The split zoom is the same scale in reverse: " + worst);
    }

    private static void AdaptiveHalvesInPlaceMergeIsStill()
    {
        // Both halves framed in place on one picture: the growing half's mapping of the
        // screen never changes, i.e. nothing inside it moves.
        var layout = new AdaptiveLayout();
        Run(layout, 0.5f, new[] { A(0, 1), A(1, 2) });
        bool moved = false;
        Action<AdaptiveLayout> watch = l =>
        {
            var anchor = l.ViewOf(0);
            if (!anchor.visible) return;
            Vector2 probe = anchor.cell.Center;
            Vector2 uv = anchor.window + (probe - anchor.cell.Min) / anchor.zoom;
            if ((uv - probe).magnitude > 0.0005f) moved = true;
        };
        Run(layout, Adaptive.mergeDelay + Adaptive.zoomSeconds + 0.2f, new[] { A(0, 1), A(1, 1) }, watch);
        Check(layout.FullAtRest && layout.fullCamera == 0 && !moved, "An in-place half widens to full without moving its picture");
    }

    private static void AdaptiveNothingJumps()
    {
        var layout = new AdaptiveLayout();
        float worst = 0f;
        string where = "";
        int phase = 0;
        var last = new Dictionary<int, AdaptiveLayout.Box>();
        Action<AdaptiveLayout> watch = l =>
        {
            // A view that died is gone; a revived player's view is a new one that grows
            // from a point, so it has no previous box to jump from.
            var present = new List<int>();
            foreach (var view in l.views) if (view.visible) present.Add(view.camera);
            foreach (int camera in new List<int>(last.Keys)) if (!present.Contains(camera)) last.Remove(camera);
            foreach (var view in l.views)
            {
                if (!view.visible) continue;
                AdaptiveLayout.Box previous;
                if (last.TryGetValue(view.camera, out previous))
                {
                    float jump = Mathf.Max(Mathf.Max(Mathf.Abs(view.cell.x - previous.x), Mathf.Abs(view.cell.y - previous.y)),
                        Mathf.Max(Mathf.Abs(view.cell.xMax - previous.xMax), Mathf.Abs(view.cell.yMax - previous.yMax)));
                    if (jump > worst) { worst = jump; where = $"phase {phase} camera {view.camera} {previous} -> {view.cell}"; }
                }
                last[view.camera] = view.cell;
            }
        };
        phase = 1; Run(layout, 0.5f, new[] { A(0, 1), A(1, 2), A(2, 3) }, watch);
        phase = 2; Run(layout, 2.5f, new[] { A(0, 1), A(1, 1), A(2, 1) }, watch);
        phase = 3; Run(layout, 2.5f, new[] { A(0, 1), A(1, 1), A(2, 4) }, watch);
        phase = 4; Run(layout, 1.5f, new[] { A(0, 1), A(2, 4) }, watch);
        phase = 5; Run(layout, 1.5f, new[] { A(0, 1), A(1, 5), A(2, 4) }, watch);
        phase = 6; Run(layout, 1.5f, new[] { A(0, 1), A(1, 5), A(2, 4), A(3, 6) }, watch);
        phase = 7; Run(layout, 1.5f, new[] { A(2, 4) }, watch);
        // Smoothstep over 0.35 s peaks at 1.5 / 0.35 of the distance per second: a
        // quarter (0.5) moves at most ~0.036 of the screen per 60 fps frame.
        Check(worst < 0.06f, "No visible cell edge may jump in one frame: " + worst + " at " + where);
    }

    private static void AdaptiveReflow()
    {
        var layout = new AdaptiveLayout();
        Run(layout, 0.5f, new[] { A(0, 1), A(1, 2), A(2, 3), A(3, 4) });
        Run(layout, Adaptive.reflowSeconds + 0.1f, new[] { A(0, 1), A(2, 3), A(3, 4) });
        Check(layout.shape == AdaptiveLayout.Shape.Grid && layout.spare != null && layout.spare.visible, "Four to three: grid with the spare");
        Check(layout.ViewOf(2).cell.Approximately(AdaptiveLayout.Slot(AdaptiveLayout.Shape.Grid, 1)) &&
              layout.ViewOf(3).cell.Approximately(AdaptiveLayout.Slot(AdaptiveLayout.Shape.Grid, 2)), "Survivors reflow by rank");
        Run(layout, Adaptive.reflowSeconds + 0.1f, new[] { A(0, 1), A(3, 4) });
        Check(layout.shape == AdaptiveLayout.Shape.Halves && layout.spare == null &&
              layout.ViewOf(3).cell.Approximately(AdaptiveLayout.Slot(AdaptiveLayout.Shape.Halves, 1)), "Three to two: halves");
        Run(layout, Adaptive.reflowSeconds + 0.1f, new[] { A(3, 4) });
        Check(layout.FullAtRest && layout.fullCamera == 3, "A single survivor fills the screen");
        Run(layout, 0.3f, new[] { A(0, 1), A(3, 4) });
        Check(layout.full, "A revival on another screen first keeps the one view");
        Run(layout, Adaptive.splitGrace + Adaptive.zoomSeconds + 0.1f, new[] { A(0, 1), A(3, 4) });
        Check(!layout.full && layout.shape == AdaptiveLayout.Shape.Halves && VisibleCount(layout) == 2, "then splits into halves");
    }

    private static void AdaptiveNobodyAliveKeepsTheLayout()
    {
        // Game over: the game passes no players at all (it no longer substitutes camera 0),
        // and the last death must stay on screen exactly as it was.
        var layout = new AdaptiveLayout();
        Run(layout, 0.5f, new[] { A(0, 1), A(1, 2), A(2, 3) });
        Run(layout, Adaptive.reflowSeconds + 0.1f, new[] { A(2, 3) });
        Check(layout.FullAtRest && layout.fullCamera == 2, "The last survivor fills the screen");
        var cell = layout.ViewOf(2).cell;
        int visible = VisibleCount(layout);
        Run(layout, 3f, new AdaptiveLayout.Player[0]);
        Check(layout.lastEvent == "" && layout.FullAtRest && layout.fullCamera == 2 && layout.ViewOf(2) != null &&
              layout.ViewOf(2).cell.Approximately(cell) && VisibleCount(layout) == visible,
            "With nobody alive nothing moves: no reflow to camera 0, no event");
        Check(layout.ViewOf(0) == null, "No view for camera 0 appears");

        // The last two die at once, mid-grid: the grid stays as it was.
        var grid = new AdaptiveLayout();
        Run(grid, 0.5f, new[] { A(0, 1), A(1, 2), A(3, 4) });
        var before = grid.ViewOf(3).cell;
        Run(grid, 1f, new AdaptiveLayout.Player[0]);
        Check(!grid.full && grid.shape == AdaptiveLayout.Shape.Grid && grid.ViewOf(3).cell.Approximately(before),
            "A grid with nobody left alive keeps its cells");
    }

    private static void AdaptiveFullViewStaysWithTheGroup()
    {
        var layout = new AdaptiveLayout();
        Run(layout, 0.5f, new[] { A(0, 1), A(1, 1), A(2, 1) });
        Check(layout.fullCamera == 0, "Owner is the lowest camera");
        var box = layout.ViewOf(0).cell;
        Run(layout, Tick, new[] { A(0, 2), A(1, 1), A(2, 1) });
        Check(layout.full && layout.fullCamera == 1 && layout.ViewOf(1).cell.Approximately(box) && VisibleCount(layout) == 1,
            "When the owner leaves, the full view is handed to a camera still on the group's screen, unchanged");
    }

    private static void AdaptiveFraming()
    {
        const float w = 0.5f, m = 0.22f;
        Check(AdaptiveLayout.StepFraming(0f, 0.2f, w, m) == 0f, "Inside the margins nothing moves");
        Check(AdaptiveLayout.StepFraming(0f, 0.40f, w, m) == 0.25f, "Near the right edge: one step right");
        Check(AdaptiveLayout.StepFraming(0.25f, 0.40f, w, m) == 0.25f, "No step straight back");
        Check(AdaptiveLayout.StepFraming(0.25f, 0.66f, w, m) == 0.5f, "Next step right");
        Check(AdaptiveLayout.StepFraming(0.5f, 0.60f, w, m) == 0.25f, "Back left near the left edge");
        Check(AdaptiveLayout.StepFraming(0f, 0.02f, w, m) == 0f, "No step past the screen's edge");
        Check(AdaptiveLayout.StepFraming(0.5f, 0.05f, w, m) == 0f, "Far away: straight to the best step");
        Check(AdaptiveLayout.StepFraming(0.25f, float.NaN, w, m) == 0.25f, "No position: keep the window");
        Check(AdaptiveLayout.BestFraming(0.1f, w) == 0f && AdaptiveLayout.BestFraming(0.5f, w) == 0.25f &&
              AdaptiveLayout.BestFraming(0.9f, w) == 0.5f, "Best steps");
        // Sweeping across the whole screen and back steps at most twice each way.
        float current = 0f; int steps = 0;
        for (float x = 0f; x <= 1f; x += 0.005f)
        {
            float next = AdaptiveLayout.StepFraming(current, x, w, m);
            if (next != current) steps++;
            current = next;
        }
        for (float x = 1f; x >= 0f; x -= 0.005f)
        {
            float next = AdaptiveLayout.StepFraming(current, x, w, m);
            if (next != current) steps++;
            current = next;
        }
        Check(steps == 4, "A full sweep there and back takes four steps: " + steps);

        // A step eases over stepSeconds, and never starts during a zoom.
        var layout = new AdaptiveLayout();
        Run(layout, 0.5f, new[] { A(0, 1), A(1, 2) });
        var left = layout.ViewOf(0);
        layout.SetFraming(0, 0.25f, false, Adaptive);
        layout.Animate(Adaptive.stepSeconds / 2f);
        Check(left.window.x > 0.01f && left.window.x < 0.24f, "Half way through a step: " + left.window.x);
        layout.Animate(Adaptive.stepSeconds);
        Check(Mathf.Abs(left.window.x - 0.25f) < 0.0001f, "Step completes");
        layout.SetFraming(0, 0f, true, Adaptive);
        Check(left.window.x == 0f, "A snap applies at once");
    }
}
