namespace ioxide.pg;

/// <summary>
/// The result of a simple query. <see cref="Value"/> is the first column of the first row as text
/// (null when there were no rows, or the field was SQL NULL); <see cref="Rows"/> counts the
/// DataRows returned; <see cref="CommandTag"/> is the server's completion tag (e.g. "SELECT 1").
/// </summary>
public readonly record struct PgResult(string? Value, int Rows, string CommandTag);
