using System.Windows.Input;

namespace NoaChess.GUI.Wpf.ViewModels;

// Minimal ICommand over a delegate, so buttons and keyboard gestures bind to
// the ViewModel instead of going through code-behind event handlers.
//
// CanExecuteChanged is routed to the WPF command manager, which re-queries it
// whenever the UI settles. That is coarse but exactly right here: what enables
// the navigation buttons is the game cursor, and it only moves in response to
// the very input the manager already tracks.
public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => execute();
}

// Same, for commands that take one typed argument (a move row to jump to, a
// board palette to apply).
public sealed class RelayCommand<T>(Action<T> execute, Func<T, bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) =>
        parameter is T typed ? canExecute?.Invoke(typed) ?? true : parameter is null;

    public void Execute(object? parameter)
    {
        if (parameter is T typed)
            execute(typed);
    }
}
