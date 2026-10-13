using System.Collections.Immutable;
using BasisPM.Core.Services;

namespace BasisPM.App.Services;

public sealed class ProjectWork
{
    private static readonly AsyncLocal<ImmutableDictionary<string, long>?> Held = new();
    private static readonly StringComparer KeyComparer = StringComparer.FromComparison(Platform.PathComparison());
    private readonly object _gate = new();
    private readonly Dictionary<string, Slot> _slots = new(KeyComparer);
    private long _nextOwner;

    public bool IsBusy(string projectRoot)
    {
        lock (_gate) return _slots.ContainsKey(Key(projectRoot));
    }

    public string? Running(string projectRoot)
    {
        lock (_gate) return _slots.TryGetValue(Key(projectRoot), out var slot) ? slot.Label : null;
    }

    public Task RunAsync(string projectRoot, string label, Func<Task> work, Action<string>? onWait = null) =>
        RunAsync(projectRoot, label, async () => { await work(); return true; }, onWait);

    public async Task<T> RunAsync<T>(string projectRoot, string label, Func<Task<T>> work, Action<string>? onWait = null)
    {
        var key = Key(projectRoot);
        if (Holds(key)) return await work();
        var owner = await EnterAsync(key, label, onWait);
        var held = Held.Value;
        Held.Value = (held ?? ImmutableDictionary.Create<string, long>(KeyComparer)).SetItem(key, owner);
        try { return await work(); }
        finally
        {
            Held.Value = held;
            Exit(key);
        }
    }

    private bool Holds(string key)
    {
        if (Held.Value is not { } held || !held.TryGetValue(key, out var owner)) return false;
        lock (_gate) return _slots.TryGetValue(key, out var slot) && slot.Owner == owner;
    }

    private async Task<long> EnterAsync(string key, string label, Action<string>? onWait)
    {
        TaskCompletionSource<long> turn;
        string running;
        lock (_gate)
        {
            if (!_slots.TryGetValue(key, out var slot))
            {
                var owner = ++_nextOwner;
                _slots[key] = new Slot(owner, label);
                return owner;
            }
            turn = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
            slot.Waiting.Enqueue((turn, label));
            running = slot.Label;
        }
        onWait?.Invoke(running);
        return await turn.Task;
    }

    private void Exit(string key)
    {
        lock (_gate)
        {
            var slot = _slots[key];
            if (slot.Waiting.Count == 0)
            {
                _slots.Remove(key);
                return;
            }
            var (turn, label) = slot.Waiting.Dequeue();
            slot.Owner = ++_nextOwner;
            slot.Label = label;
            turn.SetResult(slot.Owner);
        }
    }

    private static string Key(string projectRoot) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));

    private sealed class Slot(long owner, string label)
    {
        public long Owner = owner;
        public string Label = label;
        public readonly Queue<(TaskCompletionSource<long> Turn, string Label)> Waiting = new();
    }
}
