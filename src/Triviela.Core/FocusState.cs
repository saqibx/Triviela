using Triviela.Domain;

namespace Triviela.Core;

public sealed class FocusState
{
    private readonly object _gate = new();
    private IReadOnlyList<Fixture> _live = [];
    private string? _selectedId;

    public event Action? Changed;

    public IReadOnlyList<Fixture> Live
    {
        get { lock (_gate) return _live; }
    }

    public string? SelectedId
    {
        get { lock (_gate) return _selectedId; }
    }

    public void SetLive(IReadOnlyList<Fixture> live)
    {
        bool changed;
        lock (_gate)
        {
            _live = live;

            if (_selectedId is null || live.All(f => f.Id != _selectedId))
            {
                var next = live.FirstOrDefault()?.Id;
                changed = next != _selectedId;
                _selectedId = next;
            }
            else changed = false;
        }

        Changed?.Invoke();
        _ = changed;
    }

    public void Select(string fixtureId)
    {
        lock (_gate) { _selectedId = fixtureId; }
        Changed?.Invoke();
    }
}
