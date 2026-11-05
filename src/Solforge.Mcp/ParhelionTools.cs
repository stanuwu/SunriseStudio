using System.Text.Json.Nodes;
using System.Text;
using Solforge.Weapons;

namespace Solforge.Mcp;

/** MCP access to project-local Solforge and native Parhelion recipes. */
public static partial class SolforgeTools
{
    private const long MaximumRecipeBytes = 1 << 20;

    private static IEnumerable<McpTool> ParhelionTools(ISolforgeHost host)
    {
        yield return new McpTool("get_parhelion_context",
            "Project-specific Parhelion guidance: native recipe authoring, the project skill and source schema, and how package output is staged.",
            McpJson.Schema(), false, (_, _) => Task.FromResult(McpToolResult.Text(
                "This project supports two recipe paths: Solforge weapon-clone recipes (*.weapon.json) and Sundial Parhelion's native recipe schema (*.parhelion.json). Native recipes live in the active project's recipes/ folder and are validated by the bundled Parhelion library. " +
                "Read skills/solforge-new-weapon/SKILL.md for Solforge weapon authoring. For Parhelion weapons or subclasses, read the relevant Solforge skill and third_party/sundial/crates/parhelion/README.md, then use create_parhelion_recipe and build_parhelion_packages. Package artifacts are verified and staged under the active project's build/parhelion/ directory; MCP does not install them. " +
                "The existing validate_parhelion_weapon_recipe and start_parhelion_weapon_recipe tools open Solforge's own weapon-clone editor workflow for the legacy Solforge recipe format.")));

        yield return new McpTool("get_parhelion_subclass_context",
            "Explain Parhelion's subclass authoring support and route subclass work to the matching Solforge MCP system.",
            McpJson.Schema(), false, (_, _) => Task.FromResult(McpToolResult.Text(
                "Sundial's Parhelion recipe format supports subclass ability and attunement choices, node-by-node paths, perks, gameplay values, visuals, class/equipping, and artwork. Use create_parhelion_recipe with the complete recipe JSON (kind=\"subclass\") to validate and save it, then build_parhelion_packages to compile and stage output. " +
                "Read skills/solforge-new-subclass/SKILL.md. For Solforge server subclass content, use get_subclass_layout and save_subclass_definition for explicit MCP controls; update_content can then patch it, validate_content checks it, check_subclass_build checks a selected build, and export_content exports server data. " +
                "That server content model is separate from a Parhelion subclass package recipe. Package building only stages output under the project and does not install it.")));

        yield return new McpTool("create_parhelion_recipe",
            "Validate and save a complete Parhelion recipe JSON (weapon, subclass, armor, or other supported kind) under the active project's recipes folder. The bundled Parhelion parser validates its native schema before writing.",
            McpJson.Schema(("file_name", "string", "Recipe filename ending in .parhelion.json; saved under recipes/", true),
                ("recipe_json", "string", "Complete JSON document in Sundial Parhelion's recipe schema", true)), true,
            async (args, cancel) =>
            {
                string? name = McpJson.String(args, "file_name");
                string? encoded = McpJson.String(args, "recipe_json");
                if (!TryRecipePath(host.ProjectRoot, name, out string? path, out string? relative, out string? error)) return McpToolResult.Error(error!);
                if (string.IsNullOrWhiteSpace(encoded) || Encoding.UTF8.GetByteCount(encoded) > MaximumRecipeBytes) return McpToolResult.Error("Recipe JSON is missing or exceeds the 1 MiB limit.");
                var validation = await RunParhelionBridgeAsync(["validate-json"], encoded, cancel).ConfigureAwait(false);
                if (validation.ExitCode != 0) return McpToolResult.Error(validation.Stderr);
                if (!await host.ApproveAsync("create_parhelion_recipe", $"Save validated Parhelion recipe to {relative}", cancel).ConfigureAwait(false)) return McpToolResult.Error("The user declined this in Solforge.");
                Directory.CreateDirectory(Path.GetDirectoryName(path!)!);
                await using (var file = new FileStream(path!, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
                await using (var writer = new StreamWriter(file, new UTF8Encoding(false)))
                    await writer.WriteAsync(validation.Stdout.AsMemory(), cancel).ConfigureAwait(false);
                return McpToolResult.Json(new JsonObject { ["saved"] = true, ["path"] = relative, ["validation"] = JsonNode.Parse(validation.Stdout) });
            });

        yield return new McpTool("build_parhelion_packages",
            "Compile one or more validated Parhelion recipes against a read-only source package directory and stage a verified package output under the active project. This never installs packages into a game.",
            McpJson.Schema(("package_directory", "string", "Absolute path to the source package directory required by Parhelion", true),
                ("recipe_paths", "string", "Newline-separated project-relative recipes/*.parhelion.json paths", true)), true,
            async (args, cancel) =>
            {
                if (string.IsNullOrWhiteSpace(host.ProjectRoot)) return McpToolResult.Error("Open a Solforge project first.");
                string? packageDirectory = McpJson.String(args, "package_directory");
                string? pathsText = McpJson.String(args, "recipe_paths");
                string[] recipes = (pathsText ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (string.IsNullOrWhiteSpace(packageDirectory) || !Directory.Exists(packageDirectory)) return McpToolResult.Error("The source package directory does not exist.");
                if (recipes.Length == 0 || recipes.Length > 100) return McpToolResult.Error("Provide between 1 and 100 recipe paths.");
                var resolved = new List<string>();
                foreach (string recipe in recipes)
                {
                    if (!recipe.EndsWith(".parhelion.json", StringComparison.OrdinalIgnoreCase)) return McpToolResult.Error($"Not a Parhelion recipe path: {recipe}");
                    string? full = ProjectFiles.Resolve(host.ProjectRoot, [], recipe);
                    if (full is null || !File.Exists(full) || new FileInfo(full).Length > MaximumRecipeBytes) return McpToolResult.Error($"Recipe is missing, outside the project, or over 1 MiB: {recipe}");
                    resolved.Add(full);
                }
                string outputRoot = Path.Combine(host.ProjectRoot, "build", "parhelion");
                string runOutput = Path.Combine(outputRoot, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
                string summary = $"Build {recipes.Length} Parhelion recipe(s) from {packageDirectory} and stage packages in {runOutput}";
                if (!await host.ApproveAsync("build_parhelion_packages", summary, cancel).ConfigureAwait(false)) return McpToolResult.Error("The user declined this in Solforge.");
                Directory.CreateDirectory(outputRoot);
                var result = await RunParhelionBridgeAsync(["build", packageDirectory, runOutput, .. resolved], null, cancel, TimeSpan.FromHours(1)).ConfigureAwait(false);
                return result.ExitCode == 0 ? McpToolResult.Text(result.Stdout) : McpToolResult.Error(result.Stderr);
            });

        yield return new McpTool("list_parhelion_weapon_recipes",
            "List project-local Parhelion recipe JSON documents from the top-level recipes folder. Does not read outside the active project.",
            McpJson.Schema(), false, (_, _) => Task.FromResult(ListRecipes(host.ProjectRoot)));

        yield return new McpTool("validate_parhelion_weapon_recipe",
            "Parse and run Parhelion-compatible schema and static validation on a project-local *.weapon.json recipe. This does not build or apply the recipe.",
            McpJson.Schema(("path", "string", "Project-relative recipe path, such as recipes/example.weapon.json", true)),
            false, (args, _) => Task.FromResult(ValidateRecipe(host.ProjectRoot, McpJson.String(args, "path"))));

        yield return new McpTool("start_parhelion_weapon_recipe",
            "Open Solforge's existing weapon-clone authoring workflow with a validated Parhelion recipe prefilled. The editor lists any recipe fields it cannot apply; the user reviews and presses Create to write project changes.",
            McpJson.Schema(("path", "string", "Project-relative *.weapon.json path", true)), true,
            async (args, cancel) =>
            {
                string? relative = McpJson.String(args, "path");
                if (!TryLoadRecipe(host.ProjectRoot, relative, out var recipe, out var path, out var bytes, out string? error))
                    return McpToolResult.Error(error ?? "Recipe is invalid.");
                var summary = $"Open Parhelion recipe '{recipe!.Name}' ({relative}) in the weapon clone editor";
                if (!await host.ApproveAsync("start_parhelion_weapon_recipe", summary, cancel).ConfigureAwait(false))
                    return McpToolResult.Error("The user declined this in Solforge.");
                return McpToolResult.Text(await host.RunActionAsync("start_parhelion_weapon_recipe", new JsonObject
                {
                    ["path"] = relative,
                    ["expected_sha256"] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes!)),
                }, cancel).ConfigureAwait(false));
            });
    }

    private static McpToolResult ListRecipes(string? projectRoot)
    {
        if (string.IsNullOrWhiteSpace(projectRoot)) return McpToolResult.Error("Open a project before listing recipes.");
        string directory = Path.Combine(projectRoot, "recipes");
        if (!Directory.Exists(directory)) return McpToolResult.Json(new JsonObject { ["recipes"] = new JsonArray() });
        var recipes = new JsonArray();
        foreach (string file in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
            .Where(file => file.EndsWith(".weapon.json", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".parhelion.json", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase))
        {
            var info = new FileInfo(file);
            recipes.Add(new JsonObject { ["path"] = "recipes/" + info.Name, ["bytes"] = info.Length });
        }
        return McpToolResult.Json(new JsonObject { ["recipes"] = recipes });
    }

    private static McpToolResult ValidateRecipe(string? projectRoot, string? relative)
    {
        if (!TryLoadRecipe(projectRoot, relative, out var recipe, out _, out _, out string? error))
            return McpToolResult.Json(new JsonObject { ["valid"] = false, ["error"] = error });
        return McpToolResult.Json(new JsonObject
        {
            ["valid"] = true,
            ["name"] = recipe!.Name,
            ["namespace"] = recipe.Namespace,
            ["donor_item_hash"] = recipe.Donor.ItemHash.Text,
            ["identity_item_hash"] = recipe.Identity.ItemHash.Text,
            ["socket_columns"] = recipe.Overrides.SocketColumns.Count,
            ["static_issues"] = new JsonArray(),
        });
    }

    private static bool TryLoadRecipe(string? projectRoot, string? relative, out WeaponRecipe? recipe,
        out string? path, out byte[]? bytes, out string? error)
    {
        recipe = null; path = null; bytes = null; error = null;
        if (string.IsNullOrWhiteSpace(projectRoot)) { error = "Open a project first."; return false; }
        if (string.IsNullOrWhiteSpace(relative) || !relative.EndsWith(WeaponRecipeJson.Extension, StringComparison.OrdinalIgnoreCase))
        { error = "Give a project-relative *.weapon.json recipe path."; return false; }
        path = ProjectFiles.Resolve(projectRoot, [], relative);
        if (path is null || !File.Exists(path)) { error = "Recipe path is missing or outside the active project."; return false; }
        var info = new FileInfo(path);
        if (info.Length > MaximumRecipeBytes) { error = "Recipe exceeds the 1 MiB limit."; return false; }
        try
        {
            bytes = File.ReadAllBytes(path);
            recipe = WeaponRecipeJson.Parse(new UTF8Encoding(false, true).GetString(bytes));
            return true;
        }
        catch (WeaponRecipeException exception) { error = exception.Message; }
        catch (DecoderFallbackException) { error = "Recipe is not valid UTF-8."; }
        catch (IOException exception) { error = $"Could not read recipe: {exception.Message}"; }
        catch (UnauthorizedAccessException exception) { error = $"Could not read recipe: {exception.Message}"; }
        return false;
    }

    private sealed record BridgeResult(int ExitCode, string Stdout, string Stderr);

    private static bool TryRecipePath(string? projectRoot, string? fileName, out string? fullPath, out string? relative, out string? error)
    {
        fullPath = null; relative = null; error = null;
        if (string.IsNullOrWhiteSpace(projectRoot)) { error = "Open a project first."; return false; }
        if (string.IsNullOrWhiteSpace(fileName) || Path.GetFileName(fileName) != fileName || !fileName.EndsWith(".parhelion.json", StringComparison.OrdinalIgnoreCase))
        { error = "Give a simple filename ending in .parhelion.json."; return false; }
        relative = Path.Combine("recipes", fileName);
        fullPath = ProjectFiles.Resolve(projectRoot, [], relative);
        if (fullPath is null) { error = "Recipe path is outside the active project."; return false; }
        if (File.Exists(fullPath)) { error = "That recipe file already exists; choose another filename."; return false; }
        return true;
    }

    private static async Task<BridgeResult> RunParhelionBridgeAsync(IReadOnlyList<string> arguments, string? stdin, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        string executable = Environment.GetEnvironmentVariable("SOLFORGE_PARHELION_BRIDGE") ?? Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "solforge-parhelion-bridge.exe" : "solforge-parhelion-bridge");
        if (!File.Exists(executable)) return new BridgeResult(127, "", $"Parhelion bridge not found at {executable}; rebuild Solforge with publish.ps1.");
        var start = new System.Diagnostics.ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardInput = stdin is not null, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = new System.Diagnostics.Process { StartInfo = start };
        try
        {
            process.Start();
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            if (stdin is not null) { await process.StandardInput.WriteAsync(stdin.AsMemory(), cancellationToken).ConfigureAwait(false); process.StandardInput.Close(); }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            return new BridgeResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(true); } catch { }
            throw;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        { return new BridgeResult(1, "", exception.Message); }
    }
}
