# Detroit: Become Human Audio Extractor
[WIP] Extract playable audio files from Detroit: Become Human!

## Features
1. Extract BNK
2. Extract WEM
3. Extract OGG
4. Convert OGG to listenable OGG format.
5. Mark possible music with a command line argument.
6. Extract dialogue (experimental)

## Command Line Arguments
`--delete-errors`
Automatically deletes faulty or corrupt .OGG files during conversion.

`--mark-music`
Automatically add a comment tag to any audio with stereo or quad channels and a sample rate of 48000 Hz. Additionally, tags files located in directories containing "music" in their path.

`--{LANGUAGE}`
Only exports the given dialogue languages, can be stacked. 
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
```
## To-Do
- Get file names for non-dialogue audio.
- Improve dialogue extractor
- Improve extraction speed
  
## READ
This tool is WIP! It doesn't work yet fully, any contribution is appreciated!!!
