using System.ComponentModel;
using BasisPM.App.ViewModels;
using Xunit;

namespace BasisPM.App.Tests;

public sealed class ObservableObjectTests
{
    private sealed class Probe : ObservableObject
    {
        private int _n;
        public int N { get => _n; set => SetField(ref _n, value); }

        public bool TrySet(int v) => SetField(ref _n, v, nameof(N));
        public void RaiseManually() => OnPropertyChanged(nameof(N));
    }

    [Fact]
    public void SetField_updates_value_and_raises_property_changed()
    {
        var probe = new Probe();
        var raised = new List<string?>();
        probe.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        probe.N = 42;

        Assert.Equal(42, probe.N);
        Assert.Contains(nameof(Probe.N), raised);
    }

    [Fact]
    public void SetField_returns_true_when_changed_false_when_unchanged()
    {
        var probe = new Probe();
        Assert.True(probe.TrySet(1));
        Assert.False(probe.TrySet(1));
        Assert.True(probe.TrySet(2));
    }

    [Fact]
    public void SetField_does_not_raise_when_value_is_unchanged()
    {
        var probe = new Probe { N = 5 };
        var count = 0;
        probe.PropertyChanged += (_, _) => count++;

        probe.N = 5;

        Assert.Equal(0, count);
    }

    [Fact]
    public void OnPropertyChanged_carries_the_member_name()
    {
        var probe = new Probe();
        PropertyChangedEventArgs? args = null;
        probe.PropertyChanged += (_, e) => args = e;

        probe.RaiseManually();

        Assert.Equal(nameof(Probe.N), args?.PropertyName);
    }
}
