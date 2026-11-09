// Adapted from WindowsTerminalControl by 0x5bfa (MIT). See licenses/WindowsTerminalControl-LICENSE.txt.
using Microsoft.Win32.SafeHandles;
using System.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

using Microsoft.Terminal.Wpf;

namespace IdeaLite;

/// <summary>
/// Runs a local process through the Windows 10+ system ConPTY implementation.
/// </summary>
public sealed class NativeTerminalSession : ITerminalConnection, IDisposable
{
    private readonly string _commandLine;
    private readonly string _workingDirectory;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private CancellationTokenSource? _lifetimeCancellation;
    private FileStream? _inputStream;
    private FileStream? _outputStream;
    private SafeProcessHandle? _processHandle;
    private SafePseudoConsoleHandle? _pseudoConsole;
    private Task? _exitMonitorTask;
    private Task? _outputPumpTask;
    private int _disposed;
    private int _started;
    private TerminalSize _initialSize = new(100, 30);
    private volatile bool _bracketedPaste;
    private string _pasteModeTail = "";

    /// <summary>
    /// Initializes a new instance of the <see cref="NativeTerminalSession"/> class.
    /// </summary>
    /// <param name="commandLine">The command line to run.</param>
    /// <param name="workingDirectory">The initial working directory.</param>
    public NativeTerminalSession(string commandLine, string workingDirectory)
    {
        _commandLine = commandLine;
        _workingDirectory = workingDirectory;
    }

    /// <inheritdoc/>
    public event EventHandler<TerminalDataEventArgs>? OutputReceived;

    /// <inheritdoc/>
    public event EventHandler<TerminalOutputEventArgs>? TerminalOutput;

    /// <inheritdoc/>
    public event EventHandler? Exited;

    /// <inheritdoc/>
    public Task StartAsync(TerminalSize initialSize, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        cancellationToken.ThrowIfCancellationRequested();

        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("The ConPTY session has already been started.");
        }

        SafeFileHandle? pseudoConsoleInput = null;
        SafeFileHandle? pseudoConsoleOutput = null;
        SafeFileHandle? hostInput = null;
        SafeFileHandle? hostOutput = null;

        try
        {
            CreatePipePair(out pseudoConsoleInput, out hostInput);
            CreatePipePair(out hostOutput, out pseudoConsoleOutput);

            var result = NativeMethods.CreatePseudoConsole(ToCoord(initialSize), pseudoConsoleInput, pseudoConsoleOutput, 0, out var pseudoConsoleHandle);
            Marshal.ThrowExceptionForHR(result);
            _pseudoConsole = new SafePseudoConsoleHandle(pseudoConsoleHandle);

            pseudoConsoleInput.Dispose();
            pseudoConsoleInput = null;
            pseudoConsoleOutput.Dispose();
            pseudoConsoleOutput = null;

            _processHandle = StartProcess(_pseudoConsole, _commandLine, _workingDirectory);
            _inputStream = new FileStream(hostInput, FileAccess.Write, 4096, false);
            hostInput = null;
            _outputStream = new FileStream(hostOutput, FileAccess.Read, 4096, false);
            hostOutput = null;

            _lifetimeCancellation = new CancellationTokenSource();
            _outputPumpTask = PumpOutputAsync(_lifetimeCancellation.Token);
            _exitMonitorTask = MonitorProcessExitAsync();

            return Task.CompletedTask;
        }
        catch
        {
            pseudoConsoleInput?.Dispose();
            pseudoConsoleOutput?.Dispose();
            hostInput?.Dispose();
            hostOutput?.Dispose();
            Dispose();
            throw;
        }
    }

    /// <inheritdoc/>
    public async ValueTask WriteAsync(string data, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        var inputStream = _inputStream ?? throw new InvalidOperationException("The ConPTY session has not been started.");
        if (data.Length == 0)
        {
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(data);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await inputStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await inputStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc/>
    public async ValueTask ResizeAsync(TerminalSize size, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        var pseudoConsole = _pseudoConsole ?? throw new InvalidOperationException("The ConPTY session has not been started.");
        await Task.Run(() =>
        {
            var result = NativeMethods.ResizePseudoConsole(pseudoConsole, ToCoord(size));
            Marshal.ThrowExceptionForHR(result);
        }, cancellationToken).ConfigureAwait(false);
    }

    private void Publish(string text)
    {
        if (_disposed != 0) return;
        string modes = _pasteModeTail + text;
        int enabled = modes.LastIndexOf("\x1b[?2004h", StringComparison.Ordinal), disabled = modes.LastIndexOf("\x1b[?2004l", StringComparison.Ordinal);
        if (enabled >= 0 || disabled >= 0) _bracketedPaste = enabled > disabled;
        _pasteModeTail = modes.Length <= 16 ? modes : modes[^16..];
        TerminalOutput?.Invoke(this, new TerminalOutputEventArgs(text));
        OutputReceived?.Invoke(this, new TerminalDataEventArgs(text));
    }

    internal string PreparePaste(string text)
    {
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        return _bracketedPaste ? "\x1b[200~" + text + "\x1b[201~" : text.Replace('\n', '\r');
    }

    public void Start()
    {
        if (_disposed != 0 || _started != 0) return;
        try { StartAsync(_initialSize, CancellationToken.None).GetAwaiter().GetResult(); }
        catch (Exception ex) { Publish("\r\n[terminal error] " + ex.Message + "\r\n"); Dispose(); }
    }
    public async void WriteInput(string data)
    {
        if (_disposed != 0 || _started == 0) return;
        try { await WriteAsync(data, CancellationToken.None); }
        catch (Exception ex) { if (_disposed == 0) Publish("\r\n[terminal error] " + ex.Message + "\r\n"); }
    }
    public async void Resize(uint rows, uint columns)
    {
        if (_disposed != 0 || rows is 0 or > (uint)short.MaxValue || columns is 0 or > (uint)short.MaxValue) return;
        _initialSize = new((ushort)columns, (ushort)rows);
        if (_started == 0) return;
        try { await ResizeAsync(_initialSize, CancellationToken.None); }
        catch (Exception ex) { if (_disposed == 0) Publish("\r\n[terminal error] " + ex.Message + "\r\n"); }
    }
    public void Close() => Dispose();

    private static void CreatePipePair(out SafeFileHandle readHandle, out SafeFileHandle writeHandle)
    {
        if (!NativeMethods.CreatePipe(out readHandle, out writeHandle, IntPtr.Zero, 0))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreatePipe failed.");
        }

        if (!NativeMethods.SetHandleInformation(readHandle, NativeMethods.HandleFlagInherit, 0) || !NativeMethods.SetHandleInformation(writeHandle, NativeMethods.HandleFlagInherit, 0))
        {
            var error = Marshal.GetLastPInvokeError();
            readHandle.Dispose();
            writeHandle.Dispose();
            throw new Win32Exception(error, "SetHandleInformation failed.");
        }
    }

    private static SafeProcessHandle StartProcess(SafePseudoConsoleHandle pseudoConsole, string commandLine, string workingDirectory)
    {
        nuint attributeListSize = 0;
        _ = NativeMethods.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeListSize);
        var attributeList = Marshal.AllocHGlobal(checked((nint)attributeListSize));

        try
        {
            if (!NativeMethods.InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeListSize))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "InitializeProcThreadAttributeList failed.");
            }

            if (!NativeMethods.UpdateProcThreadAttribute(
                attributeList, 0, NativeMethods.ProcThreadAttributePseudoConsole, pseudoConsole.DangerousGetHandle(), (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "UpdateProcThreadAttribute failed.");
            }

            var startupInfo = new NativeMethods.StartupInfoEx
            {
                StartupInfo = new NativeMethods.StartupInfo
                {
                    Size = Marshal.SizeOf<NativeMethods.StartupInfoEx>(),
                    // 防止从终端/重定向输出启动时继承父进程句柄、绕过 ConPTY。
                    Flags = 0x00000100
                },
                AttributeList = attributeList
            };

            var mutableCommandLine = new StringBuilder(commandLine);
            if (!NativeMethods.CreateProcess(
                null, mutableCommandLine, IntPtr.Zero, IntPtr.Zero, false, NativeMethods.ExtendedStartupInfoPresent | NativeMethods.CreateUnicodeEnvironment,
                IntPtr.Zero, workingDirectory, ref startupInfo, out var processInformation))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), $"CreateProcess failed for '{commandLine}'.");
            }

            using var threadHandle = new SafeKernelHandle(processInformation.Thread, true);
            return new SafeProcessHandle(processInformation.Process, true);
        }
        finally
        {
            NativeMethods.DeleteProcThreadAttributeList(attributeList);
            Marshal.FreeHGlobal(attributeList);
        }
    }

    private async Task PumpOutputAsync(CancellationToken cancellationToken)
    {
        var outputStream = _outputStream!;
        var bytes = new byte[32 * 1024];
        var decoder = Encoding.UTF8.GetDecoder();
        var characters = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];

        try
        {
            while (true)
            {
                var bytesRead = await outputStream.ReadAsync(bytes, cancellationToken).ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    break;
                }

                var charactersDecoded = decoder.GetChars(bytes.AsSpan(0, bytesRead), characters, false);
                if (charactersDecoded > 0)
                {
                    Publish(new string(characters, 0, charactersDecoded));
                }
            }

            var finalCharacters = decoder.GetChars(ReadOnlySpan<byte>.Empty, characters, true);
            if (finalCharacters > 0)
            {
                Publish(new string(characters, 0, finalCharacters));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (_disposed != 0)
        {
        }
        catch (Exception exception)
        {
            Publish("\r\n\x1b[31m[terminal error] " + exception.Message + "\x1b[0m\r\n");
        }
    }

    private async Task MonitorProcessExitAsync()
    {
        try
        {
            var processHandle = _processHandle!;
            var waitResult = await Task.Run(() => NativeMethods.WaitForSingleObject(processHandle, NativeMethods.Infinite)).ConfigureAwait(false);
            if (waitResult == NativeMethods.WaitFailed)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "WaitForSingleObject failed.");
            }

            if (_disposed == 0)
            {
                Publish("\r\n\x1b[90m[process exited]\x1b[0m\r\n");
                Exited?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (ObjectDisposedException) when (_disposed != 0)
        {
        }
        catch (Exception exception) when (_disposed == 0)
        {
            Publish("\r\n\x1b[31m[terminal error] " + exception.Message + "\x1b[0m\r\n");
        }
    }

    private static NativeMethods.Coord ToCoord(TerminalSize size)
    {
        return new NativeMethods.Coord(checked((short)size.Columns), checked((short)size.Rows));
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetimeCancellation?.Cancel();
        _inputStream?.Dispose();
        _pseudoConsole?.Dispose();
        _outputStream?.Dispose();
        _processHandle?.Dispose();
        _lifetimeCancellation?.Dispose();
        _inputStream = null;
        _outputStream = null;
        _processHandle = null;
        _pseudoConsole = null;
        _lifetimeCancellation = null;
        _outputPumpTask = null;
        _exitMonitorTask = null;
    }

    private sealed class SafePseudoConsoleHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal SafePseudoConsoleHandle(IntPtr value) : base(true)
        {
            SetHandle(value);
        }

        protected override bool ReleaseHandle()
        {
            NativeMethods.ClosePseudoConsole(handle);
            return true;
        }
    }

    private sealed class SafeKernelHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal SafeKernelHandle(IntPtr value, bool ownsHandle) : base(ownsHandle)
        {
            SetHandle(value);
        }

        protected override bool ReleaseHandle()
        {
            return NativeMethods.CloseHandle(handle);
        }
    }

    private static class NativeMethods
    {
        internal const uint HandleFlagInherit = 0x00000001;
        internal const uint ExtendedStartupInfoPresent = 0x00080000;
        internal const uint CreateUnicodeEnvironment = 0x00000400;
        internal const uint Infinite = 0xFFFFFFFF;
        internal const uint WaitFailed = 0xFFFFFFFF;
        internal const nuint ProcThreadAttributePseudoConsole = 0x00020016;

        [StructLayout(LayoutKind.Sequential)]
        internal readonly struct Coord(short x, short y)
        {
            internal readonly short X = x;
            internal readonly short Y = y;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct StartupInfo
        {
            internal int Size;
            internal string? Reserved;
            internal string? Desktop;
            internal string? Title;
            internal int X;
            internal int Y;
            internal int XSize;
            internal int YSize;
            internal int XCountChars;
            internal int YCountChars;
            internal int FillAttribute;
            internal int Flags;
            internal short ShowWindow;
            internal short Reserved2;
            internal IntPtr Reserved2Pointer;
            internal IntPtr StandardInput;
            internal IntPtr StandardOutput;
            internal IntPtr StandardError;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct StartupInfoEx
        {
            internal StartupInfo StartupInfo;
            internal IntPtr AttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct ProcessInformation
        {
            internal IntPtr Process;
            internal IntPtr Thread;
            internal uint ProcessId;
            internal uint ThreadId;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreatePipe(out SafeFileHandle readPipe, out SafeFileHandle writePipe, IntPtr pipeAttributes, uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr pseudoConsole);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern int ResizePseudoConsole(SafePseudoConsoleHandle pseudoConsole, Coord size);

        [DllImport("kernel32.dll")]
        internal static extern void ClosePseudoConsole(IntPtr pseudoConsole);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool InitializeProcThreadAttributeList(IntPtr attributeList, int attributeCount, uint flags, ref nuint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UpdateProcThreadAttribute(IntPtr attributeList, uint flags, nuint attribute, IntPtr value, nuint size, IntPtr previousValue, IntPtr returnSize);

        [DllImport("kernel32.dll")]
        internal static extern void DeleteProcThreadAttributeList(IntPtr attributeList);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreateProcess(
            string? applicationName, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
            uint creationFlags, IntPtr environment, string currentDirectory, ref StartupInfoEx startupInfo, out ProcessInformation processInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr handle);
    }
}

public readonly record struct TerminalSize(ushort Columns, ushort Rows);
public sealed class TerminalDataEventArgs(string data) : EventArgs { public string Data { get; } = data; }
