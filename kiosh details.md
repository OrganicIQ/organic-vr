Organic IQ VR Kiosk - Setup \& Architecture Guide

Overview

We have built a fully autonomous, controller-free, continuous-looping VR Kiosk application using Unity and Meta Quest. The app is designed to run completely unattended at exhibitions, requiring absolutely zero input from the user wearing the headset.



🚀 Features Implemented

Infinite Looping Playlist Automatically loads all .mp4, .mov, and .mkv videos from the headset's internal Kiosk folder, sorts them alphabetically, and plays them seamlessly one after another in a continuous infinite loop.



Dynamic Format Switcher Automatically reads the filename and builds the correct 360-degree projection screen in real-time, completely eliminating "squeezed" or distorted videos.



\_3dv.mp4 / \_OU.mp4 -> Plays in Over/Under Stereoscopic 3D 360 mode.

\_mono360.mp4 (or no tag) -> Plays in standard Monoscopic 2D 360 mode.

Auto-Recentering Automatically calculates the physical direction the user is facing and realigns the 360 video to perfectly match their forward gaze every time a new video starts.



Animated Intro Sequence A floating, glowing "Organic IQ" text animation that greets the first user with a subtle breathing effect for 5 seconds when the app is initially booted.



Hidden Operator Controls Operators keeping a Quest controller in their pocket can secretly press the Trigger or A/X button to instantly restart the sequence for the next person, without ever breaking user immersion.



⚙️ Headset OS Modifications (ADB Commands)

To ensure the app never pauses, goes to sleep, or gets interrupted by system menus, we bypassed several Meta Quest OS features using ADB (Android Debug Bridge).



IMPORTANT



How to Replicate on a New Headset: Plug the new headset into a PC via USB, ensure Developer Mode is enabled, and run the following commands in your computer's terminal.



1\. Disable the Proximity Sensor

By default, the Quest goes to sleep and pauses all apps the moment you take it off your head. We disabled the physical forehead sensor so the headset thinks it is always being worn. This allows the video to loop endlessly on a table.



bash



adb shell settings put system prox\_enabled 0

(To re-enable it in the future, run the same command but change 0 to 1)



2\. Disable the Guardian Boundary System

By default, moving too far triggers a system-level "Boundary Not Found" popup that freezes the app and requires a controller to dismiss. We paused the Guardian system entirely.



bash



adb shell setprop debug.oculus.guardian\_pause 1

WARNING



The guardian\_pause ADB command sometimes resets when the headset is rebooted. For a 100% permanent fix that survives reboots, you must put on the headset and manually toggle it off in the UI: Settings > System > Developer > Guardian > Off



🛠️ Unity Level Safeguards

In addition to the ADB commands, the following safeguards are hard-coded into the Unity KioskVideoPlayer.cs script to prevent the OS from interrupting the kiosk:



NeverSleep: Screen.sleepTimeout = SleepTimeout.NeverSleep prevents the screen from turning off while the app is running.

Stationary Tracking Override: The script forcefully requests TrackingOriginModeFlags.Device from the OpenXR subsystem. This tells the Meta OS to treat the app as a seated, 3DOF experience, heavily reducing the chance of the OS asking for a RoomScale boundary.

Inverted UV Mirror Correction: Custom mathematical UV inversion (1f - x) guarantees that text inside 360 videos is never mirrored backwards, rega

