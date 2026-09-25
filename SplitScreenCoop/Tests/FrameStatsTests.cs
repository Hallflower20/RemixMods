using System;
using SplitScreenCoop;

internal static partial class Program
{
    private static void FrameWindowPercentiles()
    {
        var window = new FrameTimeWindow(1000);
        Check(window.Frames == 0 && window.Percentile(0.5f) == 0f && window.Max == 0f, "An empty window reports zeros");
        // 99 frames at 16 ms and one at 50 ms: the median is the typical frame, the
        // 99th percentile and the maximum are the spike.
        for (int i = 0; i < 99; i++) window.Add(0.016f);
        window.Add(0.05f);
        Check(window.Frames == 100, "Every frame is counted: " + window.Frames);
        Check(Math.Abs(window.Percentile(0.5f) - 0.016f) < 1e-6f, "Median is the typical frame: " + window.Percentile(0.5f));
        Check(Math.Abs(window.Percentile(0.99f) - 0.05f) < 1e-6f, "p99 of 100 frames is the worst one: " + window.Percentile(0.99f));
        Check(Math.Abs(window.Max - 0.05f) < 1e-6f, "Max is the spike");
        Check(Math.Abs(window.Seconds - (99 * 0.016 + 0.05)) < 1e-4, "Seconds is the sum: " + window.Seconds);
        // Order of arrival does not matter, and adding after a query re-sorts.
        window.Add(0.001f);
        Check(Math.Abs(window.Percentile(0f) - 0.001f) < 1e-6f, "Adding after a query re-sorts: " + window.Percentile(0f));
        window.Clear();
        Check(window.Frames == 0 && window.Max == 0f && window.Percentile(0.95f) == 0f, "Clear empties the window");
    }

    private static void FrameWindowCap()
    {
        // Past the storage cap the count, total and maximum still cover every frame.
        var window = new FrameTimeWindow(10);
        for (int i = 0; i < 10; i++) window.Add(0.004f);
        for (int i = 0; i < 90; i++) window.Add(0.005f);
        window.Add(0.2f);
        Check(window.Frames == 101, "Frames past the cap are counted: " + window.Frames);
        Check(Math.Abs(window.Max - 0.2f) < 1e-6f, "The maximum sees frames past the cap");
        Check(Math.Abs(window.Percentile(0.5f) - 0.004f) < 1e-6f, "Percentiles come from the stored frames");
    }

    private static void HitchRule()
    {
        const float sixty = 1f / 60f, twoForty = 1f / 240f;
        Check(!FrameTimeWindow.IsHitch(sixty, sixty), "A normal 60 fps frame is not a hitch");
        Check(FrameTimeWindow.IsHitch(2f * sixty, sixty), "One missed vsync at 60 Hz is a hitch");
        Check(!FrameTimeWindow.IsHitch(0.024f, twoForty), "Under 25 ms is never a hitch below the absolute threshold");
        Check(FrameTimeWindow.IsHitch(0.03f, twoForty), "30 ms against a 4 ms typical frame is a hitch");
        Check(!FrameTimeWindow.IsHitch(0.03f, 0.02f), "30 ms is normal when the typical frame is 20 ms");
        Check(FrameTimeWindow.IsHitch(0.05f, 0.05f), "50 ms is always a hitch");
    }

    private static void AllocationRate()
    {
        var meter = new AllocationMeter();
        meter.Note(1000, false);
        Check(meter.Bytes == 0, "The first sample is only a baseline");
        meter.Note(1500, false);
        meter.Note(1600, false);
        Check(meter.Bytes == 600, "Rises add up: " + meter.Bytes);
        meter.Note(400, true);
        Check(meter.Bytes == 600, "A frame with a collection adds nothing: " + meter.Bytes);
        meter.Note(900, false);
        Check(meter.Bytes == 1100, "Counting resumes from the post-collection heap: " + meter.Bytes);
        meter.Note(800, false);
        Check(meter.Bytes == 1100, "A fall without a collection adds nothing");
        meter.Reset();
        meter.Note(1000, false);
        Check(meter.Bytes == 200, "Reset clears the total but keeps the baseline: " + meter.Bytes);
    }
}
