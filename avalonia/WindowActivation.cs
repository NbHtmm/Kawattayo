using System.Diagnostics;
using System.IO.Pipes;

namespace ShareFileWatch;

internal enum WindowRequest : byte { Monitor = 1, Collector = 2 }

internal static class WindowActivation
{
    // 別セッションのアプリを起こさず、同一ユーザーのパイプだけを使用する。
    private static readonly string PipeName = "Kawattayo.Window." + Process.GetCurrentProcess().SessionId;
    internal static WindowRequest FromArguments(string[] args) =>
        args is ["--show-collector"] ? WindowRequest.Collector : WindowRequest.Monitor;

    internal static bool Send(WindowRequest request)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(1800);
            client.WriteByte((byte)request);
            return true;
        }
        catch (Exception error) when (error is IOException or TimeoutException or UnauthorizedAccessException) { return false; }
    }

    internal static async Task Listen(Action<WindowRequest> activate, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(stop).ConfigureAwait(false);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
                deadline.CancelAfter(TimeSpan.FromSeconds(2));
                byte[] value = new byte[1];
                if (await server.ReadAsync(value, deadline.Token).ConfigureAwait(false) == 1 &&
                    value[0] is (byte)WindowRequest.Monitor or (byte)WindowRequest.Collector)
                    activate((WindowRequest)value[0]);
            }
            catch (OperationCanceledException) { }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                if (!stop.IsCancellationRequested)
                {
                    try { await Task.Delay(500, stop).ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                }
            }
        }
    }
}
