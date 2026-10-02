using AudioPlayer.Models;

namespace AudioPlayer.Services;

/// <summary>Decides navigation independently of the audio device; shuffle visits each track once per cycle.</summary>
public sealed class PlaybackQueue
{
    private readonly Random _random;
    private readonly List<Guid> _bag = new();
    private readonly List<Guid> _history = new();
    private HashSet<Guid> _known = new();
    private int _cursor = -1;
    private PlayMode? _lastMode;
    public PlaybackQueue(Random? random = null) => _random = random ?? Random.Shared;

    public void Reset()
    {
        _bag.Clear(); _history.Clear(); _known.Clear(); _cursor = -1; _lastMode = null;
    }

    public Track? Next(IReadOnlyList<Track> tracks, Track? current, PlayMode mode, bool backwards, bool automatic, bool repeat)
    {
        if (tracks.Count == 0) return null;
        int index = current is null ? -1 : tracks.ToList().FindIndex(t => t.Id == current.Id);
        if (automatic && mode == PlayMode.Single) return null;
        if (automatic && mode == PlayMode.RepeatOne && index >= 0) return tracks[index];
        if (mode != PlayMode.Shuffle)
        {
            _lastMode = mode;
            int step = (mode == PlayMode.Reverse ? -1 : 1) * (backwards ? -1 : 1);
            if (index < 0) return tracks[step > 0 ? 0 : tracks.Count - 1];
            int next = index + step;
            if (automatic && !repeat && (next < 0 || next >= tracks.Count)) return null;
            return tracks[(next + tracks.Count) % tracks.Count];
        }

        var ids = tracks.Select(t => t.Id).ToHashSet();
        if (_lastMode != mode || !_known.SetEquals(ids) ||
            (index >= 0 && (_cursor < 0 || _history[_cursor] != current!.Id)))
        {
            Reset(); _known = ids;
            if (index >= 0) { _history.Add(current!.Id); _cursor = 0; }
            FillBag(tracks, current);
        }
        _lastMode = mode;
        if (backwards)
        {
            if (_cursor > 0) _cursor--;
            return _cursor >= 0 ? tracks.First(t => t.Id == _history[_cursor]) : tracks[0];
        }
        if (_cursor + 1 < _history.Count)
        {
            _cursor++;
            return tracks.First(t => t.Id == _history[_cursor]);
        }
        if (_bag.Count == 0)
        {
            if (automatic && !repeat) return null;
            FillBag(tracks, current);
            if (_bag.Count == 0) return tracks[0];
        }
        Guid id = _bag[^1]; _bag.RemoveAt(_bag.Count - 1);
        _history.Add(id); _cursor = _history.Count - 1;
        return tracks.First(t => t.Id == id);
    }

    private void FillBag(IReadOnlyList<Track> tracks, Track? current)
    {
        _bag.Clear();
        _bag.AddRange(tracks.Where(t => t.Id != current?.Id).Select(t => t.Id));
        for (int i = _bag.Count - 1; i > 0; i--)
        {
            int j = _random.Next(i + 1);
            (_bag[i], _bag[j]) = (_bag[j], _bag[i]);
        }
    }
}
