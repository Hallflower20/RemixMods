using Menu.Remix.MixedUI;
using UnityEngine;

namespace SplitScreenCoop
{
    public class SplitScreenCoopOptions : OptionInterface
    {
        public readonly Configurable<bool> AlwaysSplit;
        public readonly Configurable<bool> DualDisplays;
        public readonly Configurable<string> SplitStyle;
        public readonly Configurable<float> MergeDistance;
        public readonly Configurable<float> BlendWidth;
        public readonly Configurable<float> MinZoom;
        public readonly Configurable<float> ZoomExponent;
        public readonly Configurable<float> DividerWidth;
        public readonly Configurable<float> SmoothingTime;
        public readonly Configurable<string> ZoomedFilter;
        public readonly Configurable<bool> DebugOverlay;

        public SplitScreenCoopOptions()
        {
            AlwaysSplit = config.Bind("AlwaysSplit", false);
            DualDisplays = config.Bind("DualDisplays", false);
            SplitStyle = config.Bind("SplitStyle", "Dynamic", new ConfigurableInfo(
                "Dynamic rotates and weights regions; Classic keeps the original fixed layouts.",
                new ConfigAcceptableList<string>("Classic", "Dynamic")));
            MergeDistance = config.Bind("MergeDistance", 600f, new ConfigurableInfo(
                "Players closer than this on the same camera screen share one image.",
                new ConfigAcceptableRange<float>(100f, 1400f)));
            BlendWidth = config.Bind("BlendWidth", 250f, new ConfigurableInfo(
                "Distance over which the divider fades and images blend.",
                new ConfigAcceptableRange<float>(20f, 800f)));
            // Migrate earlier defaults so existing installs pick up the current ones
            // rather than a value a previous build wrote into the config file.
            if (Mathf.Approximately(MergeDistance.Value, 280f) || Mathf.Approximately(MergeDistance.Value, 850f)) MergeDistance.Value = 600f;
            if (Mathf.Approximately(BlendWidth.Value, 200f) || Mathf.Approximately(BlendWidth.Value, 300f)) BlendWidth.Value = 250f;
            MinZoom = config.Bind("MinZoom", 0.5f, new ConfigurableInfo(
                "Smallest view zoom, still limited by the one-screen source image.",
                new ConfigAcceptableRange<float>(0.3f, 1f)));
            // New key: the old "ZoomExponent" default of 0.5 zoomed three- and
            // four-player cells out to half scale, which made following pointless.
            // Native scale with panning is the baseline; raise this for a wider view.
            ZoomExponent = config.Bind("ViewZoomExponent", 0f, new ConfigurableInfo(
                "How strongly smaller cells zoom out. 0 keeps every view at native scale and pans to follow.",
                new ConfigAcceptableRange<float>(0f, 1.5f)));
            DividerWidth = config.Bind("DividerWidth", 2f, new ConfigurableInfo(
                "Divider width in display pixels.", new ConfigAcceptableRange<float>(0.5f, 8f)));
            SmoothingTime = config.Bind("SmoothingTime", 0.18f, new ConfigurableInfo(
                "Response time for region movement and weighting.",
                new ConfigAcceptableRange<float>(0.02f, 1f)));
            ZoomedFilter = config.Bind("ZoomedFilter", "Bilinear", new ConfigurableInfo(
                "Texture sampling for zoomed-out camera images.",
                new ConfigAcceptableList<string>("Bilinear", "Point")));
            DebugOverlay = config.Bind("DebugOverlay", false);
        }

        public override void Initialize()
        {
            var general = new OpTab(this, "General");
            var dynamic = new OpTab(this, "Dynamic");
            Tabs = new[] { general, dynamic };
            var dual = new OpCheckBox(DualDisplays, 10f, 430f)
            { description = "Requires two physical displays and bypasses Dynamic layouts." };
            dual.greyedOut = !SplitScreenCoop.DualDisplaySupported();
            general.AddItems(new UIelement[]
            {
                new OpLabel(10f, 550f, "Split-screen style", true),
                new OpComboBox(SplitStyle, new Vector2(10f, 505f), 140f,
                    new[] { "Dynamic", "Classic" }),
                new OpCheckBox(AlwaysSplit, 10f, 465f),
                new OpLabel(40f, 465f, "Permanent split")
                    { verticalAlignment = OpLabel.LabelVAlignment.Center },
                dual,
                new OpLabel(40f, 430f, "Dual Display (experimental)")
                    { verticalAlignment = OpLabel.LabelVAlignment.Center }
            });
            dynamic.AddItems(new UIelement[]
            {
                new OpLabel(10f, 550f, "Distance, area and zoom", true),
                new OpLabel(10f, 507f, "Merge distance"),
                new OpFloatSlider(MergeDistance, new Vector2(225f, 500f), 190, 0),
                new OpLabel(10f, 462f, "Blend width"),
                new OpFloatSlider(BlendWidth, new Vector2(225f, 455f), 190, 0),
                new OpLabel(10f, 417f, "Minimum zoom"),
                new OpFloatSlider(MinZoom, new Vector2(225f, 410f), 190, 2),
                new OpLabel(10f, 372f, "Zoom exponent"),
                new OpFloatSlider(ZoomExponent, new Vector2(225f, 365f), 190, 2),
                new OpLabel(10f, 327f, "Divider width"),
                new OpFloatSlider(DividerWidth, new Vector2(225f, 320f), 190, 1),
                new OpLabel(10f, 282f, "Smoothing time"),
                new OpFloatSlider(SmoothingTime, new Vector2(225f, 275f), 190, 2),
                new OpLabel(10f, 237f, "Zoomed filter"),
                new OpComboBox(ZoomedFilter, new Vector2(225f, 230f), 145f,
                    new[] { "Bilinear", "Point" }),
                new OpCheckBox(DebugOverlay, 10f, 175f),
                new OpLabel(40f, 175f, "Show layout debug overlay")
                    { verticalAlignment = OpLabel.LabelVAlignment.Center }
            });
        }
    }
}
