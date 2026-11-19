using System.Text;

namespace HolySheet.Desktop.Host;

/// <summary>
/// Entry point of <c>holysheet-host</c>. Reads JSON-RPC requests from stdin, one per line, and writes responses and
/// notifications to stdout, one per line. Everything else that would reach stdout goes to stderr instead,
/// so a stray write can never corrupt the protocol. The process ends when stdin closes.
/// </summary>
public static class Program
{
    public static async Task<int> Main()
    {
        // Referencing the Sep backend from here is what puts its assembly next to the host; the engine finds it by name.
        _ = typeof(HolySheet.Csv.SepCsvParser);

        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var protocolOut = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = false };
        Console.SetOut(Console.Error);
        using var protocolIn = new StreamReader(Console.OpenStandardInput(), utf8);
        using var server = new RpcServer(protocolIn, protocolOut);
        await server.RunAsync().ConfigureAwait(false);
        return 0;
    }
}
