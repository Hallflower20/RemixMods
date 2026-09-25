using System;

namespace SplitScreenCoop
{
    /// <summary>
    /// Every frame of one [Perf] heartbeat window, however many there are: the window
    /// is time, not a frame count. The old 600-frame ring covered the whole 10 s at
    /// 60 fps but a quarter of it at 240 fps, so the worst frames of a window were
    /// usually not in it. Unity-free, covered by the layout tests.
    /// </summary>
    public sealed class FrameTimeWindow
    {
        private readonly float[] samples;
        private readonly float[] sorted;
        private int stored;
        private int sortedCount = -1;

        public FrameTimeWindow(int capacity)
        {
            samples = new float[Math.Max(1, capacity)];
            sorted = new float[samples.Length];
        }

        /// <summary>Frames added since the last Clear, including any past the storage cap.</summary>
        public int Frames { get; private set; }
        /// <summary>Sum of their durations.</summary>
        public double Seconds { get; private set; }
        public float Max { get; private set; }
        public float Average => Frames == 0 ? 0f : (float)(Seconds / Frames);

        public void Add(float seconds)
        {
            Frames++;
            Seconds += seconds;
            if (seconds > Max) Max = seconds;
            // Past the cap (a 10 s window at over 800 fps) percentiles come from the
            // first frames; the count, total and maximum still cover all of them.
            if (stored < samples.Length) samples[stored++] = seconds;
            sortedCount = -1;
        }

        public void Clear()
        {
            stored = 0;
            sortedCount = -1;
            Frames = 0;
            Seconds = 0;
            Max = 0f;
        }

        /// <summary>The p-quantile (0..1) of the stored frames: the value below which that fraction lies.</summary>
        public float Percentile(float p)
        {
            if (stored == 0) return 0f;
            if (sortedCount != stored)
            {
                Array.Copy(samples, sorted, stored);
                Array.Sort(sorted, 0, stored);
                sortedCount = stored;
            }
            int index = (int)Math.Floor(stored * p);
            if (index < 0) index = 0;
            if (index > stored - 1) index = stored - 1;
            return sorted[index];
        }

        /// <summary>
        /// A frame worth a [FrameHitch] line: 50 ms or more, or 25 ms and at least twice
        /// the typical frame. At 60 Hz one missed vsync (33 ms) is exactly the stutter a
        /// player sees, and so is a 20-40 ms garbage collection; the fixed 50 ms
        /// threshold logged neither.
        /// </summary>
        public static bool IsHitch(float seconds, float typical)
        {
            return seconds >= 0.05f || (seconds >= 0.025f && seconds >= 2f * typical);
        }
    }

    /// <summary>
    /// Managed allocation rate from the heap size sampled once per frame: a rise is
    /// allocation. A frame that ran a collection is skipped, since its rise is unknown,
    /// so the rate is a slight underestimate; it is for comparing before and after.
    /// </summary>
    public sealed class AllocationMeter
    {
        private long lastHeap = -1;

        /// <summary>Bytes allocated since the last Reset, over the frames that could be measured.</summary>
        public long Bytes { get; private set; }

        public void Note(long heapBytes, bool collected)
        {
            if (lastHeap >= 0 && !collected && heapBytes > lastHeap) Bytes += heapBytes - lastHeap;
            lastHeap = heapBytes;
        }

        public void Reset()
        {
            Bytes = 0;
        }
    }
}
