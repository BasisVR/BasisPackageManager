using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace BasisPM.App.Views;

public static class ExpanderToolTip
{
    public static void Register()
    {
        TemplatedControl.TemplateAppliedEvent.AddClassHandler<Expander>((expander, e) =>
        {
            if (e.NameScope.Find<ToggleButton>("ExpanderHeader") is { } header) header.Bind(ToolTip.TipProperty, expander.GetObservable(ToolTip.TipProperty));
        });
        ToolTip.ToolTipOpeningEvent.AddClassHandler<Expander>((_, e) => e.Cancel = true);
    }
}
