namespace Snipper.Cli;

/// <summary>
/// Serialisation format for the report file. Separate from
/// <see cref="CommandLineOptions"/> so both the parser and the writer can name it
/// without depending on each other.
/// </summary>
internal enum ReportFormat : byte
{
    Json,
    Sarif,
}