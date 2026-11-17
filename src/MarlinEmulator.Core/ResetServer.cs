using System.Net;
using System.Net.Sockets;
using System.Text;

namespace MarlinEmulator.Core;

// Separate loopback control channel, never forwarded to Marlin's G-code parser.
public sealed class ResetServer : IAsyncDisposable
{
    private readonly TcpListener listener;
    private readonly Func<Task> reset;
    private readonly CancellationTokenSource lifetime=new();
    private Task? worker;
    public ResetServer(int port, Func<Task> reset) { listener=new(IPAddress.Loopback,port); this.reset=reset; }
    public void Start() { listener.Start(); worker=Run(); }
    private async Task Run()
    {
        try
        {
            while(!lifetime.IsCancellationRequested)
            {
                using var client=await listener.AcceptTcpClientAsync(lifetime.Token);
                try
                {
                    using var reader=new StreamReader(client.GetStream(),Encoding.ASCII,false,1024,true);
                    using var writer=new StreamWriter(client.GetStream(),Encoding.ASCII,1024,true) { AutoFlush=true,NewLine="\n" };
                    await writer.WriteLineAsync("MARLIN-RESET/1");
                    // Bounded request: no arbitrary-length lines from network clients.
                    var request=new StringBuilder();
                    using var timeout=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); timeout.CancelAfter(3000);
                    char[] one=new char[1];
                    while(request.Length<32 && await reader.ReadAsync(one.AsMemory(),timeout.Token)>0)
                    { if(one[0]=='\n')break; request.Append(one[0]); }
                    if(request.ToString().TrimEnd('\r')!="HARD_RESET") { await writer.WriteLineAsync("ERROR unsupported request"); continue; }
                    await reset();
                    await writer.WriteLineAsync("OK RESET");
                }
                catch(Exception ex) when(ex is IOException or SocketException or OperationCanceledException or InvalidOperationException) { }
            }
        }
        catch(Exception ex) when(lifetime.IsCancellationRequested && ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }
    public async ValueTask DisposeAsync() { lifetime.Cancel(); listener.Stop(); if(worker is not null)await worker; lifetime.Dispose(); }
}
