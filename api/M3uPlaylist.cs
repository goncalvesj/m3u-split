using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

namespace api;

public static class M3uError
{
    public const string NotM3u = "not_m3u";
    public const string NoCategories = "no_categories";
    public const string TooManyCategories = "too_many_categories";
}

/// <summary>A playlist problem that is safe to persist and return.</summary>
public sealed class M3uFormatException(string code) : Exception($"The playlist could not be read ({code}).")
{
    public string Code { get; } = code;
}

public sealed record M3uCategory(string Name, int Count);

/// <summary>Categories in first-seen order with the number of complete channel entries in each.</summary>
public sealed record M3uIndex(IReadOnlyList<M3uCategory> Categories, long Entries);

/// <summary>One #EXTINF entry: the #EXTINF line, directive lines before the URL, and the URL line.</summary>
public sealed class M3uEntry(string extInf)
{
    public string ExtInf { get; } = extInf;
    public List<string>? Directives { get; set; }

    /// <summary>Null when the entry ended without a URL line; such entries are never written.</summary>
    public string? Url { get; set; }
}

/// <summary>
/// Streaming M3U support shared by URL imports and later saved-profile refreshes. Category names use the
/// same rule as the browser parser: the exact, untrimmed value of every group-title="..." attribute.
/// </summary>
public static partial class M3uPlaylist
{
    public const int MaxCategories = 10_000;
    public const int MaxCategoryNameChars = 1024 * 1024;
    private const int ProgressInterval = 1000;
    // Four MiB blocks also work with older append blob service versions.
    private const int DownloadChunkSize = 4 * 1024 * 1024;

    /// <summary>Checks the M3U header and copies a download in blocks. Returns the total byte count.</summary>
    public static async Task<long> CopyDownloadAsync(
        Stream source,
        Func<byte[], int, CancellationToken, Task> append,
        Func<long, CancellationToken, Task> progress,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(DownloadChunkSize);
        try
        {
            var checkedHeader = false;
            long stored = 0;
            var endOfStream = false;
            while (!endOfStream)
            {
                var filled = 0;
                while (filled < DownloadChunkSize)
                {
                    var read = await source.ReadAsync(buffer.AsMemory(filled, DownloadChunkSize - filled), cancellationToken);
                    if (read == 0)
                    {
                        endOfStream = true;
                        break;
                    }

                    filled += read;
                    if (!checkedHeader)
                    {
                        var looksLikeM3u = LooksLikeM3u(buffer.AsSpan(0, filled), endOfStream: false);
                        if (looksLikeM3u == false)
                            throw new M3uFormatException(M3uError.NotM3u);
                        checkedHeader = looksLikeM3u == true;
                    }

                    await progress(stored + filled, cancellationToken);
                }

                // Reject an undecided header after the first block or the end of the body.
                if (!checkedHeader)
                {
                    if (LooksLikeM3u(buffer.AsSpan(0, filled), endOfStream: true) != true)
                        throw new M3uFormatException(M3uError.NotM3u);
                    checkedHeader = true;
                }

                if (filled > 0)
                {
                    await append(buffer, filled, cancellationToken);
                    stored += filled;
                }
            }

            return stored;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Checks the start of a download for the #EXTM3U header without decoding the whole body.</summary>
    /// <returns>True or false when known; null when more bytes are needed.</returns>
    public static bool? LooksLikeM3u(ReadOnlySpan<byte> start, bool endOfStream)
    {
        ReadOnlySpan<byte> bom = [0xEF, 0xBB, 0xBF];
        ReadOnlySpan<byte> header = "#EXTM3U"u8;

        if (start.StartsWith(bom))
            start = start[bom.Length..];
        else if (start.Length < bom.Length && bom.StartsWith(start) && !endOfStream)
            return null;

        var offset = start.IndexOfAnyExcept(" \t\r\n"u8);
        if (offset < 0)
            return endOfStream ? false : null;

        start = start[offset..];
        var compared = Math.Min(start.Length, header.Length);
        if (!Ascii.EqualsIgnoreCase(start[..compared], header[..compared]))
            return false;
        if (compared < header.Length)
            return endOfStream ? false : null;
        if (start.Length == header.Length)
            return endOfStream ? true : null;

        return start[header.Length] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';
    }

    public static async Task<M3uIndex> IndexAsync(
        Stream source, Func<long, ValueTask>? progress, CancellationToken cancellationToken)
    {
        using var reader = await M3uReader.OpenAsync(source, cancellationToken);
        var positions = new Dictionary<string, int>(StringComparer.Ordinal);
        var names = new List<string>();
        var counts = new List<int>();
        var nameChars = 0;
        var entryCategories = new List<string>();
        long entries = 0;
        long seen = 0;

        await foreach (var entry in reader.ReadEntriesAsync(cancellationToken))
        {
            AddCategories(entry.ExtInf, entryCategories);
            foreach (var name in entryCategories)
            {
                if (!positions.TryGetValue(name, out var position))
                {
                    nameChars += name.Length;
                    if (names.Count >= MaxCategories || nameChars > MaxCategoryNameChars)
                        throw new M3uFormatException(M3uError.TooManyCategories);

                    position = names.Count;
                    positions.Add(name, position);
                    names.Add(name);
                    counts.Add(0);
                }

                if (entry.Url is not null)
                    counts[position]++;
            }

            if (entry.Url is not null)
                entries++;

            if (progress is not null && ++seen % ProgressInterval == 0)
                await progress(entries);
        }

        var categories = new M3uCategory[names.Count];
        for (var index = 0; index < categories.Length; index++)
            categories[index] = new M3uCategory(names[index], counts[index]);
        return new M3uIndex(categories, entries);
    }

    /// <summary>
    /// Writes the source header line, then every complete entry in a selected category in source order,
    /// as UTF-8 without a BOM and with \n line endings.
    /// </summary>
    public static async Task<long> WriteFilteredAsync(
        Stream source, Stream destination, IReadOnlySet<string> selectedCategories, CancellationToken cancellationToken)
    {
        using var reader = await M3uReader.OpenAsync(source, cancellationToken);
        await using var writer = new StreamWriter(destination, new UTF8Encoding(false), 64 * 1024, leaveOpen: true);
        await WriteLineAsync(writer, reader.HeaderLine, cancellationToken);

        var entryCategories = new List<string>();
        long written = 0;
        await foreach (var entry in reader.ReadEntriesAsync(cancellationToken))
        {
            if (entry.Url is null)
                continue;

            AddCategories(entry.ExtInf, entryCategories);
            if (!ContainsAny(selectedCategories, entryCategories))
                continue;

            await WriteLineAsync(writer, entry.ExtInf, cancellationToken);
            if (entry.Directives is not null)
            {
                foreach (var directive in entry.Directives)
                    await WriteLineAsync(writer, directive, cancellationToken);
            }

            await WriteLineAsync(writer, entry.Url, cancellationToken);
            written++;
        }

        await writer.FlushAsync(cancellationToken);
        return written;
    }

    /// <summary>Replaces <paramref name="into"/> with the distinct group-title values of an #EXTINF line.</summary>
    public static void AddCategories(string extInf, List<string> into)
    {
        into.Clear();
        for (var match = GroupTitle().Match(extInf); match.Success; match = match.NextMatch())
        {
            var name = match.Groups[1].Value;
            if (!into.Contains(name))
                into.Add(name);
        }
    }

    private static bool ContainsAny(IReadOnlySet<string> selected, List<string> names)
    {
        foreach (var name in names)
        {
            if (selected.Contains(name))
                return true;
        }

        return false;
    }

    private static async Task WriteLineAsync(StreamWriter writer, string line, CancellationToken cancellationToken)
    {
        await writer.WriteAsync(line.AsMemory(), cancellationToken);
        await writer.WriteAsync("\n".AsMemory(), cancellationToken);
    }

    // Must stay equivalent to extractCategories in client/src/m3u-parser.ts so favourites match by exact name.
    [GeneratedRegex("group-title=\"([^\"]+)\"")]
    private static partial Regex GroupTitle();
}

/// <summary>Reads the #EXTM3U header and then entries, one line at a time.</summary>
public sealed class M3uReader : IDisposable
{
    private readonly M3uLineReader lines;

    private M3uReader(M3uLineReader lines, string headerLine)
    {
        this.lines = lines;
        HeaderLine = headerLine;
    }

    /// <summary>The #EXTM3U line, including attributes such as url-tvg or x-tvg-url.</summary>
    public string HeaderLine { get; }

    public static async Task<M3uReader> OpenAsync(Stream stream, CancellationToken cancellationToken)
    {
        var lines = new M3uLineReader(stream);
        try
        {
            while (await lines.ReadLineAsync(cancellationToken) is { } line)
            {
                if (lines.LastLineTooLong)
                    break;

                var trimmed = line.TrimStart();
                if (trimmed.Length == 0)
                    continue;

                if (!IsHeader(trimmed))
                    break;

                return new M3uReader(lines, trimmed);
            }

            throw new M3uFormatException(M3uError.NotM3u);
        }
        catch
        {
            lines.Dispose();
            throw;
        }
    }

    public async IAsyncEnumerable<M3uEntry> ReadEntriesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        M3uEntry? current = null;
        while (await lines.ReadLineAsync(cancellationToken) is { } line)
        {
            switch (lines.LastLineTooLong ? LineKind.TooLong : Classify(line))
            {
                case LineKind.TooLong:
                    // An over-long line cannot be copied safely; the entry it belongs to ends without a URL.
                    if (current is not null)
                        yield return current;
                    current = null;
                    break;

                case LineKind.ExtInf:
                    if (current is not null)
                        yield return current;
                    current = new M3uEntry(line);
                    break;

                case LineKind.Directive when current is not null:
                    // Directives such as #EXTVLCOPT, #KODIPROP or #EXTGRP belong to the entry they follow.
                    (current.Directives ??= new List<string>()).Add(line);
                    break;

                case LineKind.Url when current is not null:
                    current.Url = line;
                    yield return current;
                    current = null;
                    break;
            }
        }

        if (current is not null)
            yield return current;
    }

    public void Dispose() => lines.Dispose();

    private enum LineKind { Blank, Header, ExtInf, Directive, Url, TooLong }

    private static LineKind Classify(string line)
    {
        var content = line.AsSpan().TrimStart();
        if (content.IsEmpty)
            return LineKind.Blank;
        if (content.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
            return LineKind.ExtInf;
        // Repeated #EXTM3U headers (concatenated playlists) are dropped, like stray lines outside an entry.
        if (IsHeader(content))
            return LineKind.Header;
        return content[0] == '#' ? LineKind.Directive : LineKind.Url;
    }

    private static bool IsHeader(ReadOnlySpan<char> line) =>
        line.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase) &&
        (line.Length == 7 || char.IsWhiteSpace(line[7]));
}

/// <summary>
/// Splits a UTF-8 stream into lines on \n (a trailing \r is removed) with a bounded line length, so a
/// hostile file cannot make the reader allocate without limit. A leading BOM is removed.
/// </summary>
internal sealed class M3uLineReader : IDisposable
{
    public const int MaxLineLength = 64 * 1024;
    private readonly StreamReader reader;
    private readonly char[] buffer = new char[16 * 1024];
    private readonly StringBuilder line = new();
    private int position;
    private int length;
    private bool endOfStream;
    private bool firstLine = true;

    public M3uLineReader(Stream stream)
    {
        reader = new StreamReader(
            stream, new UTF8Encoding(false, false), detectEncodingFromByteOrderMarks: false, 64 * 1024, leaveOpen: true);
    }

    /// <summary>True when the last line exceeded <see cref="MaxLineLength"/>; its text is then empty.</summary>
    public bool LastLineTooLong { get; private set; }

    public async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        line.Clear();
        LastLineTooLong = false;
        var consumed = false;

        while (true)
        {
            if (position == length)
            {
                if (!endOfStream)
                {
                    length = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
                    position = 0;
                    endOfStream = length == 0;
                }

                if (endOfStream)
                {
                    if (!consumed)
                        return null;
                    break;
                }
            }

            consumed = true;
            var available = buffer.AsSpan(position, length - position);
            var newline = available.IndexOf('\n');
            var segment = newline >= 0 ? available[..newline] : available;
            position += segment.Length;

            if (!LastLineTooLong)
            {
                // Allow one extra character for the \r of a CRLF line ending.
                if (line.Length + segment.Length > MaxLineLength + 1)
                {
                    LastLineTooLong = true;
                    line.Clear();
                }
                else
                {
                    line.Append(segment);
                }
            }

            if (newline >= 0)
            {
                position++;
                break;
            }
        }

        if (line.Length > 0 && line[^1] == '\r')
            line.Length--;

        if (firstLine)
        {
            firstLine = false;
            if (line.Length > 0 && line[0] == '\uFEFF')
                line.Remove(0, 1);
        }

        if (line.Length > MaxLineLength)
        {
            LastLineTooLong = true;
            line.Clear();
        }

        return line.ToString();
    }

    public void Dispose() => reader.Dispose();
}
