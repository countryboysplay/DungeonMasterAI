# Dungeon Master AI - Windows Test Checklist

This checkpoint is intended to answer the question the Linux build container cannot answer: does the current native .NET/WPF source really compile, run, and pass its smoke tests on Windows?

## 1. Install the .NET 10 SDK

Open **PowerShell as Administrator** and run:

```powershell
winget install Microsoft.DotNet.SDK.10 --source winget
```

Close PowerShell after installation, open a new PowerShell window, and verify:

```powershell
dotnet --version
```

The first number should be `10`.

You do not need Visual Studio for this test. The .NET SDK is sufficient.

## 2. Run the automated test

Extract the test-source ZIP to a normal local folder, for example:

```text
C:\DungeonMasterAI-Test
```

Open PowerShell in the extracted folder and run:

```powershell
powershell -ExecutionPolicy Bypass -File .\windows\WINDOWS_TEST.ps1
```

The first restore requires internet access because the project restores its NuGet dependency.

The script will:

1. Record basic Windows, CPU, GPU, and .NET information.
2. Restore the native projects.
3. Compile the WPF application in Release mode.
4. Compile and run the native smoke-test executable.
5. Publish a self-contained Windows x64 build.
6. Create `DungeonMasterAI-TestResults-<timestamp>.zip` inside the `windows` folder.

## 3. Send the result back to ChatGPT

Upload the generated `DungeonMasterAI-TestResults-<timestamp>.zip` to the project chat whether the test passes or fails. The failure logs are more useful than screenshots of compiler messages.

## 4. Optional first GUI launch

If the automated test passes, run it again with:

```powershell
powershell -ExecutionPolicy Bypass -File .\windows\WINDOWS_TEST.ps1 -LaunchApp
```

For this checkpoint, only verify that:

- The main window opens without an immediate crash.
- Navigation between the major screens works.
- The included sample campaign can be opened/loaded if presented by the UI.
- The app can be closed and launched again.

Do not worry yet if local-AI narration is unavailable. The bundled llama.cpp runtime and final model package are not part of this source checkpoint yet.

## If Windows blocks the script

Use the exact `-ExecutionPolicy Bypass` command above. It changes policy only for that PowerShell process; it does not require changing the machine-wide execution policy.
