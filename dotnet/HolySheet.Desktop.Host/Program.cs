using System.Text;

namespace HolySheet.Desktop.Host;

public static class Program
{
    public static async Task<int> Main()
    {
        _ = typeof(HolySheet.Csv.SepCsvParser);

        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var protocolOut = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = false };
        Console.SetOut(Console.Error);
        using var protocolIn = new StreamReader(Console.OpenStandardInput(), utf8);
        var server = new RpcServer(protocolIn, protocolOut);
        await server.RunAsync().ConfigureAwait(false);
        return 0;
    }
}
