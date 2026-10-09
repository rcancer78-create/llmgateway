using System.Text;
using LLMGateway.Core;

namespace LLMGateway.Native;

/// <summary>Bounds one native NDJSON/RPC line while retaining incremental CR/LF/CRLF delivery.
/// The NativeProcess owns the reader and remains responsible for its Windows job cleanup.</summary>
internal sealed class NativeBoundedLineReader
{
    private readonly StreamReader _reader;
    private readonly int _maximumChars;
    // StreamReader already buffers bytes. Filling a larger char request can block past
    // an available line when a full UTF-8 byte buffer decodes to fewer characters.
    private readonly char[] _character = new char[1];
    private bool _skipLf;

    public NativeBoundedLineReader(StreamReader reader, int maximumChars)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumChars);
        _reader = reader;
        _maximumChars = maximumChars;
    }

    public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        var line = new StringBuilder(Math.Min(_maximumChars, 4096));
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await _reader.ReadAsync(_character.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) return line.Length == 0 ? null : line.ToString();
            var character = _character[0];
            if (_skipLf)
            {
                _skipLf = false;
                if (character == '\n') continue;
            }
            if (character is '\r' or '\n')
            {
                _skipLf = character == '\r';
                return line.ToString();
            }
            if (line.Length == _maximumChars)
                throw new GatewayException(GatewayErrorKind.Upstream, "Нативный клиент превысил лимит строки протокола.");
            line.Append(character);
        }
    }
}
