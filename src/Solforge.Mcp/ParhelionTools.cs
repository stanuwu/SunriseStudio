using System.Text.Json.Nodes;
using System.Text;
using Solforge.Weapons;

namespace Solforge.Mcp;

/** MCP access to project-local Parhelion-compatible weapon recipes. */
public static partial class SolforgeTools
{
    private const long MaximumRecipeBytes = 1 << 20;

    private static IEnumerable<McpTool> ParhelionTools(ISolforgeHost host)
    {
        yield return new McpTool("get_parhelion_context",
            "Project-specific Parhelion guidance: where recipes live, which Solforge authoring systems own the resulting changes, and which project skill to read before weapon work.",
            McpJson.Schema(), false, (_, _) => Task.FromResult(McpToolResult.Text(
                "Solforge's Parhelion-compatible weapon recipe domain is Solforge.Weapons. Keep recipes in the active project under recipes/*.weapon.json. " +
                "Read the project's solforge-new-weapon skill before authoring. Recipes use a stock donor, a distinct namespace-derived identity, text/locales, stats, sockets, tuning, and optional collection placement. " +
                "Use validate_parhelion_weapon_recipe before starting one, then start_parhelion_weapon_recipe opens Solforge's existing clone authoring dialog. Solforge applies item name, flavor, type line, donor rarity/bucket, matching donor stats, and one installed initial plug per authored socket. Read the dialog's Limits before Create: unsupported fields are reported there.")));

        yield return new McpTool("get_parhelion_subclass_context",
            "Explain Parhelion's subclass authoring support and route subclass work to the matching Solforge MCP system.",
            McpJson.Schema(), false, (_, _) => Task.FromResult(McpToolResult.Text(
                "Sundial's Parhelion desktop workbench supports custom subclasses, including ability and attunement choices, node-by-node paths, custom perks, gameplay values, visuals, class/equipping, and artwork. " +
                "The current Solforge Parhelion recipe MCP integration handles weapon recipes only; it does not expose Parhelion's desktop workbench or import subclass recipes. " +
                "Read skills/solforge-new-subclass/SKILL.md. For Solforge server subclass content, use get_subclass_layout and save_subclass_definition for explicit MCP controls; update_content can then patch it, validate_content checks it, check_subclass_build checks a selected build, and export_content exports server data. " +
                "That server content model is separate from a Parhelion subclass package recipe and cannot be installed by Parhelion.")));

        yield return new McpTool("list_parhelion_weapon_recipes",
            "List project-local Solforge weapon recipes (*.weapon.json) from the top-level recipes folder. Does not read outside the active project.",
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
        foreach (string file in Directory.EnumerateFiles(directory, "*.weapon.json", SearchOption.TopDirectoryOnly).Order(StringComparer.OrdinalIgnoreCase))
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
}
