using System.Globalization;
using System.Text;

namespace Runvoke.WinUI.Core;

internal readonly record struct TerminalLogUpdate(string Message, string Mode);

/// <summary>
/// Parses the small terminal subset emitted by build tools. It keeps one bounded
/// editable line per stream and never executes terminal control sequences.
/// </summary>
internal sealed class TerminalLogParser
{
    private const int MaxColumns = 16_000;
    private const int MaxTextBytes = MaxColumns * 4;
    private const string TruncationMarker = "… [单条日志过长，已截断]";

    private readonly List<Cell> _cells = [];
    private readonly StringBuilder _csi = new();
    private ParserState _state;
    private int _cursor;
    private int _textBytes;
    private bool _truncated;
    private bool _pendingCr;
    private bool _rewritten;
    private bool _published;
    private bool _dirty;
    private string? _lastSnapshot;

    public IReadOnlyList<TerminalLogUpdate> Feed(ReadOnlySpan<char> input)
    {
        var updates = new List<TerminalLogUpdate>();
        foreach (var character in input) Consume(character, updates);
        PublishProgress(updates);
        return updates;
    }

    public IReadOnlyList<TerminalLogUpdate> Finish()
    {
        _state = ParserState.Normal;
        var updates = new List<TerminalLogUpdate>();
        if (_cells.Count > 0 || _truncated || _published) Complete(updates);
        return updates;
    }

    private void Consume(char character, List<TerminalLogUpdate> updates)
    {
        switch (_state)
        {
            case ParserState.Escape:
                if (character == '[') { _state = ParserState.Csi; _csi.Clear(); }
                else if (character == ']') _state = ParserState.Osc;
                else _state = ParserState.Normal;
                return;
            case ParserState.Csi:
                _csi.Append(character);
                if (character is >= '@' and <= '~')
                {
                    DispatchCsi(character);
                    _state = ParserState.Normal;
                }
                return;
            case ParserState.Osc:
                if (character == '\a') _state = ParserState.Normal;
                else if (character == '\u001b') _state = ParserState.OscEscape;
                return;
            case ParserState.OscEscape:
                _state = character == '\\' ? ParserState.Normal : ParserState.Osc;
                return;
        }

        if (character == '\u001b') { _state = ParserState.Escape; return; }
        switch (character)
        {
            case '\r': _cursor = 0; _pendingCr = true; return;
            case '\n': Complete(updates); return;
            case '\b': MoveCursor(Math.Max(0, _cursor - 1)); return;
            case '\t':
                if (_pendingCr) { _rewritten = true; _pendingCr = false; }
                _cursor = Math.Min(MaxColumns, ((_cursor / 8) + 1) * 8);
                return;
            case '\v':
            case '\f': Complete(updates); return;
            default: Write(character); return;
        }
    }

    private void DispatchCsi(char action)
    {
        var parameters = _csi.ToString()[..^1]
            .Split(';', StringSplitOptions.None)
            .Select(value => int.TryParse(value, out var parsed) ? parsed : 0)
            .ToArray();
        var parameter = parameters.FirstOrDefault();
        var distance = Math.Max(1, parameter);
        switch (action)
        {
            case 'C':
            case 'a': MoveCursor(_cursor + distance); break;
            case 'D': MoveCursor(Math.Max(0, _cursor - distance)); break;
            case 'G':
            case '`': MoveCursor(Math.Max(0, distance - 1)); break;
            case 'K': EraseLine(parameter); break;
        }
    }

    private void Write(char character)
    {
        if (_pendingCr) { _rewritten = true; _pendingCr = false; }
        var width = UnicodeWidth(character);
        _dirty = true;
        if (width == 0)
        {
            for (var index = Math.Min(_cursor, _cells.Count) - 1; index >= 0; index--)
            {
                if (_cells[index].Continuation) continue;
                if (_cells[index].Glyph is not null && _textBytes + Utf8Bytes(character) <= MaxTextBytes)
                {
                    var cell = _cells[index];
                    _cells[index] = cell with { Glyph = cell.Glyph + character };
                    _textBytes += Utf8Bytes(character);
                }
                else _truncated = true;
                break;
            }
            return;
        }

        if (_cursor + width > MaxColumns) { _truncated = true; _cursor = MaxColumns; return; }
        while (_cells.Count < _cursor + width) _cells.Add(Cell.Blank());
        for (var column = _cursor; column < _cursor + width; column++) ClearCell(column);
        if (_textBytes + Utf8Bytes(character) > MaxTextBytes) { _truncated = true; return; }
        _cells[_cursor] = new Cell(character.ToString(), width, false);
        for (var column = _cursor + 1; column < _cursor + width; column++)
            _cells[column] = new Cell(string.Empty, 0, true);
        _textBytes += Utf8Bytes(character);
        _cursor += width;
    }

    private void ClearCell(int column)
    {
        if (column >= _cells.Count) return;
        var start = _cells[column].Continuation ? column - 1 : column;
        if (start < 0 || start >= _cells.Count) return;
        var cell = _cells[start];
        if (cell.Glyph is not null) _textBytes = Math.Max(0, _textBytes - Encoding.UTF8.GetByteCount(cell.Glyph));
        for (var index = start; index < Math.Min(_cells.Count, start + Math.Max(1, cell.Width)); index++)
            _cells[index] = Cell.Blank();
    }

    private void EraseLine(int kind)
    {
        var start = kind switch { 1 => 0, 2 => 0, _ => _cursor };
        var end = kind switch { 1 => Math.Min(_cells.Count, _cursor + 1), _ => _cells.Count };
        if (kind is not (0 or 1 or 2)) return;
        for (var index = start; index < end; index++) ClearCell(index);
        if (kind is 0 or 2) _truncated = false;
        while (_cells.Count > 0 && !_cells[^1].Continuation && _cells[^1].Glyph is null)
            _cells.RemoveAt(_cells.Count - 1);
        _rewritten = true;
        _pendingCr = false;
        _dirty = true;
    }

    private void MoveCursor(int column)
    {
        _cursor = Math.Clamp(column, 0, MaxColumns);
        _rewritten = true;
        _pendingCr = false;
    }

    private void PublishProgress(List<TerminalLogUpdate> updates)
    {
        if (!(_rewritten || _pendingCr) || !_dirty) return;
        _dirty = false;
        var message = Message();
        if ((message.Length == 0 && !_published) || message == _lastSnapshot) return;
        _lastSnapshot = message;
        updates.Add(new TerminalLogUpdate(message, "progress"));
        _published = true;
    }

    private void Complete(List<TerminalLogUpdate> updates)
    {
        updates.Add(new TerminalLogUpdate(Message(), _published || _rewritten ? "finish" : "append"));
        _cells.Clear();
        _cursor = 0;
        _textBytes = 0;
        _truncated = false;
        _pendingCr = false;
        _rewritten = false;
        _published = false;
        _dirty = false;
        _lastSnapshot = null;
    }

    private string Message()
    {
        var message = string.Concat(_cells.Select(cell => cell.Continuation ? string.Empty : cell.Glyph ?? " "));
        if (_rewritten) message = message.TrimEnd(' ');
        return _truncated ? message + TruncationMarker : message;
    }

    private static int UnicodeWidth(char character)
    {
        if (CharUnicodeInfo.GetUnicodeCategory(character) is UnicodeCategory.Control or UnicodeCategory.NonSpacingMark) return 0;
        var code = (int)character;
        return code >= 0x1100 && (code <= 0x115f || code is >= 0x2e80 and <= 0xa4cf or >= 0xac00 and <= 0xd7a3 or >= 0xf900 and <= 0xfaff or >= 0xfe10 and <= 0xfe6f or >= 0xff00 and <= 0xff60 or >= 0xffe0 and <= 0xffe6) ? 2 : 1;
    }

    private static int Utf8Bytes(char character) => Encoding.UTF8.GetByteCount(character.ToString());

    private enum ParserState { Normal, Escape, Csi, Osc, OscEscape }

    private readonly record struct Cell(string? Glyph, int Width, bool Continuation)
    {
        public static Cell Blank() => new(null, 1, false);
    }
}
