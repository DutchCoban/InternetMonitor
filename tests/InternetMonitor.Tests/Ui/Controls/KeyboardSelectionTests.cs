using System.Reflection;
using System.Windows.Forms;
using InternetMonitor.Core.Diagnosis;
using InternetMonitor.Core.Incidents;
using InternetMonitor.Core.Steps;
using InternetMonitor.Ui.Controls;
using Xunit;

namespace InternetMonitor.Tests.Ui.Controls;

/// <summary>The painted lists a mouse used to be the only way into are selectable by keyboard: chips, the incident list, the step table.</summary>
[Collection("Localization")]
public sealed class KeyboardSelectionTests
{
    [Fact]
    public void Arrow_keys_move_the_selected_chip_and_home_and_end_jump()
    {
        UiTestHost.Run(() =>
        {
            using var chips = new ChipGroup();
            chips.SetChips([new ChipGroup.Chip("a", "A"), new ChipGroup.Chip("b", "B"), new ChipGroup.Chip("c", "C")], "a");
            var selected = new List<string>();
            chips.SelectionChanged += selected.Add;

            InvokeOnKeyDown(chips, Keys.Right);
            InvokeOnKeyDown(chips, Keys.End);
            InvokeOnKeyDown(chips, Keys.Right);
            InvokeOnKeyDown(chips, Keys.Home);
            InvokeOnKeyDown(chips, Keys.Left);

            Assert.Equal(["b", "c", "a"], selected);
            Assert.Equal("a", chips.SelectedId);
            Assert.True(chips.TabStop);
            Assert.Equal(AccessibleRole.PageTabList, chips.AccessibleRole);
        });
    }

    [Fact]
    public void Arrow_keys_move_through_the_incident_list()
    {
        UiTestHost.Run(() =>
        {
            using var list = new IncidentList();
            list.Size = new System.Drawing.Size(400, 300);
            list.SetIncidents([Incident("INC-1"), Incident("INC-2"), Incident("INC-3")]);
            var selected = new List<string>();
            list.SelectionChanged += incident => selected.Add(incident.Id);

            InvokeOnKeyDown(list, Keys.Down);
            InvokeOnKeyDown(list, Keys.Down);
            InvokeOnKeyDown(list, Keys.Down);
            InvokeOnKeyDown(list, Keys.Up);

            Assert.Equal(["INC-2", "INC-3", "INC-2"], selected);
            Assert.Equal("INC-2", list.Selected?.Id);
        });
    }

    [Fact]
    public void Arrow_keys_move_through_the_step_table()
    {
        UiTestHost.Run(() =>
        {
            using var table = new StepTable();
            var selected = new List<StepId>();
            table.SelectionChanged += selected.Add;

            InvokeOnKeyDown(table, Keys.Down);
            InvokeOnKeyDown(table, Keys.Up);
            InvokeOnKeyDown(table, Keys.Up);

            Assert.Equal([StepId.Link, StepId.Adapter], selected);
            Assert.Equal(StepId.Adapter, table.Selected);
        });
    }

    private static Incident Incident(string id) => new()
    {
        Id = id,
        Cause = RootCause.Internet,
        StartUtc = DateTimeOffset.UtcNow.AddMinutes(-10),
        Status = IncidentStatus.Resolved,
        EndUtc = DateTimeOffset.UtcNow,
        Headline = id,
    };

    private static void InvokeOnKeyDown(Control control, Keys key) =>
        control.GetType().GetMethod("OnKeyDown", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(control, [new KeyEventArgs(key)]);
}
