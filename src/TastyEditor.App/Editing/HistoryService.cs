using TastyEditor.App.Viewport;

namespace TastyEditor.App.Editing;

/// <summary>Undo/redo for the level document (roadmap slice 7 "undo/redo UI"). Snapshot-based
/// on purpose: every entry is the codec's own <see cref="LevelDoc.Encode"/> bytes, and stepping
/// re-decodes a snapshot through the already golden-verified decode path. That makes undo correct
/// for <em>every</em> edit kind (drag, vertex, water, sprite, dup/delete/move) without each
/// mutation shipping a hand-written inverse, and a snapshot can never drift from what Save would
/// write at that point — it IS what Save would write.
///
/// <para>Model: an ordered list of states with a cursor. State 0 is the freshly-loaded baseline;
/// each committed edit appends the model's new encode and moves the cursor to the tip. Undo moves
/// the cursor back one and returns that state; redo forward. Editing after an undo truncates the
/// redo tail (a new branch). Consecutive edits tagged with the same <c>group</c> inside a short
/// window overwrite the tip, so a burst (e.g. typing a water field) collapses to one step whose
/// predecessor is the true pre-burst state.</para>
///
/// <para>This design needs only the POST-edit encode, so no "capture before the edit" hook is
/// required at the drag/inspector call sites — they just call <see cref="Commit"/> once the model
/// has changed.</para></summary>
public sealed class HistoryService
{
    private const int Cap = 80;               // plenty for a session; snapshots are level-sized
    private readonly List<byte[]> _states = new();
    private int _cursor = -1;                  // index of the state currently in the model
    private DateTime _lastCommit = DateTime.MinValue;
    private string _lastGroup = "";

    public bool CanUndo => _cursor > 0;
    public bool CanRedo => _cursor >= 0 && _cursor < _states.Count - 1;
    public int UndoDepth => _cursor;

    /// <summary>Seed the baseline for a freshly loaded document (its on-disk bytes, which are by
    /// invariant the encode of the decoded model). Clears any prior history.</summary>
    public void Seed(LevelDoc doc)
    {
        _states.Clear();
        _states.Add(doc.Original);
        _cursor = 0;
        _lastGroup = "";
        _lastCommit = DateTime.MinValue;
    }

    /// <summary>Record the model's current bytes after a committed edit. A burst tagged with the
    /// same <paramref name="group"/> within <paramref name="coalesceSec"/> replaces the tip rather
    /// than adding a step. An encode identical to the tip (refused / reverted edit) is dropped.</summary>
    public void Commit(LevelDoc doc, string group = "", double coalesceSec = 0)
    {
        if (_cursor < 0) Seed(doc);   // defensive: commit before any seed still records a baseline
        var now = doc.Encode();
        if (_states.Count == 0) { _states.Add(now); _cursor = 0; return; }

        var tip = _states[_cursor];
        if (now.AsSpan().SequenceEqual(tip)) return;   // nothing changed

        // Drop any redo tail beyond the cursor: this edit starts a new branch.
        if (_cursor < _states.Count - 1) _states.RemoveRange(_cursor + 1, _states.Count - _cursor - 1);

        bool sameBurst = group.Length > 0 && group == _lastGroup && _lastGroup.Length > 0
            && (DateTime.Now - _lastCommit).TotalSeconds < coalesceSec
            && _cursor == _states.Count - 1 && _cursor > 0;
        if (sameBurst)
            _states[_cursor] = now;                     // collapse the burst into the tip
        else
        {
            _states.Add(now);
            if (_states.Count > Cap) _states.RemoveAt(0);
            _cursor = _states.Count - 1;
        }
        _lastCommit = DateTime.Now;
        _lastGroup = group;
    }

    /// <summary>Close the current burst: the next Commit always starts a new undo step even
    /// when it carries the same group tag (fired when a new drag gesture begins).</summary>
    public void CloseBurst() => _lastGroup = "";

    /// <summary>Step back one state, or null when already at the oldest.</summary>
    public byte[]? Undo()
    {
        if (!CanUndo) return null;
        _cursor--;
        _lastGroup = "";
        return _states[_cursor];
    }

    /// <summary>Step forward one state, or null when already at the newest.</summary>
    public byte[]? Redo()
    {
        if (!CanRedo) return null;
        _cursor++;
        _lastGroup = "";
        return _states[_cursor];
    }
}
