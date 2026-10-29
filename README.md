# BilliumMoto's OM to SM Converter (LilSanik Offset Fix)

A modern, enhanced fork of [BilliumMoto/OMiSM](https://github.com/BilliumMoto/OMiSM) (osu!mania to StepMania converter) featuring automatic audio offset detection, `.osz` support, intelligent multi-difficulty packaging, and smart background extraction.

[![Latest Release](https://img.shields.io/github/v/release/LilSanik/OMiSM?include_prereleases&label=Latest%20Release)](https://github.com/LilSanik/OMiSM/releases/latest)
[![Platform](https://img.shields.io/badge/platform-Windows%20x64-blue.svg)](https://github.com/LilSanik/OMiSM/releases)
[![License](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)

---

## Download

Get the single, portable standalone executable from the Releases page:

**[Download Latest Release (OMtoSMConverter.exe)](https://github.com/LilSanik/OMiSM/releases/latest)**

*No installation required. Zero external dependencies (BASS is embedded directly into the executable).*

---

## Key Features & Fixes in this Fork

### 1. Dynamic Audio Offset Correction
In the original converter, a hardcoded fudge factor was used, which caused charts converted to StepMania / Arrow Vortex to have a perceptible desync (~20 ms to ~76 ms). This fork introduces native, per-file audio lag detection:
* **MP3 (with Xing/LAME frame)**:
  * At 48,000 Hz: compensates for **+76 ms** (encoder padding + osu! engine latency).
  * At 44,100 Hz: compensates for **+69 ms** (e.g. *Easy Breezy*, *Witchcraft*).
* **MP3 (without Xing frame / direct DAW exports)**: compensates for **+19 ms** (e.g. *Kisaragi*).
* **OGG / Vorbis, WAV & FLAC**: compensates for osu!'s BASS engine baseline playback buffer latency (**+19 ms**), perfectly aligning notes with the audio transients (e.g. *Money Rain*, *courage*).

### 2. Compressed `.osz` Package Support
* Directly drag and drop `.osz` beatmap files into the window. The application automatically unpacks and processes them.

### 3. Multi-Difficulty & Song Pack Handling
* **Shared Audio**: When a beatmap set contains multiple `.osu` files that share the same song audio, they are merged into a single StepMania chart (`.sm`) under distinct difficulty slots (`Beginner`, `Easy`, `Medium`, `Hard`, `Challenge`), preserving their original difficulty names in the `#NOTES` description.
* **Song Packs (Different Audio per Difficulty)**: When a mapset contains multiple `.osu` files with different audio tracks (typical of community song packs), the converter outputs separate `.sm` files for each song:
  * `#TITLE`: set to the specific difficulty/song name.
  * `#SUBTITLE`: set to the overarching pack title.

### 4. Smart Background Selection
* When difficulties within a mapset use different background images, the converter selects the background from the difficulty with the highest note count.

### 5. 100% Self-Contained Portable Executable
* `bass.dll` is embedded as an embedded resource and extracted dynamically at runtime to temporary storage, allowing the executable to run standalone anywhere without loose DLLs.

---

## Usage

1. Open `OMtoSMConverter.exe`.
2. Drag and drop any `.osu` files, folders, or `.osz` files directly into the converter window.
3. Converted StepMania files (`.sm`, audio, and background) will be generated in subfolders next to `OMtoSMConverter.exe`.
4. Press `e` in the window to clear the status log at any time.

You can also run it via command line:
```bat
OMtoSMConverter.exe "C:\Path\To\Osu\Songs\MapsetFolder"
OMtoSMConverter.exe "C:\Path\To\Song.osz"
```

---

## Building from Source

Requirements:
* Windows 7 SP1 or newer (x64)
* Visual Studio 2015+ or MSBuild (`.NET Framework 4.5.2`)

To build with MSBuild:
```bat
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe OMtoSMConverter.sln /p:Configuration=Release
```

The compiled standalone binary will be located in `OMtoSMConverter\bin\Release\OMtoSMConverter.exe`.

---

## Credits

* Original OMtoSMConverter by [BilliumMoto](https://github.com/BilliumMoto/OMiSM).
* Offset detection, `.osz` support, and multi-difficulty packaging by [LilSanik](https://github.com/LilSanik).
