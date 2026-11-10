# AppLocker for Windows

AppLocker adds a password prompt to selected desktop apps. It's a small utility for keeping kids or guests from casually opening apps on a shared Windows PC.

**[Download AppLocker for Windows](https://github.com/g1mliii/AppLocker/releases/latest/download/AppLocker-Windows.zip)** · [Release notes](https://github.com/g1mliii/AppLocker/releases/latest)

## Get started

1. Extract the ZIP to a folder you can write to, such as `Documents\AppLocker`.
2. Double-click `AppLocker.exe` and set a master password.
3. Use **Browse .exe…** or **Pick Running…** to select the apps you want to lock, then click **Start AppLocker**.
4. Open a selected app. AppLocker pauses it and asks for the password. By default, the app locks again after it closes. You can also choose a timed unlock.

Use the padlock icon in the Windows system tray to change your settings, lock your apps again, or exit AppLocker. If the icon is hidden, click the arrow near the clock.

The download is portable and unsigned. It uses .NET Framework 4.x and was tested on 64-bit Windows 11. Windows SmartScreen may show a warning.

## What the lock covers

AppLocker checks running desktop apps by executable name and can pause processes it has permission to manage. It works while AppLocker is running. Someone who can stop it or edit its local configuration can bypass the lock.

Cancelling a password prompt or running out of attempts closes the selected app. Save your work before adding or relocking an app that's already open.

## Start at sign-in (optional)

Press **Win + R**, enter `shell:startup`, and put a shortcut to `AppLocker.exe` in the folder that opens. To remove AppLocker, exit it from the system tray, delete the startup shortcut if you added one, and delete the extracted folder.

## Build from source

Run `build.bat` on 64-bit Windows with .NET Framework 4.x to compile `AppLocker.exe`. Run `Run-Tests.bat` to compile and run the bundled test suite.

## License

[MIT](LICENSE)
