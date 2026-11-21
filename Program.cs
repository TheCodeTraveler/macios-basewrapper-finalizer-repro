using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Foundation;
using ObjCRuntime;
using UIKit;

namespace BaseWrapperFinalizerRepro;

public static class Program
{
	// Runs before UIApplication.Main, so no scene setup is needed; this is still the main thread.
	static void Main()
	{
		ReproInteraction.RegisterClass();

		RunCase("A UIView.Interactions (protocol wrapper)", ReadThroughUIViewInteractions);
		RunCase("B Runtime.GetINativeObject<IUIInteraction> (protocol wrapper)", CreateProtocolWrapper);
		RunCase("C Runtime.GetNSObject (NSObject peer, control)", CreateNSObjectPeer);
	}

	static void RunCase(string name, Func<IntPtr, UIView, string> createManagedReference)
	{
		ReproInteraction.ResetRecordedDealloc();
		var view = new UIView();
		var interaction = ReproInteraction.Create();

		// Raw messages, so the only managed object that refers to the interaction is the one the case creates.
		ObjC.SendVoid(view.Handle, "addInteraction:", interaction);
		var managedType = createManagedReference(interaction, view);
		ObjC.SendVoid(view.Handle, "removeInteraction:", interaction);
		ObjC.SendVoid(interaction, "release");

		// The case's managed object now holds the last reference, so its release deallocates the interaction.
		GC.Collect();
		GC.WaitForPendingFinalizers();

		// NSObject peers queue their release to the main thread, which needs a run loop turn.
		NSRunLoop.Main.RunUntil(NSDate.FromTimeIntervalSinceNow(0.5));

		Console.WriteLine($"CASE {name}: managed type = {managedType}; dealloc ran = {ReproInteraction.DeallocRan}; " +
			$"dealloc on main thread = {ReproInteraction.DeallocWasOnMainThread}; managed thread id = {ReproInteraction.DeallocManagedThreadId}");
		Console.WriteLine(ReproInteraction.DeallocStackTrace);
	}

	// NoInlining keeps each case's managed object out of RunCase's stack frame, so GC.Collect can collect it.
	[MethodImpl(MethodImplOptions.NoInlining)]
	static string ReadThroughUIViewInteractions(IntPtr interaction, UIView view)
	{
		// The getter's autoreleased NSArray would otherwise keep the interaction alive past the GC.
		using var autoreleasePool = new NSAutoreleasePool();
		return view.Interactions.Single(item => item.Handle == interaction).GetType().FullName!;
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static string CreateProtocolWrapper(IntPtr interaction, UIView view)
		=> Runtime.GetINativeObject<IUIInteraction>(interaction, false)!.GetType().FullName!;

	[MethodImpl(MethodImplOptions.NoInlining)]
	static string CreateNSObjectPeer(IntPtr interaction, UIView view)
		=> Runtime.GetNSObject(interaction)!.GetType().FullName!;
}

// No managed type is registered for this Objective-C class, so .NET can only expose it as IUIInteraction
// through a protocol wrapper, the same way it exposes UIKit's private interaction classes.
static unsafe class ReproInteraction
{
	static IntPtr nativeClass;

	public static bool DeallocRan { get; private set; }
	public static bool DeallocWasOnMainThread { get; private set; }
	public static int DeallocManagedThreadId { get; private set; }
	public static string DeallocStackTrace { get; private set; } = "";

	public static void ResetRecordedDealloc()
	{
		DeallocRan = false;
		DeallocWasOnMainThread = false;
		DeallocManagedThreadId = 0;
		DeallocStackTrace = "";
	}

	public static void RegisterClass()
	{
		nativeClass = ObjC.objc_allocateClassPair(Class.GetHandle("NSObject"), "ReproInteraction", 0);
		ObjC.class_addProtocol(nativeClass, ObjC.objc_getProtocol("UIInteraction"));
		ObjC.class_addMethod(nativeClass, Selector.GetHandle("view"), (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr>)&GetView, "@@:");
		ObjC.class_addMethod(nativeClass, Selector.GetHandle("willMoveToView:"), (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&MoveToView, "v@:@");
		ObjC.class_addMethod(nativeClass, Selector.GetHandle("didMoveToView:"), (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&MoveToView, "v@:@");
		ObjC.class_addMethod(nativeClass, Selector.GetHandle("dealloc"), (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, void>)&Dealloc, "v@:");
		ObjC.objc_registerClassPair(nativeClass);
	}

	public static IntPtr Create() => ObjC.SendIntPtr(ObjC.SendIntPtr(nativeClass, "alloc"), "init");

	[UnmanagedCallersOnly]
	static IntPtr GetView(IntPtr self, IntPtr selector) => IntPtr.Zero;

	[UnmanagedCallersOnly]
	static void MoveToView(IntPtr self, IntPtr selector, IntPtr view)
	{
	}

	[UnmanagedCallersOnly]
	static void Dealloc(IntPtr self, IntPtr selector)
	{
		DeallocRan = true;
		DeallocWasOnMainThread = NSThread.IsMain;
		DeallocManagedThreadId = Environment.CurrentManagedThreadId;
		DeallocStackTrace = Environment.StackTrace;

		var super = new ObjC.Super { Receiver = self, SuperClass = Class.GetHandle("NSObject") };
		ObjC.objc_msgSendSuper(&super, selector);
	}
}

static unsafe class ObjC
{
	const string LibObjC = "/usr/lib/libobjc.dylib";

	public struct Super
	{
		public IntPtr Receiver;
		public IntPtr SuperClass;
	}

	[DllImport(LibObjC)] public static extern IntPtr objc_allocateClassPair(IntPtr superclass, string name, nint extraBytes);
	[DllImport(LibObjC)] public static extern void objc_registerClassPair(IntPtr cls);
	[DllImport(LibObjC)] public static extern IntPtr objc_getProtocol(string name);
	[DllImport(LibObjC)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool class_addProtocol(IntPtr cls, IntPtr protocol);
	[DllImport(LibObjC)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool class_addMethod(IntPtr cls, IntPtr selector, IntPtr implementation, string types);
	[DllImport(LibObjC)] public static extern void objc_msgSendSuper(Super* super, IntPtr selector);

	[DllImport(LibObjC, EntryPoint = "objc_msgSend")] static extern IntPtr SendIntPtr(IntPtr receiver, IntPtr selector);
	[DllImport(LibObjC, EntryPoint = "objc_msgSend")] static extern void SendVoid(IntPtr receiver, IntPtr selector);
	[DllImport(LibObjC, EntryPoint = "objc_msgSend")] static extern void SendVoid(IntPtr receiver, IntPtr selector, IntPtr argument);

	public static IntPtr SendIntPtr(IntPtr receiver, string selector) => SendIntPtr(receiver, Selector.GetHandle(selector));
	public static void SendVoid(IntPtr receiver, string selector) => SendVoid(receiver, Selector.GetHandle(selector));
	public static void SendVoid(IntPtr receiver, string selector, IntPtr argument) => SendVoid(receiver, Selector.GetHandle(selector), argument);
}
