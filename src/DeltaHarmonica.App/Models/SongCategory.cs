using System.Collections.ObjectModel;
using System.ComponentModel;

namespace DeltaHarmonica.App.Models;

public sealed class SongCategory : INotifyPropertyChanged
{
    private string _name;
    private bool _isExpanded;

    public SongCategory(string id, string name, bool isExpanded = true)
    {
        Id = id;
        _name = name;
        _isExpanded = isExpanded;
        Songs.CollectionChanged += (_, _) =>
        {
            PropertyChanged?.Invoke(this, new(nameof(Count)));
            PropertyChanged?.Invoke(this, new(nameof(Summary)));
        };
    }

    public string Id { get; }
    public ObservableCollection<SongEntry> Songs { get; } = [];
    public int Count => Songs.Count;
    public string Summary => $"{Name} · {Count} 首";
    public string Name
    {
        get => _name;
        set
        {
            if (_name == value) return;
            _name = value;
            PropertyChanged?.Invoke(this, new(nameof(Name)));
            PropertyChanged?.Invoke(this, new(nameof(Summary)));
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            PropertyChanged?.Invoke(this, new(nameof(IsExpanded)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public override string ToString() => Name;
}
