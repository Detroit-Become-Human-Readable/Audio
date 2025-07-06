# Detroit: Become Human Audio Extractor
[WIP] Extract playable audio files from Detroit: Become Human!

## Features
1. Extract BNK
2. Extract WEM
3. Extract OGG
4. Convert OGG to listenable OGG format.
5. Mark possible music with a command line argument.

## Command Line Arguments
`--logfile`
Creates a detailed log output in "/logging/" for dialogue and banks giving info on where they were found in the files.

`--onlyextract "file1,file2,file3"`
Extracts only the given files when inputting a directory. Specify multiple files separated by commas.

`--delete-errors`
Automatically deletes faulty or corrupt .OGG files during conversion.

`--mark-music`
Automatically add a comment tag to any audio with stereo channels, most music in the game uses this.

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

## To-Do
- Get file names for non-dialogue audio.
- Improve dialogue extractor
- Improve extraction speed
  
## READ
This tool is WIP! It doesn't work yet fully, any contribution is appreciated!!!
