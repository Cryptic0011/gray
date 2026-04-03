using Gmux.Core.Terminal.ConPty;
using Gmux.Core.Terminal.VtParser;
using Microsoft.Win32.SafeHandles;

namespace Gmux.Core.Terminal;

/// <summary>
/// Orchestrates a ConPTY process + VT parser + terminal buffer into a complete terminal session.
/// </summary>
public class TerminalSession : IDisposable
{
    private PseudoConsole? _console;
    private PseudoConsoleProcess? _process;
    private readonly AnsiParser _parser;
    private FileStream? _outputStream;
    private FileStream? _inputStream;
    private CancellationTokenSource? _readCts;
    private bool _disposed;

    public TerminalBuffer Buffer { get; }
    public string? WorkingDirectory { get; private set; }
    public string Command { get; private set; } = string.Empty;
    public bool IsRunning => _process != null && !_process.HasExited;
    public int? ProcessId => _process?.ProcessId;

    public event Action<TerminalSession>? OutputChanged;
    public event Action<TerminalSession>? ProcessExited;
    public event Action<TerminalSession, string>? InputSent;

    public TerminalSession(int columns = 120, int rows = 30, int scrollbackSize = 5000)
    {
        Buffer = new TerminalBuffer(columns, rows, scrollbackSize);
        _parser = new AnsiParser(Buffer);
    }

    public void ApplySettings(int scrollbackSize)
    {
        Buffer.SetMaxScrollback(scrollbackSize);
    }

    public async Task StartAsync(string command = "cmd.exe", string? workingDirectory = null, CancellationToken ct = default)
    {
        WorkingDirectory = workingDirectory;
        Command = command;

        _console = PseudoConsole.Create((short)Buffer.Columns, (short)Buffer.Rows);
        _process = ProcessFactory.Start(_console, command, workingDirectory);

        _outputStream = new FileStream(_console.OutputReadSide, FileAccess.Read);
        _inputStream = new FileStream(_console.InputWriteSide, FileAccess.Write);

        _readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Start reading output in the background
        _ = ReadOutputAsync(_readCts.Token);

        // Monitor process exit
        _ = MonitorProcessAsync(ct);
    }

    private async Task ReadOutputAsync(CancellationToken ct)
    {
        var byteBuffer = new byte[4096];
        var charBuffer = new char[4096];
        var decoder = System.Text.Encoding.UTF8.GetDecoder();
        try
        {
            while (!ct.IsCancellationRequested && _outputStream != null)
            {
                int bytesRead = await _outputStream.ReadAsync(byteBuffer, ct);
                if (bytesRead == 0) break;

                // Decode UTF-8 bytes to chars, handling multi-byte sequences across reads
                int charsDecoded = decoder.GetChars(byteBuffer, 0, bytesRead, charBuffer, 0);
                _parser.ProcessChars(charBuffer.AsSpan(0, charsDecoded));
                OutputChanged?.Invoke(this);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { } // Pipe closed
    }

    private async Task MonitorProcessAsync(CancellationToken ct)
    {
        if (_process == null) return;
        try
        {
            await _process.WaitForExitAsync(ct);
            ProcessExited?.Invoke(this);
        }
        catch (OperationCanceledException) { }
    }

    public void SendInput(string text)
    {
        if (_inputStream == null || _disposed) return;
        InputSent?.Invoke(this, text);
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        SendInput(bytes);
    }

    public void SendInput(byte[] data)
    {
        if (_inputStream == null || _disposed) return;
        try
        {
            _inputStream.Write(data);
            _inputStream.Flush();
        }
        catch (IOException) { }
    }

    public void Resize(int columns, int rows)
    {
        Buffer.Resize(columns, rows);
        _console?.Resize((short)columns, (short)rows);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _readCts?.Cancel();
        _readCts?.Dispose();

        _inputStream?.Dispose();
        _outputStream?.Dispose();

        if (_process is { HasExited: false })
            _process.Kill();

        _process?.Dispose();
        _console?.Dispose();
    }
}
