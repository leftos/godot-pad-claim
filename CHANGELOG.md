# Changelog

All notable changes to this project are documented in this file. The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

- Repository skeleton: an engine-free core project, its tests and the build, test and format scripts.
- Device identity from Godot's joypad info, per-device verdicts and blocked buttons, and the JSON format the host saves them in.
- A two-button claim gesture: a pad claims the seat by releasing one button, then pressing a different one within 3 s and releasing it.
