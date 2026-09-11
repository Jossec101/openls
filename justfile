# openls task runner. Install just: https://github.com/casey/just  (winget install Casey.Just)

set windows-shell := ["powershell.exe", "-NoLogo", "-NoProfile", "-Command"]

project := "openls"
config  := "Release"
out     := "publish"
exe     := project + "/bin/" + config + "/net10.0-windows/" + project + ".exe"

# list recipes
default:
    @just --list

# build (offline, no packages)
build:
    dotnet build {{project}} -c {{config}} --nologo

# build the RegisterClassExW probe
build-probe:
    dotnet build probe -c {{config}} --nologo

# build everything
all: build build-probe

# publish a runnable folder into ./publish
publish:
    dotnet publish {{project}} -c {{config}} -o {{out}} --nologo

# run with the hotkey engine (extra args are passed through, e.g. `just run --hotkey F8`)
run *args: build
    & "{{exe}}" {{args}}

# init the core, print adapters/displays/refresh, exit
probe: build
    & "{{exe}}" --probe

# full triage when the core refuses to start
diagnose: build
    & "{{exe}}" --diagnose

# end-to-end test: inject the hotkey after 1.5 s, then exit
self-test *args: build
    & "{{exe}}" --self-test 1500 {{args}}

# engage once with a given capture api (wgc|dxgi|gdi)
toggle-once api="wgc": build
    & "{{exe}}" --toggle-once --capture {{api}} --no-auto-fg

# show which ETW providers/events arrive and per-process present rates
fps-dump seconds="10": build
    & "{{exe}}" --fps-dump {{seconds}}

# publish, then register the published exe to run at sign-in (extra flags are kept)
install-startup *args: publish
    & "{{out}}/{{project}}.exe" --install-startup {{args}}

# remove the run-at-sign-in entry
uninstall-startup: publish
    & "{{out}}/{{project}}.exe" --uninstall-startup

# show the current run-at-sign-in entry, if any
startup-status:
    $v = (Get-ItemProperty "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -ErrorAction SilentlyContinue).{{project}}; if ($v) { "registered: $v" } else { "not registered" }

# delete build output
clean:
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue {{project}}/bin, {{project}}/obj, probe/bin, probe/obj, {{out}}
