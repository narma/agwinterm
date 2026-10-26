namespace Agwinterm.Core;

public sealed class ScreenBuffer
{
    private Cell[] _cells;

    public int Cols { get; private set; }
    public int Rows { get; private set; }

    public ScreenBuffer(int cols, int rows)
    {
        if (cols <= 0) throw new ArgumentOutOfRangeException(nameof(cols));
        if (rows <= 0) throw new ArgumentOutOfRangeException(nameof(rows));
        Cols = cols;
        Rows = rows;
        _cells = new Cell[cols * rows];
        Clear();
    }

    public Cell this[int row, int col]
    {
        get
        {
            CheckBounds(row, col);
            return _cells[row * Cols + col];
        }
        set
        {
            CheckBounds(row, col);
            _cells[row * Cols + col] = value;
        }
    }

    public void Clear() => Array.Fill(_cells, Cell.Empty);

    /// <summary>Block-move <paramref name="count"/> whole rows from <paramref name="srcRow"/> to
    /// <paramref name="dstRow"/> (memmove semantics — overlapping regions are safe). Scrolling with
    /// this is one Array.Copy instead of rows×cols bounds-checked indexer calls, which dominated
    /// the profile under sustained output.</summary>
    public void MoveRows(int srcRow, int dstRow, int count)
    {
        if (count <= 0) return;
        if ((uint)srcRow >= (uint)Rows || (uint)dstRow >= (uint)Rows) throw new ArgumentOutOfRangeException(nameof(srcRow));
        if (srcRow + count > Rows || dstRow + count > Rows) throw new ArgumentOutOfRangeException(nameof(count));
        Array.Copy(_cells, srcRow * Cols, _cells, dstRow * Cols, count * Cols);
    }

    /// <summary>Fill one whole row with <paramref name="cell"/> in a single span fill.</summary>
    public void FillRow(int row, Cell cell)
    {
        if ((uint)row >= (uint)Rows) throw new ArgumentOutOfRangeException(nameof(row));
        _cells.AsSpan(row * Cols, Cols).Fill(cell);
    }

    /// <summary>Copy one whole row into <paramref name="dest"/> (used by scrollback capture).</summary>
    public void CopyRowTo(int row, Cell[] dest)
    {
        if ((uint)row >= (uint)Rows) throw new ArgumentOutOfRangeException(nameof(row));
        Array.Copy(_cells, row * Cols, dest, 0, Math.Min(Cols, dest.Length));
    }

    /// <summary>Refresh selection's compact text-only frame, reporting any change. Reading the
    /// flat array avoids per-cell coordinate checks; no bookkeeping is added to terminal writes.</summary>
    internal bool CopyTextTo(Span<uint> destination)
    {
        if (destination.Length != _cells.Length) throw new ArgumentException("Frame size mismatch", nameof(destination));
        bool changed = false;
        for (int i = 0; i < _cells.Length; i++)
        {
            // Unicode scalar: 21 bits; cell width: 8 bits. Colours do not identify selected text.
            uint text = (uint)_cells[i].Rune | ((uint)_cells[i].Width << 21);
            changed |= destination[i] != text;
            destination[i] = text;
        }
        return changed;
    }

    /// <summary>Cheap rejection of a missing selected word without copying an entire frame.</summary>
    internal bool TextMatches(int row, int from, ReadOnlySpan<uint> text)
    {
        var cells = _cells.AsSpan(row * Cols + from, text.Length);
        for (int i = 0; i < text.Length; i++)
            if (((uint)cells[i].Rune | ((uint)cells[i].Width << 21)) != text[i]) return false;
        return true;
    }

    public void Resize(int cols, int rows)
    {
        if (cols <= 0) throw new ArgumentOutOfRangeException(nameof(cols));
        if (rows <= 0) throw new ArgumentOutOfRangeException(nameof(rows));
        var next = new Cell[cols * rows];
        Array.Fill(next, Cell.Empty);
        int copyCols = Math.Min(cols, Cols);
        int copyRows = Math.Min(rows, Rows);
        for (int r = 0; r < copyRows; r++)
            for (int c = 0; c < copyCols; c++)
                next[r * cols + c] = _cells[r * Cols + c];
        _cells = next;
        Cols = cols;
        Rows = rows;
    }

    private void CheckBounds(int row, int col)
    {
        if ((uint)row >= (uint)Rows) throw new ArgumentOutOfRangeException(nameof(row));
        if ((uint)col >= (uint)Cols) throw new ArgumentOutOfRangeException(nameof(col));
    }
}
