@echo off
rem Sends Shift+Win+D twice (3 s apart) so you can check that Virtual Desktop reacts.
start "" "%~dp0VR Session Recorder.exe" --test-toggle
