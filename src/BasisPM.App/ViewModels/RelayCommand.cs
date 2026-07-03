using System.Windows.Input;

namespace BasisPM.App.ViewModels;

public sealed class RelayCommand : ICommand
{
    private readonly Func<object?, Task> _exec;
    private readonly Func<object?, bool>? _canExec;
    private bool _running;

    public RelayCommand(Func<Task> exec, Func<bool>? canExec = null)
        : this(_ => exec(), canExec is null ? null : _ => canExec()) { }

    public RelayCommand(Action exec, Func<bool>? canExec = null)
        : this(_ => { exec(); return Task.CompletedTask; }, canExec is null ? null : _ => canExec()) { }

    public RelayCommand(Func<object?, Task> exec, Func<object?, bool>? canExec = null)
    {
        _exec = exec;
        _canExec = canExec;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) =>
        !_running && (_canExec?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        _running = true;
        Raise();
        try { await _exec(parameter); }
        finally { _running = false; Raise(); }
    }

    public void Raise() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class RelayCommand<T> : ICommand
{
    private readonly Func<T?, Task> _exec;
    private readonly Func<T?, bool>? _canExec;
    private bool _running;

    public RelayCommand(Func<T?, Task> exec, Func<T?, bool>? canExec = null)
    {
        _exec = exec;
        _canExec = canExec;
    }

    public RelayCommand(Action<T?> exec, Func<T?, bool>? canExec = null)
    {
        _exec = p => { exec(p); return Task.CompletedTask; };
        _canExec = canExec;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) =>
        !_running && (_canExec?.Invoke(Cast(parameter)) ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        _running = true;
        Raise();
        try { await _exec(Cast(parameter)); }
        finally { _running = false; Raise(); }
    }

    public void Raise() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    private static T? Cast(object? p) => p is T t ? t : default;
}

