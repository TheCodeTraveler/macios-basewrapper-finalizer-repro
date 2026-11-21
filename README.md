# macios BaseWrapper finalizer-thread repro

Protocol wrappers (`ObjCRuntime.BaseWrapper`, such as `UIKit.UIInteractionWrapper`) release their native object on the finalizer thread. `NSObject` peers release on the main thread. When the release is the last one, the native object deallocates off the main thread, which can crash UIKit.

The app registers an Objective-C class that adopts `UIInteraction` and has no managed type, so .NET can only expose it through a protocol wrapper. Each case drops all native references except the managed one, forces a GC and turns the main run loop:

- A: read through `UIView.Interactions` (wrapper)
- B: `Runtime.GetINativeObject<IUIInteraction>` (wrapper)
- C: `Runtime.GetNSObject` (`NSObject` peer, the control)

## Run it locally

Needs macOS with Xcode 27.2 and .NET SDK 11.0.100 with the iOS workload.

```sh
dotnet build -c Release
UDID=$(xcrun simctl create BaseWrapperRepro "iPhone 16 Pro" com.apple.CoreSimulator.SimRuntime.iOS-18-6)
xcrun simctl boot $UDID
xcrun simctl install $UDID bin/Release/net11.0-ios/iossimulator-arm64/BaseWrapperFinalizerRepro.app
xcrun simctl launch --console-pty --terminate-running-process $UDID com.example.basewrapperrepro
```

Any iOS simulator runtime works. Delete the simulator afterwards with `xcrun simctl delete $UDID`.

## Run it in CI

[`.github/workflows/repro.yml`](.github/workflows/repro.yml) runs on every push to `main` and on manual dispatch, on the GitHub-hosted `xcode-27` image. It builds with `-bl:msbuild.binlog`, runs the app on the newest iOS simulator runtime and uploads `msbuild.binlog` and `console.txt` as the `repro-logs` artifact. The job passes when all three cases ran, whatever their results.

## Expected and actual

Expected: the native object deallocates on the main thread in all three cases.

Actual: only the `NSObject` peer does. Output of a real run (iOS 18.6 simulator, Release):

```
CASE A UIView.Interactions (protocol wrapper): managed type = UIKit.UIInteractionWrapper; dealloc ran = True; dealloc on main thread = False; managed thread id = 2
CASE B Runtime.GetINativeObject<IUIInteraction> (protocol wrapper): managed type = UIKit.UIInteractionWrapper; dealloc ran = True; dealloc on main thread = False; managed thread id = 2
CASE C Runtime.GetNSObject (NSObject peer, control): managed type = Foundation.NSObject; dealloc ran = True; dealloc on main thread = True; managed thread id = 1
```

## Crash report

[`crash-report/`](crash-report) holds the crash report from a CommunityToolkit/Maui device-test run on a GitHub-hosted runner ([run 44805229808](https://github.com/CommunityToolkit/Maui/actions/runs/44805229808/job/136527353055), attempt 1). `-[_UIKeyboardBasedTextSelectionInteraction dealloc]` ran on the finalizer thread, called from `NativeObject.Dispose` through `DisposableObject.Finalize`.

Issue: dotnet/macios#TBD

Drafted by Brandon's Claude (AI) on Brandon's behalf.
