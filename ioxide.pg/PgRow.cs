namespace ioxide.pg;

/// <summary>Receives one row during <see cref="PgConnection.QueryRowsAsync"/>; runs inline on the reactor.</summary>
public delegate void PgRowHandler(PgRow row);

/// <summary>
/// One DataRow, viewed in place over the connection's receive buffer - valid only for the
/// duration of the callback. Fields are the Postgres text-format bytes.
/// </summary>
public readonly ref struct PgRow
{
    private readonly ReadOnlySpan<byte> _body;

    public int FieldCount { get; }

    internal PgRow(ReadOnlySpan<byte> body)
    {
        _body = body;
        FieldCount = body.Length >= 2 ? (body[0] << 8) | body[1] : 0;
    }

    /// <summary>Field bytes (text format); empty for SQL NULL - check <see cref="IsNull"/>.</summary>
    public ReadOnlySpan<byte> Field(int index)
    {
        (int offset, int length) = Locate(index);
        return length < 0 ? default : _body.Slice(offset, length);
    }

    public bool IsNull(int index) => Locate(index).Length < 0;

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
