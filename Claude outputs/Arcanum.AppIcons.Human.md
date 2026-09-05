# Adding application icons — Arcanum, Compendium, The Forge

Step-by-step for JetBrains Rider on macOS. Target surfaces: **macOS Dock/Finder** and **Windows
Explorer/taskbar**. Linux is out of scope.

---

## 0. What "app icon" actually means here, per project

There is no single switch. Three different mechanisms, and which ones apply depends on the project:

| Surface | Mechanism | Arcanum (CLI) | Compendium | The Forge |
|---|---|---|---|---|
| Windows `.exe` in Explorer / pinned taskbar | `<ApplicationIcon>` → `.ico` embedded in the PE | ✅ | ✅ | ✅ |
| Windows titlebar + running taskbar button | Avalonia `Window.Icon` → `AvaloniaResource` | — (no window) | ✅ | ✅ |
| macOS Dock / Finder / ⌘-Tab | `.icns` in `Contents/Resources` + `CFBundleIconFile` | — (bare Mach-O, no bundle) | ✅ | ✅ |

Two consequences worth internalising before you start:

- **`Window.Icon` does nothing on macOS.** AppKit windows have no titlebar icon, and the Dock icon
  comes from the bundle, not the window. Setting it is still correct — it is what Windows uses at
  runtime — but do not expect to see it on your dev machine.
- **The macOS icon is a packaging change, not a project change.** It lands in
  `scripts/packaging/macos/`, not in the `.csproj`. Nothing you do in Rider's project settings will
  put an icon on the `.app`.

Arcanum gets exactly one row: the Windows `.exe` icon. On macOS it ships as a signed bare Mach-O
inside `arcanum-osx-arm64.zip` — there is no bundle to hang an icon on, so there is nothing to do.

---

## 1. Produce the icon files (once, in Terminal)

Rider has no icon converter. Do this first; everything after is just wiring.

Assume your three source PNGs are 1024×1024 with transparency. Put them somewhere scratch:

```bash
cd ~/Desktop/icons     # wherever your PNGs are
ls
# arcanum.png  compendium.png  the-forge.png
```

### 1a. `.icns` for macOS (Compendium, The Forge)

`sips` and `iconutil` ship with macOS — no install needed.

```bash
make_icns() {
  src="$1"; out="$2"
  set -e
  rm -rf "$out.iconset"; mkdir "$out.iconset"
  for pair in 16:16x16 32:16x16@2x 32:32x32 64:32x32@2x \
              128:128x128 256:128x128@2x 256:256x256 512:256x256@2x \
              512:512x512 1024:512x512@2x; do
    px="${pair%%:*}"; name="${pair##*:}"
    sips -z "$px" "$px" "$src" --out "$out.iconset/icon_$name.png" >/dev/null
  done
  iconutil -c icns "$out.iconset" -o "$out.icns"
  rm -rf "$out.iconset"
  echo "wrote $out.icns"
}

make_icns compendium.png Compendium
make_icns the-forge.png TheForge
```

`iconutil` rejects *unrecognised* filenames but will happily convert an *incomplete* iconset, so a
successful run is not proof all ten sizes made it. Check the output rather than trusting the exit
code — round-trip it and count:

```bash
iconutil -c iconset Compendium.icns -o /tmp/check.iconset && ls /tmp/check.iconset | wc -l   # want 10
```

> Do **not** just rename a `.png` to `.icns`. macOS will show a generic icon and give you no error.

### 1b. `.ico` for Windows (all three)

`sips` cannot write `.ico`. Use ImageMagick:

```bash
brew install imagemagick

for n in arcanum compendium the-forge; do
  magick "$n.png" -background none \
    -define icon:auto-resize=256,128,64,48,32,16 \
    "$n.ico"
done
```

The 16/32/48 sizes matter more than you'd think — Explorer's list view and the taskbar use them,
and a 1024px image downscaled on the fly looks muddy at 16px. If your artwork has fine detail,
consider hand-tuning the small sizes rather than letting `auto-resize` do it.

Verify each `.ico` is genuinely multi-size:

```bash
magick identify arcanum.ico
# should print 6 lines, 256x256 down to 16x16
```

---

## 2. Arcanum — Windows `.exe` icon

Project: `src/RetroDownfall.Arcanum.Cli/`

### 2a. Put the file in the project

1. In Rider's **Solution Explorer**, right-click **RetroDownfall.Arcanum.Cli** →
   **Add** → **New Folder**, name it `Assets`.
2. Copy `arcanum.ico` into `src/RetroDownfall.Arcanum.Cli/Assets/` in Finder.
   (SDK-style projects glob the directory, so it appears in Rider on its own. If you'd rather stay
   in the IDE: right-click the `Assets` folder → **Add** → **Existing Item…**)

### 2b. Point the csproj at it

Double-click the **RetroDownfall.Arcanum.Cli** node in Solution Explorer — Rider opens the
`.csproj` in the editor. Add `ApplicationIcon` to the **first** `<PropertyGroup>` (the one holding
`<OutputType>Exe</OutputType>`):

```xml
<PropertyGroup>

  <OutputType>Exe</OutputType>

  <ApplicationIcon>Assets/arcanum.ico</ApplicationIcon>

  <IsAotCompatible>true</IsAotCompatible>
  ...
```

**Use a forward slash.** `ApplicationIcon` is an MSBuild *property*, and property values are not
path-normalised the way item globs are — `Assets\arcanum.ico` risks failing on the macOS host that
runs your Linux/macOS builds. Forward slashes are correct on every platform.

### 2c. Why this survives Native AOT

Worth knowing, because it looks like it shouldn't work. The chain is:

1. Roslyn takes `ApplicationIcon` as `/win32icon:` and writes the icon into the **managed**
   assembly's Win32 resource directory.
2. On a Windows AOT publish, ILC is invoked with `--win32resourcemodule:<assembly>` (see
   `Microsoft.NETCore.Native.targets`), which copies that resource directory into the native image.

So the icon rides through the AOT link. The one thing that would break it is setting
`IlcGenerateWin32Resources=false` — don't.

### 2d. Verify

Windows builds run on `windows-latest` / `windows-11-arm` runners (`build-windows.yml` line 83), so
the icon is embedded by a native Windows toolchain and there's no cross-build question. Grab
`arcanum-win-x64.zip` from the workflow artifacts and look at `arcanum.exe` in Explorer.

Locally on your Mac you can at least prove the resource is in the managed assembly:

```bash
dotnet build src/RetroDownfall.Arcanum.Cli -c Release
# no error = the .ico was found, parsed, and embedded.
# A missing or malformed icon is a hard compile error (CS7064 / CS1566), not a warning —
# you cannot accidentally ship a blank one.
```

---

## 3. Compendium — window icon + Windows `.exe` icon

Project: `src/RetroDownfall.Compendium.Ux/`

### 3a. Add the assets

Create `src/RetroDownfall.Compendium.Ux/Assets/` and drop in `compendium.ico`.

### 3b. Register the Avalonia resource glob

This project has **no** `AvaloniaResource` item group today. It uses `Microsoft.NET.Sdk` with
Avalonia as a plain `PackageReference`, and nothing globs `Assets/` for you — the Avalonia template
supplies that line explicitly and this project was never templated.

Open `RetroDownfall.Compendium.Ux.csproj` and add a new `ItemGroup`:

```xml
<ItemGroup>

  <AvaloniaResource Include="Assets/**" />

</ItemGroup>
```

(Item `Include` globs *are* separator-normalised by MSBuild, so the `Assets\**` you'll see in
Avalonia's docs also works. Stay consistent with forward slashes anyway.)

> Rider alternative: select the file, **⌥↩ / right-click → Properties**, set **Build Action** to
> `AvaloniaResource`. That writes a per-file `<AvaloniaResource Include="Assets/compendium.ico" />`
> instead of a glob. The glob is better — the next asset you add just works.

### 3c. Set the window icon

Open `src/RetroDownfall.Compendium.Ux/Views/MainWindow.axaml` and add `Icon` to the root `<Window>`,
just above the existing `Title`:

```xml
<Window xmlns="https://github.com/avaloniaui"
        ...
        x:DataType="vm:ConfigurationViewModel"
        Icon="/Assets/compendium.ico"
        Title="Compendium"
        Width="1100"
        ...
```

The leading `/` roots the path at the assembly's resource tree. Avalonia will also accept
`avares://RetroDownfall.Compendium.Ux/Assets/compendium.ico`, which is only *required* when the
asset lives in a different assembly — not the case here.

### 3d. Set the exe icon

Same csproj, into the existing `<PropertyGroup>` beside `<ApplicationTitle>Compendium</ApplicationTitle>`:

```xml
<ApplicationIcon>Assets/compendium.ico</ApplicationIcon>
```

### 3e. macOS `.icns` — packaging, not project

Three edits, all outside the project.

**(i)** Copy `Compendium.icns` to `scripts/packaging/macos/Compendium.icns`. It belongs next to the
`Info.plist.*` templates — same reason they live there: it is an input to bundle assembly, not to
compilation.

**(ii)** Add the key to `scripts/packaging/macos/Info.plist.compendium`, next to `CFBundleName`:

```xml
  <key>CFBundleIconFile</key>
  <string>Compendium.icns</string>
```

**(iii)** Teach `scripts/packaging/macos/build-app-dmg.sh` to stage it. In the `case "$PRODUCT"`
block (line 128), add an `ICNS` variable to each arm:

```bash
  compendium)
    PROJECT="$REPO_ROOT/src/RetroDownfall.Compendium.Ux/RetroDownfall.Compendium.Ux.csproj"
    APP_NAME="Compendium"
    PLIST_TEMPLATE="$SCRIPT_DIR/Info.plist.compendium"
    ICNS="$SCRIPT_DIR/Compendium.icns"
    DMG_NAME="compendium-osx-arm64.dmg"
    EXECUTABLE_NAME="RetroDownfall.Compendium.Ux"
    ;;
```

Then, immediately after the `render_plist_template` call (line 194) and before the `cp -a` of the
publish tree:

```bash
render_plist_template "$PLIST_TEMPLATE" "$APP_PATH/Contents/Info.plist" "$MARKETING_VERSION" "$BUNDLE_VERSION"

if [[ ! -f "$ICNS" ]]; then
  echo "error: icon not found: $ICNS" >&2
  exit 1
fi
cp "$ICNS" "$APP_PATH/Contents/Resources/$(basename "$ICNS")"
```

Three things this placement gets right, and each is load-bearing:

- `Contents/Resources/` already exists — it's created on line 193.
- It is **before** `sign_app_bundle`, so the `.icns` is covered by the bundle seal. A resource added
  after signing invalidates the signature and Gatekeeper rejects the app.
- The hard failure matches the file's existing house style (see `Assert-StagedNatives` in the
  Windows packager): a release that silently ships without its icon is worse than a build that stops.

The `basename` must match the `CFBundleIconFile` string exactly, extension included.

---

## 4. The Forge — same shape

Project: `src/RetroDownfall.TheForge.Ux/`

Identical to §3 with these substitutions:

| §3 step | The Forge |
|---|---|
| 3a | `src/RetroDownfall.TheForge.Ux/Assets/the-forge.ico` |
| 3b | same `<AvaloniaResource Include="Assets/**" />` item group |
| 3c | `Views/MainWindow.axaml` → `Icon="/Assets/the-forge.ico"`, above `Title="The Forge — Inference IDE"` |
| 3d | `<ApplicationIcon>Assets/the-forge.ico</ApplicationIcon>` |
| 3e (i) | `scripts/packaging/macos/TheForge.icns` |
| 3e (ii) | `Info.plist.theforge` → `<string>TheForge.icns</string>` |
| 3e (iii) | `the-forge)` arm → `ICNS="$SCRIPT_DIR/TheForge.icns"` |

The shared `cp` block from 3e(iii) is written once and serves both products — that's why `ICNS` goes
in the `case` arms rather than being hard-coded at the copy site.

Note The Forge's csproj has no `<ApplicationTitle>`; put `<ApplicationIcon>` in the first
`<PropertyGroup>`, beside `<OutputType>WinExe</OutputType>`.

> **Do not do §3 without §4.** `tests/RetroDownfall.Arcanum.Tests/Packaging/MacOsBundleTemplateTests.cs`
> asserts the two `Info.plist` templates declare an *identical key set*. Adding `CFBundleIconFile` to
> Compendium alone turns that test red. Adding it to both keeps it green.

---

## 5. Verify

### Windows (both GUI apps + Arcanum)

Dispatch **Build Windows (Arcanum + Compendium + The Forge)** (it takes a `rid` input), download the
`windows-<rid>-<version>` artifact bundle, and check on a Windows box:

- `.exe` icon in Explorer → came from `<ApplicationIcon>`
- titlebar + taskbar while running → came from `Window.Icon`

If the first works and the second doesn't, your `AvaloniaResource` glob is missing or the XAML path
is wrong. If the second works and the first doesn't, it's `ApplicationIcon`. They fail
independently, which makes this a useful bisect.

### macOS

Fastest loop — no signing, no notarization:

```bash
./scripts/packaging/macos/build-app-dmg.sh \
  --product compendium \
  --version 0.1.0-beta.1 \
  --marketing-version 0.1.0 \
  --bundle-version 1 \
  --output-dir ./dist \
  --skip-sign
```

Mount the DMG and look at it in Finder. If it shows a generic icon:

```bash
# 1. Is the file in the bundle?
ls -l /Volumes/Compendium/Compendium.app/Contents/Resources/

# 2. Does the plist name it, spelled identically?
/usr/libexec/PlistBuddy -c 'Print :CFBundleIconFile' \
  /Volumes/Compendium/Compendium.app/Contents/Info.plist
```

**Finder icon caching will lie to you.** Having verified both of the above, a stale icon is almost
certainly the cache, not your change:

```bash
sudo rm -rf /Library/Caches/com.apple.iconservices.store
killall Dock Finder
```

Then re-open the DMG. Copying the `.app` to a fresh path also dodges the cache.

Once it looks right, re-run with `--local-sign` to confirm the bundle still seals with the new
resource in it.

---

## 6. Checklist

**Arcanum**
- [ ] `src/RetroDownfall.Arcanum.Cli/Assets/arcanum.ico`
- [ ] `<ApplicationIcon>Assets/arcanum.ico</ApplicationIcon>`

**Compendium**
- [ ] `src/RetroDownfall.Compendium.Ux/Assets/compendium.ico`
- [ ] `<AvaloniaResource Include="Assets/**" />`
- [ ] `MainWindow.axaml` → `Icon="/Assets/compendium.ico"`
- [ ] `<ApplicationIcon>Assets/compendium.ico</ApplicationIcon>`
- [ ] `scripts/packaging/macos/Compendium.icns`
- [ ] `Info.plist.compendium` → `CFBundleIconFile`

**The Forge**
- [ ] `src/RetroDownfall.TheForge.Ux/Assets/the-forge.ico`
- [ ] `<AvaloniaResource Include="Assets/**" />`
- [ ] `MainWindow.axaml` → `Icon="/Assets/the-forge.ico"`
- [ ] `<ApplicationIcon>Assets/the-forge.ico</ApplicationIcon>`
- [ ] `scripts/packaging/macos/TheForge.icns`
- [ ] `Info.plist.theforge` → `CFBundleIconFile`

**Shared**
- [ ] `build-app-dmg.sh` — `ICNS` in both `case` arms + the guarded `cp` after `render_plist_template`

Nothing to do for version control: `.gitattributes` already declares `*.ico binary` and
`*.icns binary` (lines 27–28), and no `.gitignore` rule catches an `Assets/` folder.

---

## Appendix: things that will waste your afternoon

**A `.icns` filename mismatch fails silently.** `CFBundleIconFile` is a plain string; macOS does not
validate it and does not log when the lookup misses. `Compendium.icns` in the plist against
`compendium.icns` on disk gives you a generic icon and no diagnostic anywhere. This is the single
likeliest cause of "I did everything and it's still blank."

**`Window.Icon` on macOS.** Covered in §0, repeated because it is the single most common wrong turn:
it is not broken, it is not applicable. Judge macOS from the packaged `.app`, never from `dotnet run`.

**`TreatWarningsAsErrors` is on repo-wide** (`Directory.Build.props`). A malformed `.ico` is a build
error, not a warning you can ignore — which is good, but it means a bad ImageMagick conversion stops
the build rather than shipping a blank icon.

**Rider's XAML preview** caches resources. After adding the `AvaloniaResource` glob, do a full
**Build → Rebuild Solution** before concluding the `Icon=` path is wrong.

**One `.icns` covers both Retina and non-Retina** — that's what the `@2x` entries are. You do not
need per-display variants, and `--marketing-version` etc. are unrelated to icons.

**The DMG volume icon is a separate thing** and is not covered here. `hdiutil create` produces a
default-looking disk image; changing that needs a `.VolumeIcon.icns` plus `SetFile -a C`, and is
cosmetic to the install experience only.
