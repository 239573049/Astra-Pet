using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AstraPet.Core;

/// <summary>带 <see cref="INotifyPropertyChanged"/> 的基类，设置项、模型配置、记忆条目共用。</summary>
public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
