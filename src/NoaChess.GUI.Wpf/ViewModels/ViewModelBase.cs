using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace NoaChess.GUI.Wpf.ViewModels;

// Minimal MVVM base: implements INotifyPropertyChanged, the mechanism through
// which WPF learns that a ViewModel property has changed and refreshes the
// bound controls. No external MVVM framework is used to keep the MVP
// dependency-free.
public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    // Assigns the field and notifies only if the value actually changes.
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
