using System.Collections.Concurrent;
using System.IO.Ports;

namespace DrammenMJKConfig;

sealed class ArduinoConnection : IDisposable
{
    private readonly SerialPort _port;
    private Thread? _readThread;
    private volatile bool _running;
    private volatile bool _capturing;
    private readonly ConcurrentQueue<string> _captureQueue = new();

    public ArduinoConnection(string portName, int baudRate = 115200)
    {
        _port = new SerialPort(portName, baudRate)
        {
            ReadTimeout  = 500,
            WriteTimeout = 1000,
            NewLine      = "\n"
        };
    }

    public void Open()
    {
        _port.Open();
        _running = true;
        _readThread = new Thread(ReadLoop)
        {
            IsBackground = true,
            Name = "ArduinoRead"
        };
        _readThread.Start();
    }

    public void Send(char c) { if (EnsureOpen()) _port.Write(c.ToString()); }

    // A dropped write (port gone) is treated like no response -- callers
    // already handle a null reply -- instead of crashing the whole tool.
    public void SendLine(string line)
    {
        if (!EnsureOpen()) return;
        try { _port.WriteLine(line); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            Console.WriteLine($"\r[!] Write failed: {ex.Message}");
        }
    }

    // The USB serial port has been seen to drop and reappear mid-session
    // (same COM name). Reopen it transparently on the next command. Opening
    // resets the Uno (DTR), so wait for it to boot before sending anything.
    bool EnsureOpen()
    {
        if (_port.IsOpen && _readThread is { IsAlive: true }) return true;
        Console.WriteLine($"\r[!] {_port.PortName} closed -- reconnecting...");
        try
        {
            if (_port.IsOpen) _port.Close();
            Open();
            Thread.Sleep(2000); // ponytail: fixed boot wait, poll PING if it proves too short/long
            Console.WriteLine($"\r[!] Reconnected on {_port.PortName}.");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\r[!] Reconnect failed: {ex.Message} -- check the USB cable, then retry.");
            return false;
        }
    }

    // Redirect Arduino output into a queue instead of printing it.
    // Use before binary/structured exchanges; call StopCapture when done.
    public void StartCapture()
    {
        while (_captureQueue.TryDequeue(out _)) { } // flush stale data
        _capturing = true;
    }

    public void StopCapture() => _capturing = false;

    // Wait up to timeoutMs for the next line captured from Arduino.
    // Returns null on timeout.
    public string? GetCapturedLine(int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (_captureQueue.TryDequeue(out string? line))
                return line;
            Thread.Sleep(10);
        }
        return null;
    }

    private void ReadLoop()
    {
        while (_running)
        {
            try
            {
                string line = _port.ReadLine().TrimEnd('\r', '\n');
                if (line.StartsWith('!'))
                    Console.WriteLine($"\r{line.Substring(1)}");  // status: strip '!', print as plain text
                else if (_capturing)
                    _captureQueue.Enqueue(line);                  // protocol response: queue for caller
                else
                    Console.WriteLine($"\r[A] {line}");           // relay mode: label as Arduino output
            }
            catch (TimeoutException)
            {
                // No data available — normal, keep looping
            }
            catch (Exception) when (!_running)
            {
                break; // Port closed during shutdown
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\r[!] Connection error: {ex.Message}");
                break;
            }
        }
    }

    public void Dispose()
    {
        _running = false;
        _readThread?.Join(1000);
        try { _port.Close(); } catch { }
        _port.Dispose();
    }
}
