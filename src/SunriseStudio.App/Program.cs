using System.Globalization;
using SunriseStudio.Editor;

namespace SunriseStudio.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length > 0 && args[0] == "tft") return TftCommand.Run(args[1..]);
            if (args.Contains("--help"))
            {
                Console.WriteLine("Sunrise Studio [--game PATH] [--project PATH] [--tag HEX] [--keys PATH] [--warp]");
                Console.WriteLine("Loose tags: " + TftCommand.Usage);
                Console.WriteLine("Keys: automatic from the installation; --keys accepts a Sunrise key export or unpacked executable.");
                Console.WriteLine("Open: --map HEX --mission HEX --script PATH --mission-view");
                Console.WriteLine("Preview: --native --animation HEX --category NAME --page NAME");
                Console.WriteLine("Validation: --smoke --frames N --capture PATH --capture-loading PATH --profile-frames PATH");
                Console.WriteLine("            --settings PATH --wait-for-names --pick-center");
                return 0;
            }
            var options = Parse(args);
            ApplicationConfiguration.Initialize();
            using var studio = new StudioForm(options);
            Application.Run(studio);
            if (studio.Failure is { } error) { Console.Error.WriteLine(error); return 1; }
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            if (!args.Contains("--smoke")) MessageBox.Show(error.Message, "Sunrise Studio");
            return 1;
        }
    }

    /** An unknown option or a missing value throws. */
    private static StudioOptions Parse(string[] args)
    {
        var options = new StudioOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string Value() => ++i < args.Length ? args[i] : throw new ArgumentException("Missing option value.");
            uint Hex() => uint.Parse(Value().Replace("0x", "", StringComparison.OrdinalIgnoreCase), NumberStyles.HexNumber);
            options = args[i] switch
            {
                "--game" => options with { Game = Value() },
                "--project" => options with { Project = Value() },
                "--tag" => options with { Tag = Hex() },
                "--keys" => options with { KeySource = Value() },
                "--settings" => options with { SettingsPath = Value() },
                "--capture" => options with { Capture = Path.GetFullPath(Value()) },
                "--capture-loading" => options with { CaptureLoading = Path.GetFullPath(Value()) },
                "--profile-frames" => options with { ProfileFrames = Path.GetFullPath(Value()) },
                "--frames" => options with { Frames = Math.Clamp(int.Parse(Value()), 2, 10000) },
                "--warp" => options with { Warp = true },
                "--native" => options with { NativeScene = true },
                "--animation" => options with { AnimationClip = Hex() },
                "--category" => options with { Category = Value() },
                "--page" => options with { Page = Value() },
                "--script" => options with { MissionScript = Value() },
                "--map" => options with { Map = Hex() },
                "--mission" => options with { Mission = Hex() },
                "--wait-for-names" => options with { WaitForNames = true },
                "--pick-center" => options with { PickCenter = true },
                "--mission-view" => options with { MissionView = true },
                "--smoke" => options with { Smoke = true },
                _ => throw new ArgumentException($"Unknown option: {args[i]}")
            };
        }
        return options;
    }
}
