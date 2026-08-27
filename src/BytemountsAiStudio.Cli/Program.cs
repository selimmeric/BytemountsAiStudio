// Bytemounts Studio CLI
//
// Faz 0'in kabul kriteri bu araca bagli: `run workflow shorts-fake --topic "test"`
// komutu, sahte provider'larla uctan uca gecerli bir mp4 uretecek (P0-27).
// Su an yalnizca iskelet.

using System.Globalization;

var command = args.Length > 0 ? args[0] : "help";

return command switch
{
    "version" => Version(),
    "help" or "--help" or "-h" => Help(),
    _ => Unknown(command),
};

static int Version()
{
    var version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0";
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"bytemounts-studio {version}"));
    return 0;
}

static int Help()
{
    Console.WriteLine("""
        Bytemounts Studio CLI

        Kullanim:
          bmstudio version              surum bilgisi
          bmstudio help                 bu yardim

        Planlanan (Faz 0):
          bmstudio run workflow <anahtar> --topic "<konu>"   workflow calistir
          bmstudio render <timeline.json> --out <cikti.mp4>  timeline render et
          bmstudio graph <timeline.json> --dot <cikti.dot>   IR grafigini dok
        """);
    return 0;
}

static int Unknown(string command)
{
    Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
        $"Bilinmeyen komut: {command}. 'bmstudio help' deneyin."));
    return 2;
}
