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

        // Shown in the Remix description box while the control or its label is
        // hovered. Remix reads a Configurable's info.description into the widget
        // by itself; labels get the same text explicitly.
        private const string SplitStyleHelp =
            "Dynamic: everyone shares one full-screen view; players who move apart get their own cell " +
            "(player 1 left/top, then by player number) and merge back into one image when they come close again. " +
            "Classic: the original always-split layouts (1 left, 2 right; a triangle for three; corners for four).";
        private const string AlwaysSplitHelp =
            "Give every player their own cell at all times, even when standing together. " +
            "Off: players close together on one camera screen share a single full-size image.";
        private const string DualDisplaysHelp =
            "Player 1 on the main display, player 2 on the second. Requires two physical displays " +
            "and bypasses the Dynamic layouts. Experimental.";
        private const string MergeDistanceHelp =
            "How close two players on the same camera screen must be, in world pixels (a room screen is about 1400 wide), " +
            "before their cells merge into one image. They split again once they are this far apart plus the blend width. Default 600.";
        private const string BlendWidthHelp =
            "Extra distance past the merge distance over which a merging or parting pair glides together or apart " +
            "while the divider fades. Larger is softer and slower. Default 250.";
        private const string MinZoomHelp =
            "Smallest scale a cell may zoom out to when Zoom exponent is above 0 (1 = native pixels, 0.5 = half size). " +
            "A cell can never show more than one room screen. Default 0.5.";
        private const string ZoomExponentHelp =
            "How much smaller cells zoom out to show more of the room. 0 keeps every cell at native scale and pans to keep " +
            "its player in view (recommended). Higher values shrink the picture in half and quarter cells. Default 0.";
        private const string DividerWidthHelp =
            "Thickness of the black line between cells, in screen pixels. Default 2.";
        private const string SmoothingTimeHelp =
            "Seconds a cell takes to resize when a player dies or a merged group changes size. Lower is snappier. Default 0.18.";
        private const string ZoomedFilterHelp =
            "How zoomed-out camera images are sampled: Bilinear smooths the pixels, Point keeps them crisp and blocky. " +
            "Only matters when Zoom exponent is above 0.";
        private const string DebugOverlayHelp =
            "Draw cell outlines, camera numbers and layout values over the game. For diagnosing layout problems; leave off to play.";

        public SplitScreenCoopOptions()
        {
            AlwaysSplit = config.Bind("AlwaysSplit", false, new ConfigurableInfo(AlwaysSplitHelp));
            DualDisplays = config.Bind("DualDisplays", false, new ConfigurableInfo(DualDisplaysHelp));
            SplitStyle = config.Bind("SplitStyle", "Dynamic", new ConfigurableInfo(SplitStyleHelp,
                new ConfigAcceptableList<string>("Classic", "Dynamic")));
            MergeDistance = config.Bind("MergeDistance", 600f, new ConfigurableInfo(MergeDistanceHelp,
                new ConfigAcceptableRange<float>(100f, 1400f)));
            BlendWidth = config.Bind("BlendWidth", 250f, new ConfigurableInfo(BlendWidthHelp,
                new ConfigAcceptableRange<float>(20f, 800f)));
            // Migrate earlier defaults so existing installs pick up the current ones
            // rather than a value a previous build wrote into the config file.
            if (Mathf.Approximately(MergeDistance.Value, 280f) || Mathf.Approximately(MergeDistance.Value, 850f)) MergeDistance.Value = 600f;
            if (Mathf.Approximately(BlendWidth.Value, 200f) || Mathf.Approximately(BlendWidth.Value, 300f)) BlendWidth.Value = 250f;
            MinZoom = config.Bind("MinZoom", 0.5f, new ConfigurableInfo(MinZoomHelp,
                new ConfigAcceptableRange<float>(0.3f, 1f)));
            // New key: the old "ZoomExponent" default of 0.5 zoomed three- and
            // four-player cells out to half scale, which made following pointless.
            // Native scale with panning is the baseline; raise this for a wider view.
            ZoomExponent = config.Bind("ViewZoomExponent", 0f, new ConfigurableInfo(ZoomExponentHelp,
                new ConfigAcceptableRange<float>(0f, 1.5f)));
            DividerWidth = config.Bind("DividerWidth", 2f, new ConfigurableInfo(DividerWidthHelp,
                new ConfigAcceptableRange<float>(0.5f, 8f)));
            SmoothingTime = config.Bind("SmoothingTime", 0.18f, new ConfigurableInfo(SmoothingTimeHelp,
                new ConfigAcceptableRange<float>(0.02f, 1f)));
            ZoomedFilter = config.Bind("ZoomedFilter", "Bilinear", new ConfigurableInfo(ZoomedFilterHelp,
                new ConfigAcceptableList<string>("Bilinear", "Point")));
            DebugOverlay = config.Bind("DebugOverlay", false, new ConfigurableInfo(DebugOverlayHelp));
        }

        private static OpLabel Label(float x, float y, string text, string help, bool bigText = false)
        {
            return new OpLabel(x, y, text, bigText) { description = help };
        }

        private static OpLabel CheckLabel(float x, float y, string text, string help)
        {
            return new OpLabel(x, y, text) { description = help, verticalAlignment = OpLabel.LabelVAlignment.Center };
        }

        public override void Initialize()
        {
            var general = new OpTab(this, "General");
            var dynamic = new OpTab(this, "Dynamic");
            Tabs = new[] { general, dynamic };
            var dual = new OpCheckBox(DualDisplays, 10f, 430f);
            dual.greyedOut = !SplitScreenCoop.DualDisplaySupported();
            general.AddItems(new UIelement[]
            {
                Label(10f, 550f, "Split-screen style", SplitStyleHelp, true),
                new OpComboBox(SplitStyle, new Vector2(10f, 505f), 140f,
                    new[] { "Dynamic", "Classic" }),
                new OpCheckBox(AlwaysSplit, 10f, 465f),
                CheckLabel(40f, 465f, "Permanent split", AlwaysSplitHelp),
                dual,
                CheckLabel(40f, 430f, "Dual Display (experimental)", DualDisplaysHelp)
            });
            dynamic.AddItems(new UIelement[]
            {
                Label(10f, 550f, "Distance, area and zoom", "Settings for the Dynamic split-screen style. Hover a setting for what it does.", true),
                Label(10f, 507f, "Merge distance", MergeDistanceHelp),
                new OpFloatSlider(MergeDistance, new Vector2(225f, 500f), 190, 0),
                Label(10f, 462f, "Blend width", BlendWidthHelp),
                new OpFloatSlider(BlendWidth, new Vector2(225f, 455f), 190, 0),
                Label(10f, 417f, "Minimum zoom", MinZoomHelp),
                new OpFloatSlider(MinZoom, new Vector2(225f, 410f), 190, 2),
                Label(10f, 372f, "Zoom exponent", ZoomExponentHelp),
                new OpFloatSlider(ZoomExponent, new Vector2(225f, 365f), 190, 2),
                Label(10f, 327f, "Divider width", DividerWidthHelp),
                new OpFloatSlider(DividerWidth, new Vector2(225f, 320f), 190, 1),
                Label(10f, 282f, "Smoothing time", SmoothingTimeHelp),
                new OpFloatSlider(SmoothingTime, new Vector2(225f, 275f), 190, 2),
                Label(10f, 237f, "Zoomed filter", ZoomedFilterHelp),
                new OpComboBox(ZoomedFilter, new Vector2(225f, 230f), 145f,
                    new[] { "Bilinear", "Point" }),
                new OpCheckBox(DebugOverlay, 10f, 175f),
                CheckLabel(40f, 175f, "Show layout debug overlay", DebugOverlayHelp)
            });
        }
    }
}
