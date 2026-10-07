# Detroit: Become Human Audio Extractor

Extract playable audio files from Detroit: Become Human!

A C# desktop app for browsing audio, listening to it and choosing what to export.

## Requirements

1. Own Detroit: Become Human (Steam/Epic Games, or anywhere else)
2. Windows (64-bit)
3. Free space for your exports. A full export can get large, especially WAV.
4. Internet connection if dependencies are missing. The portable build includes the tools for offline use.

## Features

1. Browse archives, banks, events and individual audio entries
2. Preview audio with pause, seeking and volume controls
3. Extract complete BNK banks and original WEM files
4. Convert audio to playable OGG and WAV, individually or in batches
5. Extract dialogue by language
6. Search by names, IDs, bank names and dialogue text
7. Read names from game files automatically
8. Export the whole library, track progress, cancel jobs and retry failed files
9. Group related audio and select several clips, banks or events
10. Browse MIDI sequences and export original `.mid` files, individually or in batches

## How to use

1. Keep `Detroit Audio Extractor.exe` and the `tools` folder together, then run the EXE.
2. The startup menu checks your dependencies and downloads missing tools. Choose your game folder and click **Open game**.
3. Open **All audio**, or expand **Banks** and **Archives** to browse their events. Select a **Type** to narrow the current list.
4. Search for what you want. Search covers the whole library by default; choose **Current view** to search the selected bank or event. **Clear search** clears the text, and **Reset filters** resets language, type and codec.
5. Select an entry and click **Play**, or double-click it. Use **Stop** and the seek bar to control playback.
6. Use **Ctrl** and **Shift** to select several clips, banks or events, then use **Export** or **Convert**. Choose your format and output folder, check the file list, then start the export.

**Group similar audio** puts clips with related event/state names into expandable groups. Selecting a group exports its matching members. **Bank** exports BNK files; **Event WAV/OGG** renders each selected event separately.

Choose **MIDI** in the Type filter to browse sequences. **Selected original**, **Filtered original**, and **Export All → Original** preserve them as `.mid` files. MIDI entries show their stored track names and format details. MIDI playback and OGG/WAV rendering are unavailable.

**Export All** exports from the whole library, regardless of the current view or filters, and can take a long time. You can check the counts before starting. Existing files are skipped unless **Replace existing files** is selected.

**Open file** accepts `.idx`, `.bnk`, `.wem`, `.dat` and `.dNN` files. You can also drag files onto the window. Open the `.idx` to browse its archive set.

Keyboard shortcuts: **Ctrl+O** opens a file, **Space** controls playback, **Escape** stops playback, and **Ctrl+A** in the audio list selects all matching results.

Choose **System**, **Light** or **Dark** in **Tools**. System is the default and follows the Windows theme.

## Notes

- Names are read automatically. Some music and sound effects still use IDs or labels from linked events. **Details** shows IDs and name sources.
- Some banks contain just the start of a streaming clip. **Partial preview** plays that part; OGG and WAV export require the full stream. **Export fragment bytes** saves the original fragment.
- Dialogue may contain long periods of silence.
- Some events depend on game logic or effects that the app cannot reproduce yet. Individual audio entries can still be previewed and exported where supported. **Details** and **Jobs** show errors.
- WAV defaults to PCM16. PCM24 and float32 are available in **Tools**. Each export includes a JSON report with filenames, results and audio metadata.

## Build

Requires the .NET 10 SDK on Windows. Preparing the decoder from source also requires Visual Studio C++ build tools and CMake.

```powershell
./scripts/Prepare-Tools.ps1
dotnet build DetroitAudio.slnx -c Release
dotnet test DetroitAudio.slnx -c Release --no-build -p:AutoPublish=false
./scripts/Package.ps1
```

The portable build goes in `artifacts/DetroitAudio`: one application EXE plus the offline `tools` folder. Source and Windows build ZIPs go in `artifacts/packages`.

The original console extractor is still included in `Audio.sln`. Its command line options include `--logfile`, `--onlyextract`, `--delete-errors`, `--meltingpot` and the dialogue language flags.


## License

The C# app is MIT licensed. Legacy code and bundled tools keep their own licenses. See [LICENSE](LICENSE) and [third-party notices](THIRD-PARTY-NOTICES.md).

## AI Disclaimer

This application was developed with assistance from AI tools.

## Important note

This tool is not affiliated with Quantic Dream. We don't provide any of *Detroit: Become Human*'s assets, you must own a purchased copy of the game to use this tool properly.