# Detroit: Become Human Audio Extractor
[WIP] Extract playable audio files from Detroit: Become Human!

## Requirements
1. Own Detroit: Become Human (Steam/Epic Games, or anywhere else)
2. Around 20 gigabytes of free space (for audio storage)
3. Internet connection (On first run to download dependencies)
4. An hour or so of your time to have the program run in the background.

## Features
1. Extract BNK
2. Extract WEM files from banks into audio type folders (`SFX`, `MUS`, `VO`, `UNKNOWN`)
3. Extract OGG
4. Convert OGG to listenable OGG format.
5. Detect bank audio types from Wwise object metadata.
6. Extract dialogue (experimental)
7. Extract MIDI files

## Command Line Arguments
If the tool is started with no arguments, it will show a startup menu so you can choose common options interactively before extraction continues.

`--logfile`
Creates a detailed log output in "/logging/" for dialogue and banks giving info on where they were found in the files.

`--onlyextract "file1,file2,file3"`
Extracts only the given files when inputting a directory. Specify multiple files separated by commas.

`--delete-errors`
Automatically deletes faulty or corrupt .OGG files during conversion.

`--{LANGUAGE}`
Only exports the given dialogue languages, can be stacked. **If no languages are passed, dialogue will be skipped.**
List of languages:

```
--english : English
--mexican : Mexican Spanish
--brazilian : Brazilian Portuguese
--french : French
--arabic : Arabic
--russian : Russian
--polish : Polish
--portuguese : Portuguese
--italian : Italian
--german : German
--spanish : Spanish (Spain)
--japanese : Japanese
--all_lang : Extracts ALL dialogue files for every language (equivalent to specifying all individual language arguments)
```

`--meltingpot`
Instead of creating subfolders for dialogue, put them all into one folder. Might be chaotic.

## Notes
- Some audio may fail, these are usually false-positives being detected by the extractor and don't have any real data to them.
- Dialogue may have long periods of silence in them, I assume this is due to the game playing audio for all characters at the start of a dialogue event, so empty space is used to pad out the time. (?)

## To-Do
- Get file names for non-dialogue audio.
- Improve dialogue extractor
- Improve extraction speed