using Menu.Remix.MixedUI;

namespace SplitScreenCoop
{
public class SplitScreenCoopOptions : OptionInterface
{
    public SplitScreenCoopOptions()
    {
        AlwaysSplit = this.config.Bind("AlwaysSplit", false);
        DualDisplays = this.config.Bind("DualDisplays", false);
    }

    public readonly Configurable<bool> AlwaysSplit;
    public readonly Configurable<bool> DualDisplays;
    private UIelement[] UIArrOptions;

    public override void Initialize()
    {
        var opTab = new OpTab(this, "Options");
        this.Tabs = new[] { opTab };
        OpCheckBox e;
        UIArrOptions = new UIelement[]
        {
            new OpLabel(10f, 550f, "General", true),

            new OpCheckBox(AlwaysSplit, 10f, 500),
            new OpLabel(40f, 500, "Permanent split mode") { verticalAlignment = OpLabel.LabelVAlignment.Center },

            e = new OpCheckBox(DualDisplays, 10f, 460) { description = "Requires two physical displays" },
            new OpLabel(40f, 460, "Dual Display (experimental)") { verticalAlignment = OpLabel.LabelVAlignment.Center },
        };

        e.greyedOut = !SplitScreenCoop.DualDisplaySupported();
        
        // Add items to the tab
        opTab.AddItems(UIArrOptions);
    }
}
}
