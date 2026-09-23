using System.Collections.Generic;
using Menu.Remix.MixedUI;
using UnityEngine;

namespace SplitScreenCoop
{
    public class SplitScreenCoopOptions : OptionInterface
    {
        public readonly Configurable<bool> AlwaysSplit;
        public readonly Configurable<bool> DualDisplays;
        public readonly Configurable<string> SplitStyle;
        public readonly Configurable<float> MinZoom;
        public readonly Configurable<float> ZoomExponent;
        public readonly Configurable<float> DividerWidth;
        public readonly Configurable<float> SmoothingTime;
        public readonly Configurable<string> ZoomedFilter;
        public readonly Configurable<bool> DebugOverlay;
        public readonly Configurable<float> ExtraRealizerBudget;
        public readonly Configurable<string> CameraRendering;
        public readonly Configurable<string> SpareQuarter;
        public readonly Configurable<bool> HudTakesTurns;
        public readonly Configurable<bool> HudOntoPicture;

        // Shown in the Remix description line while the control or its label is
        // hovered: one unwrapped line at the bottom of the screen, so keep each short.
        // Labels get the text explicitly; a control bound through a Configurable shows
        // only its first sentence.
        private const string SplitStyleHelp =
            "Adaptive: one view on a shared screen, otherwise halves or a still grid. Static: a fixed region per player. Classic: the original layouts.";
        private const string SplitStyleBoxHelp = "How the screen is shared. Open the list and hover a style to read what it does.";
        // One per style, shown while the style is hovered in the open list. The Dynamic
        // style (distance-based merging) was removed on 2026-09-22; Adaptive replaced it.
        private const string AdaptiveHelp =
            "One view while everyone is on the same screen; otherwise halves (2 players) or a still grid (3-4). Nothing moves when others move.";
        private const string StaticHelp =
            "Every living player keeps a fixed region, even side by side. The regions change only when someone dies or is revived.";
        private const string ClassicHelp =
            "The original split screen: halves or quarters while players are on different screens, one view when they are together.";
        private const string SpareQuarterHelp =
            "Adaptive style, three players: what the fourth quarter shows. Map and meters: where everyone is, visited rooms " +
            "and shelters, plus the shared food, karma and rain. Meters only, or Black to leave the meters in their corner.";
        private const string AlwaysSplitHelp =
            "Give every player their own view at all times, even on the same screen. Off: players on one screen share a view (Static always splits).";
        private const string DualDisplaysHelp =
            "Player 1 on the main display, player 2 on the second. Needs two physical displays and replaces the split styles. Experimental.";
        private const string MinZoomHelp =
            "Static style: the smallest scale a region may zoom out to when Zoom exponent is above 0 (1 = native, 0.5 = half). Default 0.5.";
        private const string ZoomExponentHelp =
            "Static style: how far smaller regions zoom out to show more of the room. 0 keeps native scale and pans (recommended). Default 0.";
        private const string DividerWidthHelp =
            "Thickness of the black line between cells, in screen pixels. Default 2.";
        private const string SmoothingTimeHelp =
            "Static style: seconds a region takes to resize when a player dies or is revived. Lower is snappier. Default 0.18.";
        private const string ZoomedFilterHelp =
            "How zoomed pictures are sampled (Adaptive's grid and zooms, Static with a zoom): Bilinear is smooth, Point is crisp and blocky.";
        private const string DebugOverlayHelp =
            "Draw cell outlines, camera numbers and layout values over the game. For diagnosing layout problems; leave off to play.";
        private const string CameraRenderingHelp =
            "Alternate frames: views take turns, so Watcher grab effects (foliage, terrain, slush) are right on every view; " +
            "the frame limit rises to keep each view's speed (needs vsync off). Every frame: those effects suit one view. " +
            "Auto takes turns while each view gets 38 fps.";
        private const string ExtraRealizerBudgetHelp =
            "Rooms kept loaded for each room the players are spread over, past the first (the game keeps 1500 worth around one). " +
            "Lower it if the game lags with 3-4 players apart, raise it if entering rooms stutters. Default 750.";
        private const string HudTakesTurnsHelp =
            "While views take turns, each player's HUD redraws with its own view instead of every frame. " +
            "Less work at the raised frame limit. Turn off if a HUD flickers or lags.";
        private const string HudOntoPictureHelp =
            "Adaptive style: each HUD is drawn straight onto its view, so see-through HUD parts keep their colour and " +
            "quarters stay smooth. Turn off if a HUD is missing or wrong.";

        public SplitScreenCoopOptions()
        {
            AlwaysSplit = config.Bind("AlwaysSplit", false, new ConfigurableInfo(AlwaysSplitHelp));
            DualDisplays = config.Bind("DualDisplays", false, new ConfigurableInfo(DualDisplaysHelp));
            // Adaptive first: Remix turns a saved value it does not list into the first one,
            // so a config that still says "Dynamic" (removed) gets Adaptive, not Classic.
            SplitStyle = config.Bind("SplitStyle", "Adaptive", new ConfigurableInfo(SplitStyleHelp,
                new ConfigAcceptableList<string>("Adaptive", "Static", "Classic")));
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
            ExtraRealizerBudget = config.Bind("ExtraRealizerBudget", 750f, new ConfigurableInfo(ExtraRealizerBudgetHelp,
                new ConfigAcceptableRange<float>(0f, 1500f)));
            CameraRendering = config.Bind("CameraRendering", "Auto", new ConfigurableInfo(CameraRenderingHelp,
                new ConfigAcceptableList<string>("Auto", "Alternate frames", "Every frame")));
            SpareQuarter = config.Bind("SpareQuarter", "Map and meters", new ConfigurableInfo(SpareQuarterHelp,
                new ConfigAcceptableList<string>("Map and meters", "Meters only", "Black")));
            HudTakesTurns = config.Bind("HudTakesTurns", true, new ConfigurableInfo(HudTakesTurnsHelp));
            HudOntoPicture = config.Bind("HudOntoPicture", true, new ConfigurableInfo(HudOntoPictureHelp));
        }

        private static OpLabel Label(float x, float y, string text, string help, bool bigText = false)
        {
            return new OpLabel(x, y, text, bigText) { description = help };
        }

        private static OpLabel CheckLabel(float x, float y, string text, string help)
        {
            return new OpLabel(x, y, text) { description = help, verticalAlignment = OpLabel.LabelVAlignment.Center };
        }

        /// <summary>
        /// Remix draws a tab's elements in the order they were added (OpTab._AddItem
        /// appends each element's container), and a combo box's open list lives inside
        /// the box's own container. A list therefore opens UNDERNEATH every element
        /// added after its box: the style list slid under the two checkboxes below it.
        /// Two guards. Boxes are added last, lowest first, so an open list also covers
        /// a box beneath it; and every box made here moves itself to the front when its
        /// list opens, which keeps working wherever a future box lands in AddItems.
        /// Input needs nothing: while a list is open its box is Remix's held element
        /// and nothing else reacts to the pointer. Always create boxes through this.
        /// </summary>
        private static OpComboBox Combo(Configurable<string> config, float x, float y, float width, string[] items)
        {
            var list = new List<ListItem>(items.Length);
            for (int i = 0; i < items.Length; i++) list.Add(new ListItem(items[i], i));
            return Combo(config, x, y, width, list);
        }

        /// <summary>A box whose items may carry their own description (ListItem.desc, shown while hovered in the open list).</summary>
        private static OpComboBox Combo(Configurable<string> config, float x, float y, float width, List<ListItem> items)
        {
            var box = new OpComboBox(config, new Vector2(x, y), width, items);
            box.OnListOpen += trigger => trigger?.myContainer?.MoveToFront();
            return box;
        }

        private static ListItem Item(string name, int order, string help)
        {
            // The box sorts its items by value: the order given here is the order shown.
            return new ListItem(name, order) { desc = help };
        }

        public override void Initialize()
        {
            var general = new OpTab(this, "General");
            var dynamic = new OpTab(this, "Layout");
            Tabs = new[] { general, dynamic };
            var dual = new OpCheckBox(DualDisplays, 10f, 430f);
            dual.greyedOut = !SplitScreenCoop.DualDisplaySupported();
            general.AddItems(new UIelement[]
            {
                Label(10f, 550f, "Split-screen style", SplitStyleHelp, true),
                new OpCheckBox(AlwaysSplit, 10f, 465f),
                CheckLabel(40f, 465f, "Permanent split", AlwaysSplitHelp),
                dual,
                CheckLabel(40f, 430f, "Dual Display (experimental)", DualDisplaysHelp),
                Label(10f, 380f, "Extra rooms per player", ExtraRealizerBudgetHelp),
                new OpFloatSlider(ExtraRealizerBudget, new Vector2(225f, 373f), 190, 0),
                Label(10f, 320f, "Rendering", "Switches for the newer rendering savings. Leave them on; turn one off if its view looks wrong.", true),
                new OpCheckBox(HudTakesTurns, 10f, 280f),
                CheckLabel(40f, 280f, "HUD redraws with its view", HudTakesTurnsHelp),
                new OpCheckBox(HudOntoPicture, 10f, 245f),
                CheckLabel(40f, 245f, "HUD drawn onto its view", HudOntoPictureHelp),
                // Combo boxes last (see Combo): the open list must draw over the rows below.
                StyleBox()
            });
            dynamic.AddItems(new UIelement[]
            {
                Label(10f, 550f, "Split-screen layout", "Zoom and smoothing apply to Static; divider, filter and camera rendering to Adaptive and Static.", true),
                Label(10f, 507f, "Minimum zoom", MinZoomHelp),
                new OpFloatSlider(MinZoom, new Vector2(225f, 500f), 190, 2),
                Label(10f, 462f, "Zoom exponent", ZoomExponentHelp),
                new OpFloatSlider(ZoomExponent, new Vector2(225f, 455f), 190, 2),
                Label(10f, 417f, "Divider width", DividerWidthHelp),
                new OpFloatSlider(DividerWidth, new Vector2(225f, 410f), 190, 1),
                Label(10f, 372f, "Smoothing time", SmoothingTimeHelp),
                new OpFloatSlider(SmoothingTime, new Vector2(225f, 365f), 190, 2),
                Label(10f, 327f, "Zoomed filter", ZoomedFilterHelp),
                Label(10f, 282f, "Camera rendering", CameraRenderingHelp),
                new OpCheckBox(DebugOverlay, 10f, 230f),
                CheckLabel(40f, 230f, "Show layout debug overlay", DebugOverlayHelp),
                Label(10f, 187f, "Spare quarter (3 players)", SpareQuarterHelp),
                // Combo boxes last, lowest first (see Combo): an open list covers the rows
                // below it, the other box included.
                Combo(SpareQuarter, 225f, 180f, 145f, new[] { "Map and meters", "Meters only", "Black" }),
                Combo(CameraRendering, 225f, 275f, 145f, new[] { "Auto", "Alternate frames", "Every frame" }),
                Combo(ZoomedFilter, 225f, 320f, 145f, new[] { "Bilinear", "Point" })
            });
        }

        private OpComboBox StyleBox()
        {
            OpComboBox box = Combo(SplitStyle, 10f, 505f, 140f, new List<ListItem>
            {
                Item("Adaptive", 0, AdaptiveHelp),
                Item("Static", 1, StaticHelp),
                Item("Classic", 2, ClassicHelp)
            });
            // Hovering the closed box: what the list is for (a box built directly shows no help of its own).
            box.description = SplitStyleBoxHelp;
            return box;
        }
    }
}
