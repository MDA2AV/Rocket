namespace ioxide.pg;

/// <summary>Receives one row during <see cref="PgConnection.QueryRowsAsync"/>; runs inline on the reactor.</summary>
public delegate void PgRowHandler(PgRow row);

/// <summary>
/// One DataRow, viewed in place over the connection's receive buffer - valid only for the
/// duration of the callback. Fields are the Postgres text-format bytes. <see cref="Columns"/> is
/// populated for row-streaming queries, enabling by-name access; it is empty otherwise.
/// </summary>
public readonly ref struct PgRow
{
    private readonly ReadOnlySpan<byte> _body;
    private readonly PgColumn[]? _columns;

    public int FieldCount { get; }

    internal PgRow(ReadOnlySpan<byte> body, PgColumn[]? columns = null)
    {
        _body = body;
        _columns = columns;
        FieldCount = body.Length >= 2 ? (body[0] << 8) | body[1] : 0;
    }

    /// <summary>Column metadata (names, type OIDs); empty unless this came from a row-streaming query.</summary>
    public ReadOnlySpan<PgColumn> Columns => _columns;

    /// <summary>Field bytes (text format); empty for SQL NULL - check <see cref="IsNull(int)"/>.</summary>
    public ReadOnlySpan<byte> Field(int index)
    {
        if ((uint)index >= (uint)FieldCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, $"row has {FieldCount} field(s)");
        }
        (int offset, int length) = Locate(index);
        return length < 0 ? default : _body.Slice(offset, length);
    }

    /// <summary>Field bytes by column name (case-sensitive). Requires column metadata (a row-streaming query).</summary>
    public ReadOnlySpan<byte> Field(string name) => Field(Ordinal(name));

    public bool IsNull(int index)
    {
        if ((uint)index >= (uint)FieldCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, $"row has {FieldCount} field(s)");
        }
        return Locate(index).Length < 0;
    }

    public bool IsNull(string name) => IsNull(Ordinal(name));

    /// <summary>The ordinal of column <paramref name="name"/>. Throws if columns weren't captured or the name is unknown.</summary>
    public int Ordinal(string name)
    {
        PgColumn[]? columns = _columns;
        if (columns is null)
        {
            throw new InvalidOperationException(
                "no column metadata on this row; use a row-streaming query (onRow / QueryRowsAsync) for by-name access");
        }
        for (int i = 0; i < columns.Length; i++)
        {
            if (columns[i].Name == name)
            {
                return i;
            }
        }
        throw new ArgumentException($"column '{name}' not found", nameof(name));
    }

    // O(index): walks the field-length prefixes from the front. Fine for narrow rows; accessing
    // every field of a very wide row is O(n^2) - read sequentially into your own structure if that bites.
    private (int Offset, int Length) Locate(int index)
    {
        int position = 2;
        for (int i = 0; i <= index; i++)
        {
            int length = (_body[position] << 24) | (_body[position + 1] << 16)
                       | (_body[position + 2] << 8) | _body[position + 3];
            position += 4;

            if (i == index)
            {
                return (position, length);
            }
            if (length > 0)
            {
                position += length;
            }
        }
        return (position, -1);
    }
}
