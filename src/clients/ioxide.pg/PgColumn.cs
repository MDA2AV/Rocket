namespace ioxide.pg;

/// <summary>
/// One column's metadata from a RowDescription. Captured for row-streaming queries (an <c>onRow</c>
/// handler / <see cref="PgConnection.QueryRowsAsync"/>); the single-value <see cref="PgConnection.QueryAsync(string)"/>
/// path skips RowDescription entirely, so it stays allocation-free. Values remain text-format - this
/// adds names and type OIDs (by-name access), not binary decoding.
/// </summary>
public readonly record struct PgColumn(
    string Name,
    int TableOid,
    short ColumnAttribute,
    int TypeOid,
    short TypeSize,
    int TypeModifier,
    short FormatCode);
