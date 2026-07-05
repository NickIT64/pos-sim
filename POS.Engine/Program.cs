using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using POS.Shared;

namespace POS.Engine
{
    internal class Program
    {
        static TcpListener _listener;
        static bool _running = true;

        static void Main(string[] args)
        {
            Console.Title = "POS Engine";
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("=================================");
            Console.WriteLine("       POS ENGINE v1.0          ");
            Console.WriteLine("=================================");
            Console.ResetColor();

            PosLogger.Source = "ENGINE";
            PosLogger.Info("POS Engine starting up...");
            PosLogger.Info($"Log directory: {PosLogger.GetLogPath()}");

            _listener = new TcpListener(IPAddress.Any, 9000);
            _listener.Start();

            PosLogger.Info("Listening on port 9000...");

            while (_running)
            {
                try
                {
                    var client = _listener.AcceptTcpClient();
                    var thread = new Thread(() => HandleClient(client));
                    thread.IsBackground = true;
                    thread.Start();
                }
                catch { break; }
            }
        }

        static void HandleClient(TcpClient client)
        {
            var endpoint = client.Client.RemoteEndPoint.ToString();
            PosLogger.Info($"Client connected: {endpoint}");

            var stream = client.GetStream();
            var buffer = new byte[1024];

            try
            {
                while (true)
                {
                    int bytesRead = stream.Read(buffer, 0, buffer.Length);
                    if (bytesRead == 0) break;

                    string message = Encoding.UTF8.GetString(buffer, 0, bytesRead).Trim();
                    PosLogger.Info($"Received from {endpoint}: {message}");

                    string response = ProcessCommand(message);

                    byte[] responseBytes = Encoding.UTF8.GetBytes(response);
                    stream.Write(responseBytes, 0, responseBytes.Length);
                    PosLogger.Info($"Sent: {response}");
                }
            }
            catch (Exception ex)
            {
                PosLogger.Error($"Client error: {ex.Message}");
            }
            finally
            {
                PosLogger.Warn($"Client disconnected: {endpoint}");
                client.Close();
            }
        }

        static string ProcessCommand(string command)
        {
            PosLogger.Debug($"Processing command: {command}");
            switch (command.ToUpper())
            {
                case "PING":
                    return "PONG";
                case "STATUS":
                    return "ENGINE:RUNNING";
                case "REBOOT":
                case "RESTART":
                    new System.Threading.Timer(_ => {
                        PosLogger.Warn("Engine shutting down by remote command...");
                        _listener.Stop();
                        Environment.Exit(0);
                    }, null, 500, System.Threading.Timeout.Infinite);
                    return command.ToUpper() == "REBOOT" ? "ENGINE:REBOOTING" : "ENGINE:RESTARTING";
                case "SHUTDOWN":
                    new System.Threading.Timer(_ => {
                        PosLogger.Warn("Engine shutdown by remote command.");
                        _listener.Stop();
                        Environment.Exit(0);
                    }, null, 500, System.Threading.Timeout.Infinite);
                    return "ENGINE:SHUTTING_DOWN";
                default:
                    PosLogger.Warn($"Unknown command received: {command}");
                    return $"ENGINE:UNKNOWN_CMD:{command}";
            }
        }
    }
}