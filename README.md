# macios BaseWrapper finalizer-thread repro

When the GC finalizes a protocol wrapper (`ObjCRuntime.BaseWrapper`, such as `UIKit.UIInteractionWrapper`), the wrapper releases its native object on the finalizer thread. A finalized `NSObject` peer releases on the main thread instead. When that release is the last one, the native object deallocates off the main thread, which can crash UIKit.

The app registers an Objective-C class that adopts `UIInteraction` and has no managed type, so .NET can only expose it through a protocol wrapper. Each case drops all native references except the managed one, forces a GC and turns the main run loop:

- A: read through `UIView.Interactions` (wrapper)
- B: `Runtime.GetINativeObject<IUIInteraction>` (wrapper)
- C: `Runtime.GetNSObject` (`NSObject` peer, the control)

## Run it locally

Needs macOS with Xcode 27.2 and .NET SDK 11.0.100 with the iOS workload (workload set 11.0.100.1, as in CI).

```sh
dotnet build -c Release
UDID=$(xcrun simctl create BaseWrapperRepro "iPhone 16 Pro" com.apple.CoreSimulator.SimRuntime.iOS-18-6)
xcrun simctl boot $UDID
xcrun simctl install $UDID bin/Release/net11.0-ios/iossimulator-arm64/BaseWrapperFinalizerRepro.app
xcrun simctl launch --console-pty --terminate-running-process $UDID com.example.basewrapperrepro
```

Any iOS simulator runtime works, with a device type that it supports. Delete the simulator afterwards with `xcrun simctl delete $UDID`.

## Run it in CI

[`.github/workflows/repro.yml`](.github/workflows/repro.yml) runs on every push to `main` and on manual dispatch, on the GitHub-hosted `xcode-27` image. It builds with `-bl:msbuild.binlog`, runs the app on the image's newest general (not device-specific) iOS simulator runtime and uploads `msbuild.binlog` and `console.txt` as the `repro-logs` artifact. The job passes when all three cases ran, whatever their results.

## Expected and actual

Expected: the native object deallocates on the main thread in all three cases.

Actual: only the `NSObject` peer does. Output of a real run (iOS 18.6 simulator, Release):

```text
CASE A UIView.Interactions (protocol wrapper): managed type = UIKit.UIInteractionWrapper; dealloc ran = True; dealloc on main thread = False; managed thread id = 2
CASE B Runtime.GetINativeObject<IUIInteraction> (protocol wrapper): managed type = UIKit.UIInteractionWrapper; dealloc ran = True; dealloc on main thread = False; managed thread id = 2
CASE C Runtime.GetNSObject (NSObject peer, control): managed type = Foundation.NSObject; dealloc ran = True; dealloc on main thread = True; managed thread id = 1
```

## CI logs

[`logs/`](logs) holds `msbuild.binlog` and `console.txt` from [run 44853538436](https://github.com/TheCodeTraveler/macios-basewrapper-finalizer-repro/actions/runs/44853538436) (iOS 27.2 (24B8117) simulator, Microsoft.iOS 27.2.11039, Release). Cases A and B deallocate on the finalizer thread there too.

## Crash report

[`crash-report/`](crash-report) holds two files, copied without edits, from a CommunityToolkit/Maui device-test run on a GitHub-hosted runner ([run 44805229808](https://github.com/CommunityToolkit/Maui/actions/runs/44805229808/job/136527353055), attempt 1, artifact `device-tests-ios`). Thread 67642 is the CoreCLR finalizer thread in both.

- `CommunityToolkit.Maui.DeviceTests-2026-11-20-200538.ips`: the crash report. Thread 67642 aborts on an unhandled managed exception in `NativeObject.Dispose` ← `DisposableObject.Finalize` ← `GC.RunFinalizers`.
- `app-log-tail.txt`: the last 65 lines of the app log (`com.communitytoolkit.maui.devicetests.log`). Its native stack trace shows `-[_UIKeyboardBasedTextSelectionInteraction dealloc]` running on thread 67642, called from `NativeObject.Dispose`, where `-[NSMutableArray removeObject:]` throws `NSRangeException`.

The `.ips` lacks the UIKit frames because the Objective-C exception was converted to a managed one (`Mode: ThrowManagedException`), which unwound them.

Issue: dotnet/macios#TBD

Drafted by Brandon's Claude (AI) on Brandon's behalf.
