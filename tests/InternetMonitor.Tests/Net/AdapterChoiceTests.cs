using InternetMonitor.Configuration;
using InternetMonitor.Net;
using Xunit;

namespace InternetMonitor.Tests.Net;

/// <summary>The adapter setting's three modes, decided over ids and up/down flags.</summary>
public sealed class AdapterChoiceTests
{
    private static readonly (string Id, bool Up)[] Adapters = [("wifi", true), ("ethernet", false)];

    [Fact]
    public void Automatic_never_names_an_adapter()
    {
        Assert.Equal(new AdapterChoice(null, false), InterfaceSelector.Choose(Adapters, AdapterMode.Automatic, "wifi"));
        Assert.Equal(new AdapterChoice(null, false), InterfaceSelector.Choose(Adapters, AdapterMode.Fixed, null));
        Assert.Equal(new AdapterChoice(null, false), InterfaceSelector.Choose(Adapters, AdapterMode.Preferred, string.Empty));
    }

    [Fact]
    public void Preferred_takes_the_chosen_adapter_only_while_it_is_up()
    {
        Assert.Equal(new AdapterChoice("wifi", false), InterfaceSelector.Choose(Adapters, AdapterMode.Preferred, "wifi"));
        Assert.Equal(new AdapterChoice(null, false), InterfaceSelector.Choose(Adapters, AdapterMode.Preferred, "ethernet"));
        Assert.Equal(new AdapterChoice(null, false), InterfaceSelector.Choose(Adapters, AdapterMode.Preferred, "gone"));
    }

    [Fact]
    public void Fixed_takes_the_chosen_adapter_even_when_down_and_reports_one_that_is_gone()
    {
        Assert.Equal(new AdapterChoice("ethernet", false), InterfaceSelector.Choose(Adapters, AdapterMode.Fixed, "ethernet"));
        Assert.Equal(new AdapterChoice(null, true), InterfaceSelector.Choose(Adapters, AdapterMode.Fixed, "gone"));
    }
}
