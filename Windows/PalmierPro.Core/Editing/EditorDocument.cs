using PalmierPro.Core.Models;

namespace PalmierPro.Core.Editing;

public sealed record CommandReceipt(string Command, bool Changed, int UndoDepth, int RedoDepth);

public sealed class EditorDocument
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Stack<DocumentSnapshot> _undo = new();
    private readonly Stack<DocumentSnapshot> _redo = new();
    private long _revision;
    private long _savedRevision;

    public EditorDocument(ProjectFile project, MediaManifest? manifest = null)
    {
        Project = project.Clone();
        Manifest = manifest?.Clone() ?? new MediaManifest();
    }

    public ProjectFile Project { get; private set; }
    public MediaManifest Manifest { get; private set; }
    public bool IsDirty => _revision != _savedRevision;

    public async Task<CommandReceipt> ExecuteAsync(IEditorCommand command, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var before = Capture();
            try
            {
                command.Apply(Project, Manifest);
            }
            catch
            {
                Restore(before);
                throw;
            }
            _undo.Push(before);
            _redo.Clear();
            _revision++;
            return new CommandReceipt(command.Name, true, _undo.Count, _redo.Count);
        }
        finally { _gate.Release(); }
    }

    public async Task<CommandReceipt> UndoAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_undo.Count == 0) return new CommandReceipt("Undo", false, 0, _redo.Count);
            _redo.Push(Capture());
            Restore(_undo.Pop());
            _revision++;
            return new CommandReceipt("Undo", true, _undo.Count, _redo.Count);
        }
        finally { _gate.Release(); }
    }

    public async Task<CommandReceipt> RedoAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_redo.Count == 0) return new CommandReceipt("Redo", false, _undo.Count, 0);
            _undo.Push(Capture());
            var next = _redo.Pop();
            Restore(next);
            _revision++;
            return new CommandReceipt("Redo", true, _undo.Count, _redo.Count);
        }
        finally { _gate.Release(); }
    }

    public async Task<ProjectSnapshot> SnapshotAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return new ProjectSnapshot(Project.Clone(), Manifest.Clone()); }
        finally { _gate.Release(); }
    }

    public async Task MarkSavedAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { _savedRevision = _revision; }
        finally { _gate.Release(); }
    }

    private DocumentSnapshot Capture() => new(Project.Clone(), Manifest.Clone());

    private void Restore(DocumentSnapshot snapshot)
    {
        Project = snapshot.Project.Clone();
        Manifest = snapshot.Manifest.Clone();
    }
}

public sealed record ProjectSnapshot(ProjectFile Project, MediaManifest Manifest);
internal sealed record DocumentSnapshot(ProjectFile Project, MediaManifest Manifest);
