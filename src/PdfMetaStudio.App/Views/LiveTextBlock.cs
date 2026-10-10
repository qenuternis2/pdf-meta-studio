using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace PdfMetaStudio.App.Views;

// LiveSetting describes the region; WPF also needs an event for text updates.
public sealed class LiveTextBlock : TextBlock
{
    static LiveTextBlock() => TextProperty.OverrideMetadata(typeof(LiveTextBlock),
        new FrameworkPropertyMetadata(OnTextChanged));

    private static void OnTextChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var region = (LiveTextBlock)sender;
        if (region.IsLoaded &&
            AutomationProperties.GetLiveSetting(region) != AutomationLiveSetting.Off &&
            AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
            UIElementAutomationPeer.CreatePeerForElement(region)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
}
