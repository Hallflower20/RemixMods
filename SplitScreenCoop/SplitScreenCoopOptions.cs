using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using Menu.Remix.MixedUI;
using Menu.Remix.MixedUI.ValueTypes;
using UnityEngine;

namespace SplitScreenCoop
{
public class SplitScreenCoopOptions : OptionInterface
{
    class BetterComboBox : OpComboBox
    {
        public BetterComboBox(ConfigurableBase configBase, Vector2 pos, float width, List<ListItem> list) : base(configBase, pos, width, list) { }
        public override void GrafUpdate(float timeStacker)
        {
            base.GrafUpdate(timeStacker);
            if(this._rectList != null && !_rectList.isHidden)
            {
                for (int j = 0; j < 9; j++)
                {
                    this._rectList.sprites[j].alpha = 1;
                }
            }
        }
    }
    public SplitScreenCoopOptions()
    {
        PreferredSplitMode = this.config.Bind("PreferredSplitMode", SplitScreenCoop.SplitMode.SplitVertical);
        AlwaysSplit = this.config.Bind("AlwaysSplit", false);
        DualDisplays = this.config.Bind("DualDisplays", false);
        Player2Class = this.config.Bind("Player2Class", "Campaign");
        Player2BodyColor = this.config.Bind("Player2BodyColor", new Color(0.85f, 0.2f, 0.2f));
        Player2FaceColor = this.config.Bind("Player2FaceColor", Color.white);
        Player2AccentColor = this.config.Bind("Player2AccentColor", new Color(1f, 0.8f, 0.15f));
        if (PreferredSplitMode.Value != SplitScreenCoop.SplitMode.NoSplit &&
            PreferredSplitMode.Value != SplitScreenCoop.SplitMode.SplitVertical)
            PreferredSplitMode.Value = SplitScreenCoop.SplitMode.SplitVertical;
    }

    public readonly Configurable<SplitScreenCoop.SplitMode> PreferredSplitMode;
    public readonly Configurable<bool> AlwaysSplit;
    public readonly Configurable<bool> DualDisplays;
    public readonly Configurable<string> Player2Class;
    public readonly Configurable<Color> Player2BodyColor;
    public readonly Configurable<Color> Player2FaceColor;
    public readonly Configurable<Color> Player2AccentColor;
    private UIelement[] UIArrOptions;

    public override void Initialize()
    {
        var opTab = new OpTab(this, "Options");
        this.Tabs = new[] { opTab };
        OpCheckBox e;
        UIArrOptions = new UIelement[]
        {
            new OpLabel(10f, 550f, "General", true),

            new OpCheckBox(AlwaysSplit, 10f, 450),
            new OpLabel(40f, 450, "Permanent split mode") { verticalAlignment = OpLabel.LabelVAlignment.Center },

            e = new OpCheckBox(DualDisplays, 10f, 410) { description = "Requires two physical displays" },
            new OpLabel(40f, 410, "Dual Display (experimental)") { verticalAlignment = OpLabel.LabelVAlignment.Center },
            
            // added last due to overlap
            new OpLabel(10f, 520, "Split Mode") { verticalAlignment = OpLabel.LabelVAlignment.Center },
            new BetterComboBox(PreferredSplitMode, new Vector2(10f, 490), 200f, new List<ListItem>
            {
                new ListItem("NoSplit", 0), new ListItem("SplitVertical", 1)
            }),

            new OpLabel(320f, 550f, "Player 2", true),
            new OpLabel(320f, 520f, "Class"),
            new BetterComboBox(Player2Class, new Vector2(320f, 490f), 230f, new List<ListItem>
            {
                new ListItem("Campaign", 0), new ListItem("White", 1), new ListItem("Yellow", 2),
                new ListItem("Red", 3), new ListItem("Night", 4), new ListItem("Gourmand", 5),
                new ListItem("Artificer", 6), new ListItem("Rivulet", 7), new ListItem("Spear", 8),
                new ListItem("Saint", 9), new ListItem("Watcher", 10)
            }),
            new OpLabel(250f, 455f, "Body"),
            new OpColorPicker(Player2BodyColor, new Vector2(250f, 285f)),
            new OpLabel(415f, 455f, "Face"),
            new OpColorPicker(Player2FaceColor, new Vector2(415f, 285f)),
            new OpLabel(330f, 260f, "Accent"),
            new OpColorPicker(Player2AccentColor, new Vector2(330f, 90f)),
        };

        e.greyedOut = !SplitScreenCoop.DualDisplaySupported();
        
        // Add items to the tab
        opTab.AddItems(UIArrOptions);
    }
}
}
