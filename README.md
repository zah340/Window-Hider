Window Hider is a small Windows WPF utility for privacy during screen sharing/recording: it helps reduce accidental exposure of sensitive windows while streaming.  
Under the hood, it relies on Windows’ display-affinity behavior (e.g., excluding a window from capture on supported systems).

## Features
- List currently open windows and select windows to protect from capture.  
- Toggle Window Hider’s own visibility in capture via a user-defined hotkey.  

## Requirements
- Windows 10 (version 2004 / build 19041) or Windows 11 recommended for best behavior.

## Usage
1. Start Window Hider (Admin rights may be required depending on what windows you’re trying to affect).
2. Select windows from the list and apply the “Hide Selected” action.
3. To restore, use the “Unhide all” action.
4. Set a self-hide hotkey inside the app; the keybind is stored via `Properties.Settings.Default`.

## Notes / Limitations
- Some windows may not support being excluded from capture due to Windows/app restrictions; behavior varies by OS version and capture method.
- If a taskbar icon is pinned, Windows may keep the pinned icon visible even when the corresponding window/taskbar entry is removed.  
- Antivirus/EDR products may flag privacy tools that interact with other processes/windows; use at your own risk.

## Disclaimer
This project is intended for privacy (e.g., preventing accidental leaks of personal information during streaming).  
Do not use it to mislead viewers, violate platform rules, or conceal prohibited activity.

## Credits
Made by @zahgerman.
