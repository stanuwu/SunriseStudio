<p align="center"><img src="src/SunriseStudio.Editor/Assets/Sunrise.source.png" alt="Sunrise" width="160"></p>

# Sunrise Studio

Mod Editor for Sunrise

- [Sunrise](https://github.com/stanuwu/Sunrise)
- [Blender Add-on](https://github.com/stanuwu/SunriseBlenderPlugin)
- [Documentation](https://projectsunrise.dev/docs/sunrise-studio/)
- [Discord](https://discord.gg/22JS6et5k9)

## Features
- View assets, maps, missions and investment data
- Move objects, edit properties and collision
- create weapons, items, vendors, static props, textures, materials,
  perk behaviors and more
- Edit mission scripts as graphs
- Export to Blender, Import from Blender, Rebuild from Blender edits
- Attach to Sunrise with live cui or inworld view
- Build and install projects as Sunrise mods

## Alpha

Sunrise Studio is an alpha. Things will be broken, and some will work in unexpected ways. Keep backups
of your projects. Features known to be broken or only partly working carry a **WIP** tag in the
editor. Hover it to see what is wrong.

## Support Me

Leave a star on this repo.

If you want to support my open source work you can find the means on my
[profile](https://github.com/stanuwu). Also consider donating to charity instead.

All content released under this project is free and open source. If someone is trying to sell you
something you are getting scammed.

## Rules
Issues are for bug reports only.

PRs are for pull requests only.

Do not go and argue/chat there, you can do that on the discord.

## Building

### Requirements

- Windows 10 or 11 with a Direct3D 11 GPU.
- The .NET 9 SDK, to build.
- A Sunrise installation of the game. Studio reads its packages and keys from there.

No game packages, package keys or decoder DLLs are part of this repository.

### Build and run

1. Clone the repository
```powershell
git clone https://github.com/stanuwu/SunriseStudio
cd SunriseStudio
```

2. Build Studio
```powershell
.\publish.ps1
```

On first start, choose your Sunrise installation; Studio remembers it. `Sunrise Studio.exe --help`
lists the command-line options.

### Blender add-on

Opening Studio's exports in Blender, and making files Studio imports from Blender, needs the
[Sunrise Blender add-on](https://github.com/stanuwu/SunriseBlenderPlugin). This is not included!

### Layout

| Path | Holds |
|---|---|
| `src/SunriseStudio.App` | The executable and its command line |
| `src/SunriseStudio.Editor` | The editor window, panels, pages and workflows |
| `src/SunriseStudio.Viewport` | The 3D and 2D views, cameras, selection and shortcuts |
| `src/SunriseStudio.Rendering` | Direct3D 11 renderer, game shaders and ImGui backend |
| `src/SunriseStudio.Scene` | Map, mission and asset scene decoding |
| `src/SunriseStudio.Packages` | Package reading and writing, names and caches |
| `src/SunriseStudio.Projects` | Projects, deltas, object edits, builds and mod installs |
| `src/SunriseStudio.Authoring` | Importers and object edit rules |
| `src/SunriseStudio.Formats` | Typed readers and editors for game formats; glTF, OBJ, DDS and PNG conversion |
| `src/SunriseStudio.Interchange` | Blender bundles and world scene export |
| `src/SunriseStudio.Animation` | Skeletons, clips and skinning |
| `src/SunriseStudio.Audio` | Wwise reading, playback and export |
| `src/SunriseStudio.Missions` | Mission script graphs |
| `src/SunriseStudio.Live` | The link to a running game |
| `tests/` | Check programs; `verify.ps1` runs all but RootManifest and Sandbox |
| `licenses/` | Notice files of the .NET runtime and the Community Toolkit, copied unchanged |

## Contributing

Pull Requests are welcome. Please follow these rules:

- **No Copyrighted Data** - All game data is read from the user's installation at runtime.
- **Clean Code** - Try to post readable high-quality code, follow the project's existing style of
  comment and add docs.
- **Provide Documentation** - Please explain what you changed, why you changed it and the effects it
  has in detail, it saves me a lot of work.
- **Run the Checks** - Run `verify.ps1`, with `-Game` if your change reads game data.
- **Follow Up** - If something with the PR is not right, I will reply and ask you to fix it.
- **One Feature** - Do not put multiple features into one PR.
- **Honest WIP** - Mark anything known to be broken or only partly working as WIP. For anything
  unchecked, say "May not work in game." or "May be inaccurate." in the tooltip or docs, without a
  tag.

## Team

- [stan](https://github.com/stanuwu) - Creator and lead developer.
- All Sunrise Studio open source contributors.

## Credits

### All Contributors

### Dependencies:

- [ImGui.NET](https://github.com/ImGuiNET/ImGui.NET)
- [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows) (Direct3D 11, DXGI, D3DCompiler)
- [BCnEncoder.NET](https://github.com/Nominom/BCnEncoder.NET)
- [Loretta](https://github.com/LorettaDevs/Loretta)

### Artwork:

- [Solus](https://www.youtube.com/@Solus-yt) - Sunrise Studio Logo

### Ported code and data

Studio's audio decoder is ported from these projects. [THIRD_PARTY.md](THIRD_PARTY.md) has their
licenses, the Studio files that use them and the license of every library inside the exe.

- [libvorbis](https://xiph.org/vorbis/) - Vorbis decoder and the codebooks
- [aoTuV](https://github.com/AO-Yumi/vorbis_aotuv) - same codebooks, a second source
- [ww2ogg](https://github.com/hcs64/ww2ogg) - Wwise codebook layout and setup rebuild
- [vgmstream](https://github.com/vgmstream/vgmstream) - Wwise Vorbis setup rebuild, the stereo
  downmix and the float to PCM16 conversion

[wwiser](https://github.com/bnnm/wwiser) was used to check Studio's composition results. No wwiser
code is in Studio.

### Inspiration/Helpful Repos

- https://github.com/zeex64/repkg
- https://github.com/KyleThmpsn/sundial
- https://github.com/OmikronApex/Tenebrae

These projects were consulted as references. No source was copied from any of them. See the
[Sunrise credits](https://github.com/stanuwu/Sunrise#credits) for the research Studio builds on.

> Want to be added to or removed from the credits? Let me know.

## License

Sunrise Studio is licensed under GPL-3.0-or-later. The full text is in [LICENSE](LICENSE), and
Studio shows it under **Help > Licenses**. Third-party code and libraries keep their own licenses;
[THIRD_PARTY.md](THIRD_PARTY.md) lists them. The Sunrise logo artwork is by Solus and is not covered
by the GPL.

## AI Disclaimer

AI was used in the creation of this project. If you are not comfortable with the use of AI in
programming projects beware.

AI was NOT used to create any art or creative writing. Only for RE, development and documentation
purposes. All AI work that is publicly released is reviewed by a human. AI is a tool and the user is
responsible for the results it produces.

## Affiliation Disclaimer

This project is not affiliated with Bungie or Sony in any way.
