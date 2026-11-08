using System.Globalization;
using SunriseStudio.Packages;
using SunriseStudio.Packages.Registry;
using SunriseStudio.Tags.Graph;
using SunriseStudio.Tags.Tft;

namespace SunriseStudio.App;

/** `tft export|import|upgrade|clone`: loose tag files without opening the window. */
internal static class TftCommand
{
    public const string Usage = "tft export FILE.tft [OUT.tft.json] | tft import FILE.tft.json [OUT.tft] | tft upgrade FILE.tft [--drop]"
        + " | tft clone GAME TAG NAME [CONTENT_DIR]";

    public static int Run(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("Usage: " + Usage); return 2; }
        if (args[0] == "clone" && args.Length is 4 or 5)
        {
            uint tag = uint.Parse(args[2].Replace("0x", "", StringComparison.OrdinalIgnoreCase), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            using var catalog = PackageCatalog.Open(args[1]);
            var file = TftClone.Clone([tag], new CatalogTagSource(catalog), StudioRegistry.Index, _ => args[3]).Single();
            string output = Path.Combine(args.Length == 5 ? args[4] : ".", file.Name.Replace('/', Path.DirectorySeparatorChar) + TftNames.FileExtension);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            File.WriteAllBytes(output, file.Write());
            Console.WriteLine(output);
            return 0;
        }
        string input = Path.GetFullPath(args[1]);
        switch (args[0])
        {
            case "export" when args.Length is 2 or 3:
            {
                string output = args.Length == 3 ? args[2] : input + ".json";
                File.WriteAllBytes(output, TftText.Export(TftFile.Read(File.ReadAllBytes(input))));
                Console.WriteLine(output);
                return 0;
            }
            case "import" when args.Length is 2 or 3:
            {
                string output = args.Length == 3 ? args[2]
                    : input.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? input[..^".json".Length] : input + ".tft";
                File.WriteAllBytes(output, TftText.Import(File.ReadAllBytes(input)).Write());
                Console.WriteLine(output);
                return 0;
            }
            case "upgrade" when args.Length == 2 || (args.Length == 3 && args[2] == "--drop"):
            {
                var result = TftUpgrade.Run(TftFile.Read(File.ReadAllBytes(input)), StudioRegistry.Index, StudioRegistry.BuildId);
                foreach (string dropped in result.Dropped) Console.Error.WriteLine("dropped: " + dropped);
                if (!result.Changed) { Console.WriteLine("current: " + input); return 0; }
                // A lossy upgrade writes only when asked to.
                if (result.Dropped.Count > 0 && args.Length == 2) { Console.Error.WriteLine("Not written; pass --drop to accept."); return 1; }
                File.WriteAllBytes(input, result.File.Write());
                Console.WriteLine("upgraded: " + input);
                return 0;
            }
        }
        Console.Error.WriteLine("Usage: " + Usage);
        return 2;
    }
}
