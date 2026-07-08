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

    public void Send(char c) => _port.Write(c.ToString());

    public void SendLine(string line)
    {
        _port.WriteLine(line);
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
                // Lines starting with '!' are status messages — always print, never queue.
                if (_capturing && !line.StartsWith('!'))
                    _captureQueue.Enqueue(line);
                else
                    Console.WriteLine($"\r[A] {line}"); // \r clears any partial prompt
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
