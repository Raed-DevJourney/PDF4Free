using PDF4Free.Core.Model;
using PDF4Free.Core.Pdfium;

namespace PDF4Free.Core;

/// <summary>What a change may have affected, so views know how much to refresh.</summary>
public enum ChangeKind
{
    /// <summary>Page contents or annotations changed; page count, order and sizes did not.</summary>
    Content,

    /// <summary>Pages were added, removed, reordered, resized or rotated.</summary>
    Structure,
}

/// <summary>
/// One open document: its current bytes, the PDFium view of them, the file it came from and its undo history.
/// Every edit is a function from PDF bytes to PDF bytes, which makes undo a matter of keeping the previous bytes.
/// </summary>
public sealed class PdfSession : IDisposable
{
    private sealed record Snapshot(byte[] Bytes, string Description);

    private const int MaxUndoSteps = 60;
    private const long MaxUndoBytes = 512L * 1024 * 1024;

    private readonly List<Snapshot> _undo = [];
    private readonly List<Snapshot> _redo = [];

    private PdfSession(byte[] bytes, PdfiumDocument document, string? path, string name, string? password)
    {
        Bytes = bytes;
        Document = document;
        FilePath = path;
        DisplayName = name;
        Password = password;
    }

    public byte[] Bytes { get; private set; }
    public PdfiumDocument Document { get; private set; }
    public string? FilePath { get; private set; }
    public string DisplayName { get; private set; }
    public bool IsDirty { get; private set; }

    /// <summary>The password the document was opened with, kept only while the document stays encrypted.</summary>
    public string? Password { get; private set; }

    /// <summary>True when the file was encrypted and has been unlocked for editing: saving writes it without a password.</summary>
    public bool SecurityRemoved { get; private set; }

    /// <summary>True when the document is encrypted and was opened without its permissions password.</summary>
    public bool IsRestricted => !Document.Security.OwnerAccess;

    public int PageCount => Document.PageCount;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string UndoDescription => CanUndo ? _undo[^1].Description : "";
    public string RedoDescription => CanRedo ? _redo[^1].Description : "";

    /// <summary>Raised after the document content changed.</summary>
    public event Action<ChangeKind>? Changed;

    /// <summary>Raised when the name, dirty flag or undo state changed.</summary>
    public event Action? StateChanged;

    /// <summary>Opens a file. <paramref name="askPassword"/> receives true when a password was already tried and was wrong; returning null cancels.</summary>
    public static PdfSession Open(string path, Func<bool, string?> askPassword)
    {
        var session = Load(File.ReadAllBytes(path), Path.GetFullPath(path), Path.GetFileName(path), askPassword);
        return session;
    }

    /// <summary>Wraps bytes that are not on disk yet (a new or converted document). The session starts out unsaved.</summary>
    public static PdfSession Create(byte[] bytes, string name)
    {
        var session = Load(bytes, null, name, _ => null);
        session.IsDirty = true;
        return session;
    }

    private static PdfSession Load(byte[] bytes, string? path, string name, Func<bool, string?> askPassword)
    {
        string? password = null;
        PdfiumDocument document;
        while (true)
        {
            try
            {
                document = PdfiumDocument.Load(bytes, password);
                break;
            }
            catch (PdfPasswordRequiredException ex)
            {
                password = askPassword(ex.WrongPassword);
                if (password is null) throw new OperationCanceledException("A password is required to open this document.");
            }
        }
        var session = new PdfSession(bytes, document, path, name, password);
        session.UnlockIfOwner();
        return session;
    }

    /// <summary>When the permissions password was supplied, work on an unencrypted copy so the document can be edited.</summary>
    private void UnlockIfOwner()
    {
        if (!Document.Security.IsEncrypted || !Document.Security.OwnerAccess) return;
        var plain = Document.Save(removeSecurity: true);
        var fresh = PdfiumDocument.Load(plain);
        Document.Dispose();
        Document = fresh;
        Bytes = plain;
        Password = null;
        SecurityRemoved = true;
    }

    /// <summary>Tries the permissions password on a restricted document. Returns false when it does not grant full access.</summary>
    public bool TryUnlock(string password)
    {
        if (!IsRestricted) return true;
        PdfiumDocument candidate;
        try
        {
            candidate = PdfiumDocument.Load(Bytes, password);
        }
        catch (PdfPasswordRequiredException)
        {
            return false;
        }
        if (!candidate.Security.OwnerAccess)
        {
            candidate.Dispose();
            return false;
        }
        Document.Dispose();
        Document = candidate;
        Password = password;
        UnlockIfOwner();
        Changed?.Invoke(ChangeKind.Content);
        StateChanged?.Invoke();
        return true;
    }

    // ------------------------------------------------------------------ editing

    /// <summary>Applies an edit on the calling thread.</summary>
    public void Apply(string description, Func<byte[], byte[]> edit, ChangeKind kind = ChangeKind.Content)
    {
        EnsureEditable();
        Replace(description, edit(Bytes), kind);
    }

    /// <summary>Runs an edit on a background thread and applies the result on the caller's context.</summary>
    public async Task ApplyAsync(string description, Func<byte[], byte[]> edit, ChangeKind kind = ChangeKind.Content)
    {
        EnsureEditable();
        var source = Bytes;
        var result = await Task.Run(() => edit(source));
        if (!ReferenceEquals(source, Bytes)) throw new InvalidOperationException("The document changed while the operation was running. Please try again.");
        Replace(description, result, kind);
    }

    /// <summary>Installs new document bytes as the next undoable step.</summary>
    public void Replace(string description, byte[] newBytes, ChangeKind kind = ChangeKind.Content)
    {
        EnsureEditable();
        if (ReferenceEquals(newBytes, Bytes)) return;
        var previous = Bytes;
        Swap(newBytes);
        _undo.Add(new Snapshot(previous, description));
        _redo.Clear();
        TrimHistory();
        IsDirty = true;
        Changed?.Invoke(kind);
        StateChanged?.Invoke();
    }

    public void Undo() => Step(_undo, _redo);

    public void Redo() => Step(_redo, _undo);

    private void Step(List<Snapshot> from, List<Snapshot> to)
    {
        if (from.Count == 0) return;
        var snapshot = from[^1];
        from.RemoveAt(from.Count - 1);
        var current = Bytes;
        Swap(snapshot.Bytes);
        to.Add(new Snapshot(current, snapshot.Description));
        IsDirty = true;
        Changed?.Invoke(ChangeKind.Structure);
        StateChanged?.Invoke();
    }

    private void Swap(byte[] bytes)
    {
        var fresh = PdfiumDocument.Load(bytes);
        var old = Document;
        Document = fresh;
        Bytes = bytes;
        old.Dispose();
    }

    private void TrimHistory()
    {
        while (_undo.Count > MaxUndoSteps || (_undo.Count > 1 && _undo.Sum(s => (long)s.Bytes.Length) > MaxUndoBytes)) _undo.RemoveAt(0);
    }

    private void EnsureEditable()
    {
        if (IsRestricted) throw new InvalidOperationException("This document is protected against changes. Unlock it with the permissions password first (Protect > Unlock).");
    }

    // ------------------------------------------------------------------ saving

    public void Save(string? path = null)
    {
        var target = path ?? FilePath ?? throw new InvalidOperationException("Choose where to save the document.");
        target = Path.GetFullPath(target);
        var temp = target + ".pdf4free-tmp";
        File.WriteAllBytes(temp, Bytes);
        File.Move(temp, target, overwrite: true);
        FilePath = target;
        DisplayName = Path.GetFileName(target);
        IsDirty = false;
        StateChanged?.Invoke();
    }

    /// <summary>
    /// Writes a transformed copy of the document (for example an encrypted one) to <paramref name="path"/> and treats
    /// that file as the saved state. The session itself keeps working on the untransformed bytes.
    /// </summary>
    public void SaveTransformed(string path, Func<byte[], byte[]> transform, bool encrypted)
    {
        var target = Path.GetFullPath(path);
        var temp = target + ".pdf4free-tmp";
        File.WriteAllBytes(temp, transform(Bytes));
        File.Move(temp, target, overwrite: true);
        FilePath = target;
        DisplayName = Path.GetFileName(target);
        IsDirty = false;
        if (encrypted) SecurityRemoved = true;
        StateChanged?.Invoke();
    }

    public void Dispose() => Document.Dispose();
}
