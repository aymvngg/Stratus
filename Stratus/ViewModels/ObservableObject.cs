using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Stratus.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise(params string[] propertyNames)
    {
        foreach (var name in propertyNames)
        {
            PropertyChanged?.Invoke(this, new(name));
            OnPropertyChanged(name);
        }
    }

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        PropertyChanged?.Invoke(this, new(propertyName!));
        OnPropertyChanged(propertyName!);
        return true;
    }

    protected virtual void OnPropertyChanged(string propertyName) { }
}
